using System;
using MelonLoader;
using UnityEngine;

namespace FSR4
{
	internal static class AutoScale
	{
		internal const string ModeName = "Auto";
		internal const float Quantum = 0.05f;
		internal static MelonPreferences_Entry<float> TargetFps, MinScale, MaxScale;

		static float _ratio = -1f;

		internal static void Init(MelonPreferences_Category cfg)
		{
			TargetFps = cfg.CreateEntry("Target frame rate", 90f, description: "Auto mode: the frame rate the render scale aims for; 0 = the display's refresh rate.");
			MinScale = cfg.CreateEntry("Auto minimum scale", 1.0f, description: "Auto mode: the smallest upscaling ratio (1.0 = render at display size, as NativeAA).");
			MaxScale = cfg.CreateEntry("Auto maximum scale", 2.0f, description: "Auto mode: the largest upscaling ratio (2.0 = half the display's width and height, as Performance).");
		}

		internal static bool Dynamic
		{
			get
			{
				return Mod.Quality != null && Mod.Quality.Value.Equals(ModeName, StringComparison.OrdinalIgnoreCase);
			}
		}

		internal static float MinRatio
		{
			get
			{
				return Mathf.Clamp(Mathf.Min(MinScale.Value, MaxScale.Value), 1f, 3f);
			}
		}

		internal static float MaxRatio
		{
			get
			{
				return Mathf.Clamp(Mathf.Max(MinScale.Value, MaxScale.Value), 1f, 3f);
			}
		}

		internal static float Ratio
		{
			get
			{
				if (_ratio < 0f) _ratio = Quantise(MinRatio + (MaxRatio - MinRatio) * 0.5f);
				return Mathf.Clamp(_ratio, MinRatio, MaxRatio);
			}
		}

		internal static float Quantise(float r) => Mathf.Round(r / Quantum) * Quantum;

		static float _smoothMs = -1f, _lastStep, _lastCoarser = -100f, _lastFiner = -100f, _overSince, _roomSince;
		static float _trialAt = -1f, _trialFrom, _trialMs, _ceiling = -1f, _ceilingMs;
		const float Hold = 5f;

		internal static void Tick()
		{
			if (!Pacing.Installed) Pacing.Install();
			float dt = Time.unscaledDeltaTime * 1000f;
			if (dt <= 0f || dt > 250f) return;
			_smoothMs = _smoothMs < 0f ? dt : _smoothMs + (dt - _smoothMs) * 0.05f;
			float fps = TargetFps.Value > 0f ? TargetFps.Value : (float)Screen.currentResolution.refreshRateRatio.value;
			float budget = 1000f / Mathf.Max(10f, fps);
			float refresh = (float)Screen.currentResolution.refreshRateRatio.value;
			if (QualitySettings.vSyncCount > 0 && refresh > 0f) budget = Mathf.Max(budget, 1000f * QualitySettings.vSyncCount / refresh);
			else if (Application.targetFrameRate > 0) budget = Mathf.Max(budget, 1000f / Application.targetFrameRate);
			float r = Ratio;
			float now = Time.unscaledTime;
			if (now - _lastStep < 0.5f) return;
			float next = r;
			bool over = _smoothMs > budget * 1.05f && Pacing.GpuPaced;
			bool badly = _smoothMs > budget * 1.15f && Pacing.GpuPaced;
			float finer = Quantise(r - Quantum);
			bool room = r - Quantum >= MinRatio - 0.001f && _smoothMs * (r * r) / (finer * finer) < budget * 0.85f;
			if (!over) _overSince = now;
			if (!room) _roomSince = now;
			if (_trialAt > 0f && now - _trialAt >= 1.5f)
			{
				float predictedGain = _trialMs * (1f - (_trialFrom * _trialFrom) / (r * r));
				float gain = _trialMs - _smoothMs;
				_trialAt = -1f;
				if (predictedGain > 0f && gain < 0.3f * predictedGain)
				{
					_ceiling = _trialFrom;
					_ceilingMs = _trialMs;
					_ratio = _trialFrom;
					_lastStep = now; _lastFiner = now;
					return;
				}
			}
			if (_ceiling > 0f && _smoothMs > _ceilingMs * 1.15f) _ceiling = -1f;
			float top = _ceiling > 0f ? Mathf.Min(MaxRatio, _ceiling) : MaxRatio;
			if (over && now - _overSince >= 1f && (now - _lastFiner >= Hold || badly) && r + Quantum <= top + 0.001f)
			{
				next = Quantise(r + Quantum);
				_trialFrom = r; _trialMs = _smoothMs; _trialAt = now;
			}
			else if (room && now - _roomSince >= 2f && now - _lastCoarser >= Hold && now - _lastFiner >= 2f) next = finer;
			if (Mathf.Abs(next - r) < 0.001f) return;
			_lastStep = now;
			if (next > r) _lastCoarser = now; else _lastFiner = now;
			_ratio = next;
		}
	}
}
