using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace FSR4
{
	internal static class TaaSlot
	{
		internal static bool Ready { get; private set; }

		static bool _broken;
		internal static bool Usable => Ready && !_broken;

		static readonly MethodInfo ConfigureJitter = AccessTools.Method(typeof(TemporalAntialiasing), nameof(TemporalAntialiasing.ConfigureJitteredProjectionMatrix));
		static readonly MethodInfo TaaRenderMethod = AccessTools.Method(typeof(TemporalAntialiasing), "Render", new[] { typeof(PostProcessRenderContext) });
		static readonly Action<TemporalAntialiasing, PostProcessRenderContext> TaaRender =
			TaaRenderMethod != null ? AccessTools.MethodDelegate<Action<TemporalAntialiasing, PostProcessRenderContext>>(TaaRenderMethod) : null;

		internal static void Patch(HarmonyLib.Harmony h)
		{
			try
			{
				if (ConfigureJitter == null || TaaRender == null) { MelonLogger.Warning($"{Mod.Name}: the game's TAA methods were not found; FSR stays off."); return; }
				HistoryResets.Patch(h);
				var render = AccessTools.Method(typeof(PostProcessLayer), nameof(PostProcessLayer.Render));
				h.Patch(render, prefix: new HarmonyMethod(typeof(TaaSlot), nameof(RenderPrefix)), transpiler: new HarmonyMethod(typeof(TaaSlot), nameof(Transpile)));
				h.Patch(AccessTools.Method(typeof(PostProcessLayer), "OnPreCull"), prefix: new HarmonyMethod(typeof(TaaSlot), nameof(PreCull)));
				h.Patch(AccessTools.Method(typeof(PostProcessLayer), "OnPostRender"), prefix: new HarmonyMethod(typeof(TaaSlot), nameof(PostRender)));
				h.Patch(AccessTools.Method(typeof(PostProcessLayer), "OnRenderImage"), prefix: new HarmonyMethod(typeof(TaaSlot), nameof(RenderImage)));
				int jitter = 0, upscale = 0, originals = 0;
				foreach (var ci in PatchProcessor.GetCurrentInstructions(render))
				{
					if (!(ci.operand is MethodInfo m)) continue;
					if (m == JitterHook) jitter++;
					else if (m == UpscaleHook) upscale++;
					else if (m == ConfigureJitter || m == TaaRenderMethod) originals++;
				}
				Ready = jitter == 1 && upscale == 1 && originals == 0 && SetWidth != null && SetHeight != null && SetScreenWidth != null && SetScreenHeight != null && SourceDescriptor != null;
				MelonLogger.Msg(Ready
					? $"{Mod.Name}: FSR sits in the game's TAA slot (PostProcessLayer.Render as patched calls FSR's jitter {jitter}x and upscale {upscale}x, the game's TAA calls {originals}x)."
					: $"{Mod.Name}: the game's post-processing has changed (call sites found: jitter {_jitterSites}, TAA {_taaSites}, each wanted once; patched Render calls FSR's jitter {jitter}x, upscale {upscale}x, the game's TAA {originals}x); FSR stays off.");
			}
			catch (Exception e)
			{
				Ready = false;
				MelonLogger.Warning($"{Mod.Name}: patching the game's post-processing failed ({e.Message}); FSR stays off.");
			}
		}

		static int _jitterSites, _taaSites;
		static bool _firstUpscale;

		static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
		{
			var code = new List<CodeInstruction>(instructions);
			int jitterAt = -1, taaAt = -1;
			_jitterSites = _taaSites = 0;
			for (int i = 0; i < code.Count; i++)
			{
				if (!(code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) || !(code[i].operand is MethodInfo m)) continue;
				if (m == JitterHook || m == UpscaleHook) return code;
				if (m == ConfigureJitter) { _jitterSites++; jitterAt = i; }
				else if (m == TaaRenderMethod) { _taaSites++; taaAt = i; }
			}
			if (_jitterSites != 1 || _taaSites != 1) return code;
			code[jitterAt] = new CodeInstruction(OpCodes.Call, JitterHook).MoveLabelsFrom(code[jitterAt]);
			code[taaAt] = new CodeInstruction(OpCodes.Call, UpscaleHook).MoveLabelsFrom(code[taaAt]);
			return code;
		}

		static readonly MethodInfo JitterHook = AccessTools.Method(typeof(TaaSlot), nameof(Jitter));
		static readonly MethodInfo UpscaleHook = AccessTools.Method(typeof(TaaSlot), nameof(Upscale));

		internal static void Disable(string why)
		{
			if (_broken) return;
			_broken = true;
			MelonLogger.Warning($"{Mod.Name}: {why}; FSR off for this session, the game's own anti-aliasing back.");
		}

		static Upscaler Owner(Camera cam)
		{
			if (!Usable) return null;
			var u = Upscaler.Current;
			return u != null && u.Running && u.Cam == cam ? u : null;
		}

		static void PreCull(PostProcessLayer __instance)
		{
			try
			{
				var u = Owner(__instance.GetComponent<Camera>());
				if (u != null) u.SlotPreCull(__instance);
			}
			catch (Exception e) { Break("OnPreCull", e); }
		}

		static void Jitter(TemporalAntialiasing taa, PostProcessRenderContext context)
		{
			Upscaler u = null;
			try { u = Owner(context.camera); if (u != null) { u.SlotJitter(context); return; } }
			catch (Exception e) { Break("jitter", e); }
			taa.ConfigureJitteredProjectionMatrix(context);
		}

		static void Upscale(TemporalAntialiasing taa, PostProcessRenderContext context)
		{
			Upscaler u = null;
			try
			{
				u = Owner(context.camera);
				if (u != null)
				{
					if (!_firstUpscale) { _firstUpscale = true; MelonLogger.Msg($"{Mod.Name}: first frame upscaled in the TAA slot ({context.camera.name}, source {u.RenderW}x{u.RenderH}, stack after it at {context.width}x{context.height})."); }
					u.SlotUpscale(context);
					return;
				}
			}
			catch (Exception e) { Break("upscale", e); }
			TaaRender(taa, context);
		}

		static void RenderPrefix(PostProcessLayer __instance, PostProcessRenderContext context)
		{
			try { Owner(context.camera)?.SlotDestination(context); }
			catch (Exception e) { Break("final target", e); }
		}

		static void PostRender(PostProcessLayer __instance)
		{
			try
			{
				var u = Upscaler.Current;
				if (u != null && u.Cam == __instance.GetComponent<Camera>()) u.RestoreRect();
			}
			catch (Exception e) { Break("viewport", e); }
		}

		static bool RenderImage(PostProcessLayer __instance, RenderTexture src, RenderTexture dst)
		{
			try
			{
				var u = Owner(__instance.GetComponent<Camera>());
				return u?.FinalThisFrame == null;
			}
			catch (Exception e) { Break("present", e); return true; }
		}

		static readonly Action<PostProcessRenderContext, int> SetWidth = Setter("width"), SetHeight = Setter("height"), SetScreenWidth = Setter("screenWidth"), SetScreenHeight = Setter("screenHeight");
		static readonly AccessTools.FieldRef<PostProcessRenderContext, RenderTextureDescriptor> SourceDescriptor =
			AccessTools.FieldRefAccess<PostProcessRenderContext, RenderTextureDescriptor>("m_sourceDescriptor");

		static Action<PostProcessRenderContext, int> Setter(string property)
		{
			var m = AccessTools.PropertySetter(typeof(PostProcessRenderContext), property);
			return m != null ? AccessTools.MethodDelegate<Action<PostProcessRenderContext, int>>(m) : null;
		}

		static readonly Action<TemporalAntialiasing, Vector2> SetJitter =
			AccessTools.PropertySetter(typeof(TemporalAntialiasing), nameof(TemporalAntialiasing.jitter)) is MethodInfo js ? AccessTools.MethodDelegate<Action<TemporalAntialiasing, Vector2>>(js) : null;

		internal static void SetTaaJitter(TemporalAntialiasing taa, Vector2 jitter) { if (taa != null) SetJitter?.Invoke(taa, jitter); }

		internal static void SetRenderSize(PostProcessRenderContext context, int w, int h)
		{
			SetWidth(context, w); SetHeight(context, h);
			SetScreenWidth(context, w); SetScreenHeight(context, h);
			ref var d = ref SourceDescriptor(context);
			d.width = w; d.height = h;
		}

		static void Break(string where, Exception e)
		{
			if (_broken) return;
			_broken = true;
			MelonLogger.Error($"{Mod.Name}: FSR's hook ({where}) failed: {e}. FSR off for this session; the game's own anti-aliasing runs.");
		}
	}
}
