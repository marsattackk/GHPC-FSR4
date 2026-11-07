using System;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace FSR4
{
	internal static class PerfDistortion
	{
		static Camera _cam;
		static RenderingPath _was;
		static bool _changed, _failed;
		static int _nextSearch;

		internal static void Tick()
		{
			if (_failed) return;
			bool want = PerfSettings.DistortionForward.Value;
			if (_cam == null)
			{
				_changed = false;
				if (!want || Time.frameCount < _nextSearch) return;
				_nextSearch = Time.frameCount + 120;
				_cam = Camera.allCameras.FirstOrDefault(c => c.name == "distortion_camera");
				if (_cam == null) return;
			}
			if (want && !_changed && _cam.actualRenderingPath == RenderingPath.DeferredShading)
			{
				_was = _cam.renderingPath;
				_cam.renderingPath = RenderingPath.Forward;
				_changed = true;
			}
			else if (!want) Restore();
		}

		internal static void Restore()
		{
			if (_changed && _cam != null) _cam.renderingPath = _was;
			_changed = false;
		}

		internal static void Fail(Exception e)
		{
			if (_failed) return;
			_failed = true;
			try { Restore(); } catch { }
			MelonLogger.Warning($"{Mod.Name}: the distortion camera lever failed ({e.Message}); it stays off for this session.");
		}
	}
}
