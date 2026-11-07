using System;
using System.Diagnostics;
using UnityEngine.LowLevel;

namespace FSR4
{
	internal static class Pacing
	{
		internal const float GpuPacedMs = 0.5f;
		internal static bool GpuPaced => _waitedBits != 0 && Bits(_waitedBits) > 16;

		static long _before;
		static uint _waitedBits;
		static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

		struct Before { }

		struct After { }

		internal static bool Installed
		{
			get
			{
				foreach (var phase in PlayerLoop.GetCurrentPlayerLoop().subSystemList ?? new PlayerLoopSystem[0])
					foreach (var s in phase.subSystemList ?? new PlayerLoopSystem[0])
						if (s.type == typeof(Before)) return true;
				return false;
			}
		}

		internal static void Install()
		{
			if (Installed) return;
			var root = PlayerLoop.GetCurrentPlayerLoop();
			var phases = root.subSystemList;
			if (phases == null) return;
			for (int p = 0; p < phases.Length; p++)
			{
				var subs = phases[p].subSystemList;
				if (subs == null) continue;
				for (int i = 0; i < subs.Length; i++)
				{
					if (subs[i].type == null || subs[i].type.Name != "WaitForLastPresentationAndUpdateTime") continue;
					var list = new PlayerLoopSystem[subs.Length + 2];
					Array.Copy(subs, 0, list, 0, i);
					list[i] = new PlayerLoopSystem { type = typeof(Before), updateDelegate = StampBefore };
					list[i + 1] = subs[i];
					list[i + 2] = new PlayerLoopSystem { type = typeof(After), updateDelegate = StampAfter };
					Array.Copy(subs, i + 1, list, i + 3, subs.Length - i - 1);
					phases[p].subSystemList = list;
					root.subSystemList = phases;
					PlayerLoop.SetPlayerLoop(root);
					_before = 0; _waitedBits = 0;
					return;
				}
			}
		}

		static void StampBefore() => _before = Stopwatch.GetTimestamp();

		static void StampAfter()
		{
			if (_before == 0) return;
			float ms = (float)((Stopwatch.GetTimestamp() - _before) * TickMs);
			_waitedBits = (_waitedBits << 1) | (ms > GpuPacedMs ? 1u : 0u);
		}

		static int Bits(uint v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }
	}
}
