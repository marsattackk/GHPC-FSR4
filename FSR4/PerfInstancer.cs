using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace FSR4
{
	internal static class PerfInstancer
	{
		static FieldInfo _list, _dict, _config;
		static PropertyInfo _layers, _kvValue;
		static bool _ready, _failed;
		static int _layersFrame = -1, _layersCache;

		internal static void Patch(HarmonyLib.Harmony h)
		{
			try
			{
				var t = AccessTools.TypeByName("GHPC.Rendering.Instancing.InstancedMeshRenderer");
				var groupType = AccessTools.TypeByName("GHPC.Rendering.Instancing.MeshGroup");
				var configType = AccessTools.TypeByName("GHPC.Rendering.Instancing.MeshGroupConfig");
				var get = t != null ? AccessTools.Method(t, "GetPreCollectCameras") : null;
				var pre = t != null ? AccessTools.Method(t, "OnCameraPreCull", new[] { typeof(Camera) }) : null;
				_list = t != null ? AccessTools.Field(t, "preCollectCameraList") : null;
				_dict = t != null ? AccessTools.Field(t, "instancedMeshGroupDictionary") : null;
				_config = groupType != null ? AccessTools.Field(groupType, "config") : null;
				_layers = configType != null ? AccessTools.Property(configType, "RenderLayers") : null;
				if (get == null || pre == null || _list == null || _dict == null || _config == null || _layers == null || _list.FieldType != typeof(List<Camera>))
				{
					MelonLogger.Msg($"{Mod.Name}: the vegetation instancer's camera filter is off: the game's instancer has changed (found: list {_list != null}, groups {_dict != null}, config {_config != null}, layers {_layers != null}, methods {get != null}/{pre != null}).");
					return;
				}
				h.Patch(get, postfix: new HarmonyMethod(typeof(PerfInstancer), nameof(AfterCollectCameras)));
				h.Patch(pre, prefix: new HarmonyMethod(typeof(PerfInstancer), nameof(BeforePreCull)));
				_ready = true;
			}
			catch (Exception e) { MelonLogger.Msg($"{Mod.Name}: the vegetation instancer's camera filter is off ({e.Message})."); }
		}

		static bool On => _ready && !_failed && PerfSettings.InstancerFilter.Value;

		static int Layers(object renderer)
		{
			if (_layersFrame == Time.frameCount) return _layersCache;
			_layersFrame = Time.frameCount;
			int mask = 0;
			if (_dict.GetValue(renderer) is IEnumerable groups)
				foreach (var kv in groups)
				{
					if (kv == null) continue;
					if (_kvValue == null || _kvValue.DeclaringType != kv.GetType()) _kvValue = kv.GetType().GetProperty("Value");
					var group = _kvValue?.GetValue(kv, null);
					var config = group != null ? _config.GetValue(group) : null;
					if (config != null) mask |= (int)_layers.GetValue(config, null);
				}
			return _layersCache = mask;
		}

		static bool Hidden(Camera c, int layers) => c != null && layers != 0 && (c.cullingMask & layers) == 0;

		static void AfterCollectCameras(object __instance)
		{
			if (!On) return;
			try
			{
				int layers = Layers(__instance);
				var list = (List<Camera>)_list.GetValue(__instance);
				list.RemoveAll(c => Hidden(c, layers));
			}
			catch (Exception e) { Fail(e); }
		}

		static bool BeforePreCull(object __instance, Camera cam)
		{
			if (!On) return true;
			try { return !Hidden(cam, Layers(__instance)); }
			catch (Exception e) { Fail(e); return true; }
		}

		static void Fail(Exception e)
		{
			if (_failed) return;
			_failed = true;
			MelonLogger.Warning($"{Mod.Name}: the vegetation instancer's camera filter failed ({e.Message}); it stays off for this session.");
		}
	}
}
