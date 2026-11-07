using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace FSR4
{
	internal static class PerfFpCamera
	{
		static Camera _cam;
		static bool _offByUs, _failed, _effectLogged, _hooked, _shadowCut;
		static int _nextSearch;
		static float _shadowWas;
		static int _cascadesWas;

		internal static void Tick()
		{
			if (_failed) return;
			bool want = PerfSettings.FpCameraIdle.Value;
			if (_cam == null)
			{
				_offByUs = false;
				if ((!want && !PerfSettings.FpCameraNoShadows.Value) || Time.frameCount < _nextSearch) return;
				_nextSearch = Time.frameCount + 120;
				_cam = Camera.allCameras.FirstOrDefault(c => c.name == "FP Camera");
				if (_cam == null) return;
				_effectLogged = false;
				_bufferCountChecked = -1;
			}
			Hook();
			if (!want) { Restore(); return; }
			bool idle = OnlyIgnorableBuffers(_cam) && !HasImageEffect() && (_cam.cullingMask == 0 || RigHidden());
			if (idle && _cam.enabled)
			{
				_cam.enabled = false;
				_offByUs = true;
			}
			else if (!idle && _offByUs)
			{
				_cam.enabled = true;
				_offByUs = false;
			}
		}

		internal static bool RigHidden()
		{
			var rig = GHPC.Camera.FirstPersonBinocularsAnimator.Instance;
			if (rig == null) return false;
			if (rig.Models != null)
				foreach (var m in rig.Models)
					if (m.Model != null && m.Model.activeInHierarchy) return false;
			if (rig.HandModels != null)
				foreach (var h in rig.HandModels)
					if (h != null && h.activeInHierarchy) return false;
			return true;
		}

		const string DecalBuffer = "Easy Decals";
		static int _bufferCountChecked = -1;
		static bool _buffersIgnorable;

		static bool OnlyIgnorableBuffers(Camera c)
		{
			int n = c.commandBufferCount;
			if (n == 0) return true;
			if (n == _bufferCountChecked) return _buffersIgnorable;
			_bufferCountChecked = n;
			int decal = 0;
			if (c.actualRenderingPath == RenderingPath.Forward)
				foreach (var b in c.GetCommandBuffers(UnityEngine.Rendering.CameraEvent.BeforeReflections))
					if (b.name == DecalBuffer) decal++;
			_buffersIgnorable = decal == n;
			return _buffersIgnorable;
		}

		static readonly List<MonoBehaviour> _behaviours = new List<MonoBehaviour>();
		static readonly Dictionary<Type, bool> _hasOnRenderImage = new Dictionary<Type, bool>();

		static bool HasImageEffect()
		{
			foreach (var b in _behaviours)
			{
				if (b == null || !b.enabled) continue;
				var t = b.GetType();
				if (!_hasOnRenderImage.TryGetValue(t, out bool has))
					_hasOnRenderImage[t] = has = t.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
				if (!has) continue;
				if (!_effectLogged) { _effectLogged = true; MelonLogger.Msg($"{Mod.Name}: the first-person camera has an image effect ({b.GetType().Name}); it is left on."); }
				return true;
			}
			return false;
		}

		static void Hook()
		{
			if (_hooked) return;
			_hooked = true;
			Camera.onPreCull += PreCull;
			Camera.onPostRender += PostRender;
		}

		static void PreCull(Camera c)
		{
			if (c != _cam || _failed || !PerfSettings.FpCameraNoShadows.Value) return;
			_shadowWas = QualitySettings.shadowDistance;
			// shadowCascades gets saved and put back but never changed, only shadowDistance is. Leftover, does no harm.
			_cascadesWas = QualitySettings.shadowCascades;
			QualitySettings.shadowDistance = 0f;
			_shadowCut = true;
		}

		static void PostRender(Camera c)
		{
			if (c != _cam || !_shadowCut) return;
			QualitySettings.shadowDistance = _shadowWas;
			QualitySettings.shadowCascades = _cascadesWas;
			_shadowCut = false;
		}

		internal static void Restore()
		{
			if (_offByUs && _cam != null) _cam.enabled = true;
			_offByUs = false;
		}

		internal static void Fail(Exception e)
		{
			if (_failed) return;
			_failed = true;
			try { Restore(); } catch { }
			if (_shadowCut) { QualitySettings.shadowDistance = _shadowWas; QualitySettings.shadowCascades = _cascadesWas; _shadowCut = false; }
			MelonLogger.Warning($"{Mod.Name}: the first-person camera lever failed ({e.Message}); it stays off for this session.");
		}
	}
}
