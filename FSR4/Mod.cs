using System;
using FSR4.Common;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FSR4.Mod), "FSR 4 Upscaling", "0.1.0", "marsattackk")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]
[assembly: HarmonyDontPatchAll]

namespace FSR4
{
	public class Mod : MelonMod
	{
		internal const string Name = "FSR 4";
		internal static MelonPreferences_Entry<bool> Enabled;
		internal static MelonPreferences_Entry<string> Quality;
		internal static MelonPreferences_Entry<float> Sharpness;

		static readonly string[] Modes = { "NativeAA", "Quality", "Balanced", "Performance", "UltraPerformance" };
		static readonly float[] Ratios = { 1f, 1.5f, 1.7f, 2f, 3f };

		internal static float JitterSignX = 1f, JitterSignY = 1f, MotionSignX = 1f, MotionSignY = 1f;
		internal static bool Active => Enabled.Value;
		internal static float Ratio
		{
			get
			{
				int i = Array.FindIndex(Modes, m => m.Equals(Quality.Value, StringComparison.OrdinalIgnoreCase));
				return Ratios[i < 0 ? 1 : i];
			}
		}

		public override void OnInitializeMelon()
		{
			BuildStamp.Log(Info.Name);
			var cfg = MelonPreferences.CreateCategory("FSR 4");
			Enabled = cfg.CreateEntry("Enabled", true, description: "Upscale the game with AMD FSR 4.1 (FSR 3.1 on GPUs without FSR 4).");
			Quality = cfg.CreateEntry("Quality", "Quality", description: "NativeAA (1x, anti-aliasing only), Quality (1.5x), Balanced (1.7x), Performance (2x), UltraPerformance (3x), or Auto (the scale follows 'Target frame rate', between 'Auto minimum scale' and 'Auto maximum scale').");
			Sharpness = cfg.CreateEntry("Sharpness", 0.2f, description: "Extra sharpening, 0 (none) to 1.");
			AutoScale.Init(cfg);
			CycleKey = cfg.CreateEntry("Cycle key", "F9", description: "Key that steps through the modes while playing: NativeAA, Quality, Balanced, Performance, UltraPerformance, Off (a Unity KeyCode name; None for no key).");
			_cycleVk = VirtualKey(CycleKey.Value);
			PerfSettings.Init(HarmonyInstance);
			if (!Native.Load()) { MelonLogger.Warning($"{Name}: the upscaler could not start (above); the game runs as without the mod."); return; }
			TaaSlot.Patch(HarmonyInstance);
			MelonLogger.Msg(Enabled.Value ? $"{Name} loaded: {Quality.Value}, sharpness {Sharpness.Value:0.##}." : $"{Name} loaded, disabled in its settings.");
		}

		public override void OnUpdate()
		{
			if (!Native.Ready) return;
			Native.Poll();
			Recover();
			if (CycleKeyPressed()) Cycle();
			var cam = Camera.main;
			if (cam != null && Active && cam.GetComponent<Upscaler>() == null) cam.gameObject.AddComponent<Upscaler>();
		}

		const int MaxRetries = 3;
		const float RetryForgetSeconds = 60f;
		static int _retries;
		static float _failedAt = -1f, _retriedAt = -1f;
		static string _failedSetup;
		static bool _gaveUp;

		static string Setup() => $"{Enabled.Value} {Quality.Value} {Screen.width}x{Screen.height}";

		static void Recover()
		{
			float now = Time.realtimeSinceStartup;
			if (!Native.Failed)
			{
				_failedAt = -1f;
				if (_retries > 0 && _retriedAt >= 0f && now - _retriedAt > RetryForgetSeconds) _retries = 0;
				return;
			}
			if (_failedAt < 0f) { _failedAt = now; _failedSetup = Setup(); }
			if (!Active) return;
			bool changed = Setup() != _failedSetup;
			if (changed && _gaveUp) { _gaveUp = false; _retries = 0; }
			if (_gaveUp) return;
			if (!changed && now - _failedAt < 5f * (1 << _retries)) return;
			if (_retries >= MaxRetries)
			{
				_gaveUp = true;
				MelonLogger.Warning($"{Name}: stopped after {MaxRetries} retries; off until the mode or the screen size changes (the game's own anti-aliasing is on).");
				return;
			}
			_retries++;
			_retriedAt = now;
			MelonLogger.Msg($"{Name}: starting again after the error above ({(changed ? "settings or screen changed" : "after a pause")}, try {_retries} of {MaxRetries}).");
			Native.FSR4_ClearFailure();
		}

		[System.Runtime.InteropServices.DllImport("user32")] static extern short GetAsyncKeyState(int vk);
		internal static MelonPreferences_Entry<string> CycleKey;
		static int _cycleVk;
		static bool _cycleDown;

		static bool CycleKeyPressed()
		{
			if (_cycleVk == 0 || !Application.isFocused) { _cycleDown = false; return false; }
			bool down = (GetAsyncKeyState(_cycleVk) & 0x8000) != 0;
			bool pressed = down && !_cycleDown;
			_cycleDown = down;
			return pressed;
		}

		static int VirtualKey(string name)
		{
			if (string.IsNullOrEmpty(name) || !Enum.TryParse(name, true, out KeyCode k) || k == KeyCode.None) return 0;
			if (k >= KeyCode.F1 && k <= KeyCode.F15) return 0x70 + (k - KeyCode.F1);
			if (k >= KeyCode.A && k <= KeyCode.Z) return 0x41 + (k - KeyCode.A);
			if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return 0x30 + (k - KeyCode.Alpha0);
			MelonLogger.Warning($"{Name}: cycle key '{name}' not supported (F1-F15, A-Z, 0-9); no key.");
			return 0;
		}

		static void Cycle()
		{
			int i = Array.FindIndex(Modes, m => m.Equals(Quality.Value, StringComparison.OrdinalIgnoreCase));
			if (!Enabled.Value) { Enabled.Value = true; Quality.Value = Modes[0]; }
			else if (i >= Modes.Length - 1) Enabled.Value = false;
			else Quality.Value = Modes[Math.Max(0, i + 1)];
			MelonPreferences.Save();
			MelonLogger.Msg(Enabled.Value ? $"{Name}: {Quality.Value} ({Ratio:0.##}x)." : $"{Name}: off.");
		}
	}
}
