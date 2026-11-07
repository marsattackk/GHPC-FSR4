using System;
using GHPC.Camera;
using HarmonyLib;
using MelonLoader;
using UnityEngine.SceneManagement;

namespace FSR4
{
	internal static class HistoryResets
	{
		static bool _hooked;
		static CameraManager _cm;
		static float _fov = -1f;

		internal static void Patch(HarmonyLib.Harmony h)
		{
			try
			{
				var t = typeof(GHPC.Camera.BufferedCameraFollow);
				var post = new HarmonyMethod(typeof(HistoryResets), nameof(FollowChanged));
				foreach (var m in new[] { "ToggleFreeFlyMode", "RestoreCamera", "SuspendCamera" })
				{
					var mi = AccessTools.Method(t, m);
					if (mi != null) h.Patch(mi, postfix: post);
					else MelonLogger.Warning($"{Mod.Name}: BufferedCameraFollow.{m} not found; FSR's history is not reset when it runs.");
				}
			}
			catch (Exception e) { MelonLogger.Warning($"{Mod.Name}: patching the camera follow failed ({e.Message}); its changes reset FSR's history only through the fallback."); }
		}

		static void FollowChanged(System.Reflection.MethodBase __originalMethod) => Request();

		internal static void Tick()
		{
			if (_hooked && _cm == CameraManager.Instance) return;
			Unhook();
			_hooked = true;
			_cm = CameraManager.Instance;
			CameraSlot.ActiveInstanceChanged += SlotChanged;
			SceneManager.sceneLoaded += SceneLoaded;
			if (_cm != null)
			{
				_cm.FoVChange += FovChanged;
				_cm.LightModeChanged += LightChanged;
				_cm.ExteriorModeChanged += ExteriorChanged;
				_cm.UnitChangeComplete += UnitChanged;
				var main = CameraManager.MainCam;
				_fov = main != null ? main.fieldOfView : -1f;
			}
		}

		static void Unhook()
		{
			if (!_hooked) return;
			_hooked = false;
			CameraSlot.ActiveInstanceChanged -= SlotChanged;
			SceneManager.sceneLoaded -= SceneLoaded;
			if (_cm != null)
			{
				_cm.FoVChange -= FovChanged;
				_cm.LightModeChanged -= LightChanged;
				_cm.ExteriorModeChanged -= ExteriorChanged;
				_cm.UnitChangeComplete -= UnitChanged;
			}
			_cm = null;
		}

		static void Request()
		{
			var u = Upscaler.Current;
			if (u != null && u.Running) u.RequestReset();
		}

		static void SlotChanged(CameraSlot s) => Request();
		static void LightChanged(GHPC.Equipment.Optics.NightVisionType m) => Request();
		static void ExteriorChanged(bool was, bool now) => Request();
		static void UnitChanged() => Request();
		static void SceneLoaded(Scene s, LoadSceneMode m) { Request(); Unhook(); }

		static void FovChanged(float fov)
		{
			float was = _fov;
			_fov = fov;
			if (was > 0f && Math.Abs(fov - was) > was * 0.05f) Request();
		}
	}
}
