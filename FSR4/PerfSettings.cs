using System;
using MelonLoader;
using UnityEngine;

namespace FSR4
{
	internal static class PerfSettings
	{
		internal static MelonPreferences_Category Category;
		internal static MelonPreferences_Entry<bool> FpCameraIdle;
		internal static MelonPreferences_Entry<bool> FpCameraNoShadows;
		internal static MelonPreferences_Entry<bool> InstancerFilter;
		internal static MelonPreferences_Entry<bool> DistortionForward;
		internal static MelonPreferences_Entry<bool> MapCameraIdle;

		internal static void Init(HarmonyLib.Harmony h)
		{
			Category = MelonPreferences.CreateCategory("FSR4_Performance", "FSR 4 Upscaling: performance");
			FpCameraIdle = Category.CreateEntry("FpCameraIdle", true,"First-person camera off while it draws nothing",
				"The game's first-person camera (binoculars, hands) renders every frame even when it has nothing to draw. Off while the first-person figure is hidden (or its culling mask is empty), back the same frame it shows.");
			FpCameraNoShadows = Category.CreateEntry("FpCameraNoShadows", true,"First-person camera: no shadow map",
				"While the first-person camera renders, the sun's shadow map is not rendered a second time for it.");
			InstancerFilter = Category.CreateEntry("InstancerCameraFilter", false, "Vegetation instancer: only cameras that show it",
				"The game's grass and detail instancer prepares its meshes for every camera, also cameras that cannot show them (the first-person camera, the rain-distortion camera, the track-marks camera, FSR's presenter). Those are skipped.");
			DistortionForward = Category.CreateEntry("DistortionCameraForward", true, "Heat-haze camera: forward rendering",
				"The game's heat-haze / exhaust-shimmer / rain-distortion camera renders a full-screen deferred pass for objects that are all transparent. Forward rendering gives the same picture without that pass.");
			MapCameraIdle = Category.CreateEntry("MapCameraIdle", true, "Map camera off while the map is closed",
				"The game's map camera redraws the map every frame, also while the map screen is closed. Off while the map is not on screen, back the same frame it opens.");
			PerfInstancer.Patch(h);
			Application.onBeforeRender += BeforeRender;
		}

		static void BeforeRender()
		{
			try { PerfFpCamera.Tick(); }
			catch (Exception e) { PerfFpCamera.Fail(e); }
			try { PerfDistortion.Tick(); }
			catch (Exception e) { PerfDistortion.Fail(e); }
			try { PerfMapCamera.Tick(); }
			catch (Exception e) { PerfMapCamera.Fail(e); }
		}
	}
}
