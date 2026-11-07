using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace FSR4
{
	internal static class PerfMapCamera
	{
		static Camera _cam;
		static RenderTexture _target;
		static RawImage[] _viewers = new RawImage[0];
		static bool _offByUs, _failed;
		static int _nextSearch;

		internal static void Tick()
		{
			if (_failed) return;
			bool want = PerfSettings.MapCameraIdle.Value;
			if (_cam == null)
			{
				_offByUs = false;
				if (!want || Time.frameCount < _nextSearch) return;
				_nextSearch = Time.frameCount + 120;
				var map = UnityEngine.Object.FindObjectOfType<GHPC.UI.TopoMapCamera>();
				_cam = map != null ? map.GetComponent<Camera>() : null;
				_target = null;
				if (_cam == null) return;
			}
			if (!want) { Restore(); return; }
			if (_cam.targetTexture != _target)
			{
				_target = _cam.targetTexture;
				_viewers = _target != null ? Resources.FindObjectsOfTypeAll<RawImage>().Where(r => r != null && r.gameObject.scene.IsValid() && r.texture == _target).ToArray() : new RawImage[0];
			}
			if (_viewers.Length == 0) { Restore(); return; }
			bool shown = Shown();
			if (!shown && _cam.enabled)
			{
				_cam.enabled = false;
				_offByUs = true;
			}
			else if (shown && _offByUs)
			{
				_cam.enabled = true;
				_offByUs = false;
			}
		}

		static readonly List<Canvas> _canvases = new List<Canvas>();
		static readonly List<CanvasGroup> _groups = new List<CanvasGroup>();

		internal static bool Shown()
		{
			foreach (var v in _viewers)
			{
				if (v == null || !v.isActiveAndEnabled || v.texture != _target) continue;
				bool visible = true;
				foreach (var c in _canvases) if (!c.enabled) { visible = false; break; }
				if (visible) foreach (var g in _groups) if (g.enabled && g.alpha <= 0f) { visible = false; break; }
				if (visible) return true;
			}
			return false;
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
			MelonLogger.Warning($"{Mod.Name}: the map camera lever failed ({e.Message}); it stays off for this session.");
		}
	}
}
