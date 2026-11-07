using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace FSR4
{
	internal static class GameExposure
	{
		static readonly PropertyInfo BundleRenderer = typeof(PostProcessBundle).GetProperty("renderer", BindingFlags.Instance | BindingFlags.NonPublic);
		static FieldInfo _current;
		static float _auto = -1f;
		static bool _pending, _warned;

		internal static float Value(PostProcessLayer pp)
		{
			if (pp == null) return 0f;
			float post = 1f;
			var cg = pp.GetBundle<ColorGrading>()?.settings as ColorGrading;
			if (cg != null && cg.enabled.value) post = Mathf.Pow(2f, cg.postExposure.value);
			float auto = 1f;
			var aeb = pp.GetBundle<AutoExposure>();
			bool aeOn = aeb != null && aeb.settings.enabled.value;
			if (aeOn)
			{
				Request(aeb);
				if (_auto > 0f) auto = _auto;
			}
			float v = auto * post;
			return v;
		}

		static void Request(PostProcessBundle aeb)
		{
			if (_pending || BundleRenderer == null) return;
			var renderer = BundleRenderer.GetValue(aeb, null);
			if (renderer == null) return;
			if (_current == null) _current = renderer.GetType().GetField("m_CurrentAutoExposure", BindingFlags.Instance | BindingFlags.NonPublic);
			var rt = _current?.GetValue(renderer) as RenderTexture;
			if (rt == null || !rt.IsCreated())
			{
				if (_current == null && !_warned) { _warned = true; MelonLogger.Warning($"{Mod.Name}: the game's auto exposure could not be read; FSR uses its colour grading's exposure only."); }
				return;
			}
			_pending = true;
			AsyncGPUReadback.Request(rt, 0, r =>
			{
				_pending = false;
				if (r.hasError) return;
				float e = r.GetData<float>()[0];
				if (e > 0f && !float.IsInfinity(e) && !float.IsNaN(e)) _auto = e;
			});
		}
	}
}
