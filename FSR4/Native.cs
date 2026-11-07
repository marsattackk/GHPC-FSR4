using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MelonLoader;
using MelonLoader.Utils;

namespace FSR4
{
	internal static class Native
	{
		const string Dll = "FSR4Bridge";
		static readonly string[] Files = { "FSR4Bridge.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll" };

		[StructLayout(LayoutKind.Sequential)]
		internal struct FrameData
		{
			public IntPtr Color, Depth, Motion, Output;
			public uint RenderW, RenderH, DisplayW, DisplayH;
			public float JitterX, JitterY, MvScaleX, MvScaleY;
			public float FrameTimeMs, CameraNear, CameraFar, FovV;
			public float Sharpness;
			public int Reset;
			public uint CreateFlags, DispatchFlags;
			public float Exposure;
			public uint Direct;
			public uint MaxRenderW, MaxRenderH;
			public IntPtr Hudless;
			public uint Fg;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] CamPos;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] CamUp;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] CamRight;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] CamFwd;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Stats
		{
			public ulong Frames, ProviderId;
			public int LastResult, Failed;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Provider;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Adapter;
			public ulong Presented;
			public float CpuMs, GpuMs;
			public ulong FsrMemory, LogDropped;
			public uint BridgeStarts, ContextStarts;
			public int RemovedReason;
			public uint Failures;
			// GpuPeriodMs to MarkMs are always zero here, kept only so the struct lines up with bridge.cpp byte for byte.
			public float GpuPeriodMs, GpuPeriodMin, GpuPeriodMax, GpuBusyMs, GpuBusyMin, GpuBusyMax;
			public ulong GpuFrames, GpuDisjoint, GpuDropped;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public float[] MarkMs;
			public float FlushMs;
			public uint ExposureWanted;
			public ulong DirectFrames;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] FgProvider;
			public ulong FgProviderId, FgPrepared, FgGenerated, FgReal, FgSkipped, FgMemory;
			public int FgLastResult;
			public float FgGpuMs, FgPaceMs, FgIntervalMs;
			public float FgHookMs;
			public float FgRefreshHz;
			public ulong FgNotNeeded;
			public ulong PresentsHooked;
			public ulong FgHudless;
		}

		internal const uint HighDynamicRange = 1 << 0, DepthInverted = 1 << 3, AutoExposure = 1 << 5, NonLinearColorspace = 1 << 8;
		internal const uint NonLinearSrgb = 1 << 1;

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
		[DllImport(Dll, CharSet = CharSet.Unicode)] static extern int FSR4_Load(string dir);
		[DllImport(Dll)] internal static extern IntPtr FSR4_GetRenderEventFunc();
		[DllImport(Dll)] static extern int FSR4_PollLog(byte[] buf, int cap);
		[DllImport(Dll)] static extern void FSR4_GetStats(out Stats s);
		[DllImport(Dll)] internal static extern void FSR4_ClearFailure();
		[DllImport(Dll)] static extern int FSR4_Failed();
		[DllImport(Dll)] static extern int FSR4_ExposureWanted();
		[DllImport(Dll)] internal static extern ulong FSR4_GetViews(out IntPtr color, out IntPtr motion, out IntPtr output);
		[DllImport(Dll)] internal static extern void FSR4_ViewsInUse(ulong gen);
		[DllImport(Dll)] static extern int FSR4_LayoutField(int which, int i, out IntPtr name, out uint offset);
		internal const uint DirectColor = 1, DirectOutput = 2, DirectMotion = 4, DirectHudless = 8;
		[DllImport(Dll)] internal static extern ulong FSR4_GetHudlessView(out IntPtr hudless);
		[DllImport(Dll)] internal static extern void FSR4_HookPresent(IntPtr texture);
		internal static bool ExposureWanted => FSR4_ExposureWanted() != 0;

		internal static bool Failed => FSR4_Failed() != 0;
		internal static bool Fatal => FSR4_Failed() == 2;

		internal static bool Ready { get; private set; }

		internal static IntPtr RenderEvent { get; private set; }

		static readonly byte[] _line = new byte[1024];

		internal static bool Load()
		{
			try
			{
				string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "FSR4");
				Directory.CreateDirectory(dir);
				var asm = typeof(Native).Assembly;
				foreach (var f in Files)
				{
					using (var s = asm.GetManifestResourceStream("FSR4.native." + f))
					{
						if (s == null) { MelonLogger.Error($"{Mod.Name}: {f} missing from the mod."); return false; }
						var bytes = new byte[s.Length];
						for (int read = 0, n; read < bytes.Length; read += n)
							if ((n = s.Read(bytes, read, bytes.Length - read)) <= 0) throw new EndOfStreamException($"{f}: {read} of {bytes.Length} bytes");
						string path = Path.Combine(dir, f);
						if (!SameFile(path, bytes)) Write(path, bytes);
					}
				}
				if (LoadLibraryW(Path.Combine(dir, Files[0])) == IntPtr.Zero) { MelonLogger.Error($"{Mod.Name}: could not load {Files[0]} (error {Marshal.GetLastWin32Error()})."); return false; }
				bool ok = FSR4_Load(dir) == 1;
				Poll();
				if (!ok) return false;
				if (!LayoutMatches(0, typeof(FrameData)) | !LayoutMatches(1, typeof(Stats))) return false;
				RenderEvent = FSR4_GetRenderEventFunc();
				Ready = true;
				return true;
			}
			catch (Exception e)
			{
				MelonLogger.Error($"{Mod.Name}: native part failed to load: {e.Message}");
				return false;
			}
		}

		static bool LayoutMatches(int which, Type t)
		{
			FSR4_LayoutField(which, -1, out var np, out uint size);
			string sname = Marshal.PtrToStringAnsi(np);
			var mine = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
			var problems = new System.Collections.Generic.List<string>();
			if (Marshal.SizeOf(t) != size) problems.Add($"size {Marshal.SizeOf(t)} here, {size} in the native part");
			int n = 0;
			for (; FSR4_LayoutField(which, n, out np, out uint offset) == 1; n++)
			{
				string name = Marshal.PtrToStringAnsi(np);
				var f = Array.Find(mine, m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
				if (f == null) { problems.Add($"{name} missing here"); continue; }
				int at = (int)Marshal.OffsetOf(t, f.Name);
				if (at != offset) problems.Add($"{f.Name} at {at} here, {offset} in the native part");
			}
			if (n != mine.Length) problems.Add($"{mine.Length} fields here, {n} in the native part");
			if (problems.Count == 0)
			{
				return true;
			}
			MelonLogger.Error($"{Mod.Name}: the mod's two halves disagree on {sname} ({string.Join("; ", problems)}); FSR stays off.");
			return false;
		}

		static void Write(string path, byte[] bytes)
		{
			foreach (var old in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".old*"))
				try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			try { File.WriteAllBytes(path, bytes); return; }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
			File.Move(path, $"{path}.old{DateTime.Now.Ticks}");
			File.WriteAllBytes(path, bytes);
		}

		static bool SameFile(string path, byte[] bytes)
		{
			if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) return false;
			using (var sha = SHA256.Create())
			using (var f = File.OpenRead(path))
				return Convert.ToBase64String(sha.ComputeHash(f)) == Convert.ToBase64String(sha.ComputeHash(bytes));
		}

		internal static void Poll()
		{
			int n;
			while ((n = FSR4_PollLog(_line, _line.Length)) > 0) MelonLogger.Msg(System.Text.Encoding.UTF8.GetString(_line, 0, n));
		}

		internal static Stats GetStats()
		{
			FSR4_GetStats(out var s);
			return s;
		}
	}
}
