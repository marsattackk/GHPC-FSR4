using System;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace FSR4
{
	internal sealed class Upscaler : MonoBehaviour
	{
		internal static Upscaler Current;
		internal int RenderW, RenderH, DisplayW, DisplayH;
		internal bool Running => _color != null;
		internal Camera Cam => _cam;

		Camera _cam, _presenter;
		PostProcessLayer _pp;
		PostProcessLayer.Antialiasing _aaWas;
		bool _aaTaken;
		DepthTextureMode _depthModeWas;
		RenderTexture _color, _depth, _motion, _output, _final, _displayTarget, _targetWas;
		bool _displayTargetSet, _forceWas;
		IntPtr _colorPtr, _depthPtr, _motionPtr, _outputPtr, _slotFrame;
		readonly IntPtr[] _frames = new IntPtr[3];
		int _frame, _phase, _jitterIndex, _finalFrame = -1;
		float _jx, _jy;
		bool _reset = true;
		Vector3 _lastPos;
		float _lastFov;
		Rect _rectWas;
		bool _rectSet;
		Native.FrameData _fd;
		internal void RequestReset() { _reset = true; }

		const uint DynamicResolution = 1 << 6;

		internal static BuiltinRenderTextureType DepthSource = BuiltinRenderTextureType.ResolvedDepth;

		void Awake()
		{
			_cam = GetComponent<Camera>();
			_pp = GetComponent<PostProcessLayer>();
			for (int i = 0; i < _frames.Length; i++) _frames[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.FrameData)));
			Current = this;
		}

		void OnDestroy()
		{
			Stop();
			for (int i = 0; i < _frames.Length; i++) if (_frames[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_frames[i]); _frames[i] = IntPtr.Zero; }
			if (Current == this) Current = null;
		}

		void OnDisable() => Stop();

		void LateUpdate()
		{
			if (!Mod.Active || !Native.Ready || Native.Failed || !TaaSlot.Usable || _pp == null) { if (Running) Stop(); return; }
			if (_rectSet) RestoreRect();
			if (Running) Watchdog();
			if (!TaaSlot.Usable) { Stop(); return; }
			var flir = GHPC.Thermals.FLIRCamera.Instance;
			if (flir != null && flir.isActiveAndEnabled)
			{
				if (Running) { Stop(); MelonLogger.Msg($"{Mod.Name}: thermal sight on: FSR off until it closes."); }
				return;
			}
			bool dyn = AutoScale.Dynamic;
			if (dyn && Running && _dynamic) AutoScale.Tick();
			float ratio = dyn ? AutoScale.Ratio : Mod.Ratio;
			int dw = Screen.width, dh = Screen.height;
			float allocRatio = dyn ? AutoScale.MinRatio : ratio;
			int aw = Mathf.Max(1, Mathf.RoundToInt(dw / allocRatio)), ah = Mathf.Max(1, Mathf.RoundToInt(dh / allocRatio));
			int rw = Mathf.Min(aw, Mathf.Max(1, Mathf.RoundToInt(dw / ratio))), rh = Mathf.Min(ah, Mathf.Max(1, Mathf.RoundToInt(dh / ratio)));
			if (!Running || dw != DisplayW || dh != DisplayH || aw != AllocW || ah != AllocH || dyn != _dynamic) Begin(dw, dh, aw, ah, rw, rh, ratio, dyn);
			if (rw != RenderW || rh != RenderH) SetRenderSize(rw, rh);
			BindViews();
			MipBias.Tick(MipRenderW(), DisplayW);
			HistoryResets.Tick();
		}

		void SetRenderSize(int rw, int rh)
		{
			RenderW = rw; RenderH = rh;
			float r = DisplayW / (float)rw;
			_phase = Mathf.Max(1, (int)(8f * r * r));
			_jitterIndex %= _phase;
		}

		float _mipRatio = -1f, _mipCandidate = -1f, _mipSince;
		int MipRenderW()
		{
			if (!_dynamic) return RenderW;
			float r = DisplayW / (float)RenderW;
			if (_mipRatio < 0f) _mipRatio = r;
			if (Mathf.Abs(r - _mipCandidate) > 0.001f) { _mipCandidate = r; _mipSince = Time.unscaledTime; }
			if (Mathf.Abs(r - _mipRatio) >= 0.15f && Time.unscaledTime - _mipSince >= 2f) _mipRatio = r;
			return Mathf.Max(1, Mathf.RoundToInt(DisplayW / _mipRatio));
		}

		internal int AllocW, AllocH;
		bool _dynamic;

		void Begin(int dw, int dh, int aw, int ah, int rw, int rh, float ratio, bool dyn)
		{
			Stop();
			_dynamic = dyn;
			_mipRatio = _mipCandidate = -1f;
			DisplayW = dw; DisplayH = dh; AllocW = aw; AllocH = ah;
			_color = Target(aw, ah, 0, RenderTextureFormat.ARGBHalf, "FSR4 color");
			_depth = Target(aw, ah, 0, RenderTextureFormat.RFloat, "FSR4 depth");
			_motion = Target(aw, ah, 0, RenderTextureFormat.RGHalf, "FSR4 motion");
			_output = Target(dw, dh, 0, RenderTextureFormat.ARGBHalf, "FSR4 output");
			_colorPtr = _color.GetNativeTexturePtr();
			_depthPtr = _depth.GetNativeTexturePtr();
			_motionPtr = _motion.GetNativeTexturePtr();
			_outputPtr = _output.GetNativeTexturePtr();
			SetRenderSize(rw, rh);
			_depthModeWas = _cam.depthTextureMode;
			_cam.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
			_reset = true;
			_armed = _upscales = 0;
			_bridgeFrames = Native.GetStats().Frames;
			if (dyn || aw != dw || ah != dh)
			{
				_final = new RenderTexture(dw, dh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "FSR4 final", useMipMap = false, antiAliasing = 1 };
				_final.Create();
				bool hdr = _cam.allowHDR;
				var format = hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
				_displayTarget = new RenderTexture(dw, dh, 0, format, format == RenderTextureFormat.ARGB32 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear) { name = "FSR4 display target", useMipMap = false, antiAliasing = 1 };
				_displayTarget.Create();
				_targetWas = _cam.targetTexture;
				_forceWas = _cam.forceIntoRenderTexture;
				_cam.targetTexture = _displayTarget;
				_cam.forceIntoRenderTexture = true;
				_displayTargetSet = true;
				MakePresenter();
			}
			MelonLogger.Msg($"{Mod.Name}: on, {Mod.Quality.Value} ({ratio:0.##}x){(dyn ? $", dynamic within {AutoScale.MinRatio:0.##}x-{AutoScale.MaxRatio:0.##}x, textures {aw}x{ah}" : "")}: rendering {rw}x{rh}, upscaled to {dw}x{dh}; jitter phases {_phase}; main camera '{name}' (path {_cam.actualRenderingPath}, HDR {_cam.allowHDR}), anti-aliasing {_pp.antialiasingMode} until FSR goes off.");
		}

		void MakePresenter()
		{
			var go = new GameObject("FSR4 presenter") { hideFlags = HideFlags.HideAndDontSave };
			go.transform.SetParent(transform, false);
			_presenter = go.AddComponent<Camera>();
			_presenter.clearFlags = CameraClearFlags.SolidColor;
			_presenter.backgroundColor = Color.black;
			_presenter.cullingMask = 0;
			_presenter.depth = _cam.depth + 0.001f;
			_presenter.useOcclusionCulling = false;
			_presenter.allowHDR = false;
			_presenter.allowMSAA = false;
			_presenter.renderingPath = RenderingPath.Forward;
			go.AddComponent<PresentBlit>().Source = _final;
		}

		Texture2D _extColor, _extMotion, _extOutput;
		ulong _extGen, _staleGen;
		static readonly int DirectOffset = (int)Marshal.OffsetOf(typeof(Native.FrameData), nameof(Native.FrameData.Direct));

		void BindViews()
		{
			ulong gen = Native.FSR4_GetViews(out IntPtr c, out IntPtr m, out IntPtr o);
			if (gen == _staleGen) gen = 0;
			if (gen != _extGen)
			{
				DropViews();
				if (gen != 0)
				{
					_extColor = Texture2D.CreateExternalTexture(AllocW, AllocH, TextureFormat.RGBAHalf, false, true, c);
					_extMotion = Texture2D.CreateExternalTexture(AllocW, AllocH, TextureFormat.RGHalf, false, true, m);
					_extOutput = Texture2D.CreateExternalTexture(DisplayW, DisplayH, TextureFormat.RGBAHalf, false, true, o);
					_extOutput.filterMode = FilterMode.Bilinear;
					_extGen = gen;
				}
			}
			Native.FSR4_ViewsInUse(_extGen);
		}

		void DropViews()
		{
			foreach (var t in new[] { _extColor, _extMotion, _extOutput }) if (t != null) Destroy(t);
			_extColor = _extMotion = _extOutput = null;
			_extGen = 0;
		}

		static RenderTexture Target(int w, int h, int depth, RenderTextureFormat f, string name)
		{
			var rt = new RenderTexture(w, h, depth, f, RenderTextureReadWrite.Linear) { name = name, useMipMap = false, autoGenerateMips = false, antiAliasing = 1, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
			rt.Create();
			return rt;
		}

		internal void Stop()
		{
			if (_cam != null)
			{
				if (_color != null) _cam.depthTextureMode = _depthModeWas;
				_cam.ResetProjectionMatrix();
			}
			if (_pp != null && _aaTaken) { _pp.antialiasingMode = _aaWas; _aaTaken = false; }
			MipBias.Restore();
			if (_presenter != null) Destroy(_presenter.gameObject);
			_presenter = null;
			RestoreRect();
			if (_displayTargetSet && _cam != null) { _cam.targetTexture = _targetWas; _cam.forceIntoRenderTexture = _forceWas; _displayTargetSet = false; }
			bool had = _color != null;
			DropViews();
			if (Native.Ready) { _staleGen = Native.FSR4_GetViews(out _, out _, out _); Native.FSR4_ViewsInUse(0); }
			foreach (var rt in new[] { _color, _depth, _motion, _output, _final, _displayTarget }) if (rt != null) { rt.Release(); Destroy(rt); }
			_color = _depth = _motion = _output = _final = _displayTarget = null;
			if (had && Native.Ready) GL.IssuePluginEvent(Native.RenderEvent, 2);
			if (had) MelonLogger.Msg($"{Mod.Name}: off (the game's own anti-aliasing back).");
		}

		internal void SlotPreCull(PostProcessLayer pp)
		{
			if (pp.antialiasingMode != PostProcessLayer.Antialiasing.TemporalAntialiasing)
			{
				_aaWas = pp.antialiasingMode;
				_aaTaken = true;
				pp.antialiasingMode = PostProcessLayer.Antialiasing.TemporalAntialiasing;
			}
			NextJitter(out _jx, out _jy);
			_slotFrame = FillFrame(_jx, _jy);
			_armed++;
			if (RenderW != DisplayW || RenderH != DisplayH)
			{
				var cam = pp.GetComponent<Camera>();
				_rectWas = cam.rect;
				_rectSet = true;
				cam.aspect = DisplayW / (float)DisplayH;
				cam.rect = new Rect(_rectWas.x, _rectWas.y, _rectWas.width * RenderW / DisplayW, _rectWas.height * RenderH / DisplayH);
			}
		}

		internal void RestoreRect()
		{
			if (!_rectSet || _cam == null) return;
			_rectSet = false;
			_cam.rect = _rectWas;
			_cam.ResetAspect();
		}

		internal void SlotDestination(PostProcessRenderContext context)
		{
			if (_final == null || !_displayTargetSet) return;
			context.destination = _final;
			_finalFrame = Time.frameCount;
		}

		internal RenderTexture FinalThisFrame => _final != null && _finalFrame == Time.frameCount ? _final : null;

		internal void SlotJitter(PostProcessRenderContext context)
		{
			var cam = context.camera;
			cam.nonJitteredProjectionMatrix = cam.projectionMatrix;
			cam.projectionMatrix = Jittered(cam.projectionMatrix, _jx, _jy);
			cam.useJitteredProjectionMatrixForTransparentRendering = true;
			var taaJitter = new Vector2(-_jx * Mod.JitterSignX / RenderW, -_jy * Mod.JitterSignY / RenderH);
			TaaSlot.SetTaaJitter(context.temporalAntialiasing, taaJitter);
			if (RenderW != DisplayW || RenderH != DisplayH) TaaSlot.SetRenderSize(context, DisplayW, DisplayH);
		}

		internal void SlotUpscale(PostProcessRenderContext context)
		{
			var cmd = context.command;
			_upscales++;
			cmd.BeginSample("FSR4");
			uint direct = 0;
			bool views = _extGen != 0;
			bool part = RenderW != AllocW || RenderH != AllocH;
			var vp = new Rect(0, 0, RenderW, RenderH);
			if (views && context.sourceFormat == RenderTextureFormat.ARGBHalf)
			{
				if (part) cmd.CopyTexture(context.source, 0, 0, 0, 0, RenderW, RenderH, _extColor, 0, 0, 0, 0);
				else cmd.CopyTexture(context.source, _extColor);
				direct |= Native.DirectColor;
			}
			else if (part) cmd.BlitFullscreenTriangle(context.source, _color, false, vp);
			else cmd.Blit(context.source, _color);
			if (part) cmd.BlitFullscreenTriangle(DepthSource, _depth, false, vp);
			else cmd.Blit(DepthSource, _depth);
			if (views)
			{
				if (part) cmd.CopyTexture(BuiltinRenderTextureType.MotionVectors, 0, 0, 0, 0, RenderW, RenderH, _extMotion, 0, 0, 0, 0);
				else cmd.CopyTexture(BuiltinRenderTextureType.MotionVectors, _extMotion);
				direct |= Native.DirectMotion;
			}
			else if (part) cmd.BlitFullscreenTriangle(BuiltinRenderTextureType.MotionVectors, _motion, false, vp);
			else cmd.Blit(BuiltinRenderTextureType.MotionVectors, _motion);
			if (views) direct |= Native.DirectOutput;
			Marshal.WriteInt32(_slotFrame, DirectOffset, (int)direct);
			cmd.IssuePluginEventAndData(Native.RenderEvent, 1, _slotFrame);
			cmd.BlitFullscreenTriangle(views ? (RenderTargetIdentifier)_extOutput : _output, context.destination);
			cmd.EndSample("FSR4");
		}

		int _armed, _upscales;
		ulong _bridgeFrames;

		void Watchdog()
		{
			if (_armed >= 60 && _upscales == 0) { TaaSlot.Disable($"FSR was on in the game's TAA slot for {_armed} frames and upscaled none"); return; }
			if (_armed >= 120)
			{
				ulong frames = Native.GetStats().Frames;
				if (frames == _bridgeFrames) { TaaSlot.Disable($"the upscaler's bridge took no frame in {_armed} frames ({_upscales} upscales recorded)"); return; }
				_bridgeFrames = frames;
				_armed = _upscales = 0;
			}
		}

		void NextJitter(out float jx, out float jy)
		{
			Vector3 pos = transform.position;
			float fov = _cam.fieldOfView;
			float jump = (pos - _lastPos).magnitude;
			if (!_reset && (jump > 25f || Mathf.Abs(fov - _lastFov) > _lastFov * 0.25f))
			{
				_reset = true;
				MelonLogger.Msg($"{Mod.Name}: history reset by the fallback, no game event announced it: camera moved {jump:0.#} m, field of view {_lastFov:0.##} -> {fov:0.##} deg.");
			}
			_lastPos = pos; _lastFov = fov;
			_jitterIndex = (_jitterIndex + 1) % _phase;
			jx = Halton(_jitterIndex + 1, 2) - 0.5f; jy = Halton(_jitterIndex + 1, 3) - 0.5f;
		}

		Matrix4x4 Jittered(Matrix4x4 p, float jx, float jy) =>
			Matrix4x4.Translate(new Vector3(2f * jx * Mod.JitterSignX / RenderW, 2f * jy * Mod.JitterSignY / RenderH, 0f)) * p;

		IntPtr FillFrame(float jx, float jy)
		{
			bool gamma = QualitySettings.activeColorSpace == ColorSpace.Gamma;
			_fd.Color = _colorPtr; _fd.Depth = _depthPtr; _fd.Motion = _motionPtr; _fd.Output = _outputPtr;
			_fd.RenderW = (uint)RenderW; _fd.RenderH = (uint)RenderH; _fd.DisplayW = (uint)DisplayW; _fd.DisplayH = (uint)DisplayH;
			_fd.JitterX = jx; _fd.JitterY = jy;
			_fd.MvScaleX = -RenderW * Mod.MotionSignX; _fd.MvScaleY = -RenderH * Mod.MotionSignY;
			_fd.FrameTimeMs = Mathf.Max(0.1f, Time.unscaledDeltaTime * 1000f);
			_fd.CameraNear = _cam.nearClipPlane; _fd.CameraFar = _cam.farClipPlane;
			_fd.FovV = _cam.fieldOfView * Mathf.Deg2Rad;
			_fd.Sharpness = Mathf.Clamp01(Mod.Sharpness.Value);
			_fd.Reset = _reset ? 1 : 0;
			_fd.CreateFlags = Native.AutoExposure | (SystemInfo.usesReversedZBuffer ? Native.DepthInverted : 0) | (gamma ? Native.NonLinearColorspace : 0);
			if (_cam.allowHDR) _fd.CreateFlags |= Native.HighDynamicRange;
			_fd.MaxRenderW = (uint)AllocW; _fd.MaxRenderH = (uint)AllocH;
			if (_dynamic) _fd.CreateFlags |= DynamicResolution;
			_fd.DispatchFlags = gamma ? Native.NonLinearSrgb : 0;
			_fd.Exposure = Native.ExposureWanted ? GameExposure.Value(_pp) : 0f;
			_fd.Direct = 0;
			IntPtr slot = _frames[_frame++ % _frames.Length];
			Marshal.StructureToPtr(_fd, slot, false);
			_reset = false;
			return slot;
		}

		sealed class PresentBlit : MonoBehaviour
		{
			internal RenderTexture Source;

			void OnRenderImage(RenderTexture src, RenderTexture dst)
			{
				GL.IssuePluginEvent(Native.RenderEvent, 3);
				Graphics.Blit(Source != null ? Source : src, dst);
			}
		}

		static float Halton(int index, int b)
		{
			float f = 1f, r = 0f;
			for (int i = index; i > 0; i /= b) { f /= b; r += f * (i % b); }
			return r;
		}
	}
}
