using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GHPC.Camera;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FSR4
{
	internal static class MipBias
	{
		static readonly Dictionary<int, (Texture tex, float own)> _biased = new Dictionary<int, (Texture, float)>();
		static readonly HashSet<int> _seen = new HashSet<int>();
		static HashSet<int> _excluded = new HashSet<int>();
		static float _bias;
		static int _sweepAt = -1;
		static float _lightAt;
		static bool _hooked;

		internal static float For(int renderW, int displayW)
		{
			float b = Mathf.Log(renderW / (float)displayW, 2f) - 1f;
			return Mathf.Clamp(b, -3f, 0f);
		}

		internal static void Tick(int renderW, int displayW)
		{
			Hook();
			float want = For(renderW, displayW);
			if (!Mathf.Approximately(want, _bias)) { _bias = want; Sweep("bias " + want.ToString("0.###")); return; }
			if (_sweepAt >= 0 && Time.frameCount >= _sweepAt) { _sweepAt = -1; Sweep("view, unit or scene change"); return; }
			if (_bias != 0f && Time.unscaledTime >= _lightAt) { _lightAt = Time.unscaledTime + 10f; Light(); }
		}

		internal static void Restore()
		{
			Unhook();
			int n = 0;
			foreach (var (tex, own) in _biased.Values) if (tex != null) { tex.mipMapBias = own; n++; }
			if (_biased.Count > 0) MelonLogger.Msg($"{Mod.Name}: mip bias off: {n} textures given their own bias back.");
			_biased.Clear();
			_seen.Clear();
			_bias = 0f;
		}

		static void Sweep(string why)
		{
			var sw = Stopwatch.StartNew();
			_excluded = Exclusions(out string parts);
			int added = 0, changed = 0, restored = 0, candidates = 0;
			foreach (int id in _biased.Keys.Where(_excluded.Contains).ToList())
			{
				var (tex, own) = _biased[id];
				if (tex != null) tex.mipMapBias = own;
				_biased.Remove(id);
				restored++;
			}
			foreach (int id in _biased.Keys.Where(k => _biased[k].tex == null).ToList()) _biased.Remove(id);
			foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
			{
				if (!Candidate(t)) continue;
				candidates++;
				int id = t.GetInstanceID();
				_seen.Add(id);
				if (_excluded.Contains(id)) continue;
				if (_biased.TryGetValue(id, out var e)) { t.mipMapBias = e.own + _bias; changed++; }
				else if (_bias != 0f) { _biased[id] = (t, t.mipMapBias); t.mipMapBias += _bias; added++; }
			}
			if (_bias == 0f) { foreach (var (tex, own) in _biased.Values) if (tex != null) tex.mipMapBias = own; restored += _biased.Count; _biased.Clear(); }
			_lightAt = Time.unscaledTime + 10f;
			MelonLogger.Msg($"{Mod.Name}: mip bias {_bias:0.###} ({why}): {_biased.Count} textures biased ({added} new, {changed} updated) of {candidates} with mips; {_excluded.Count} excluded ({parts}); {restored} given their own bias back; {sw.Elapsed.TotalMilliseconds:0.#} ms.");
		}

		static void Light()
		{
			int added = 0;
			foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
			{
				if (!Candidate(t)) continue;
				int id = t.GetInstanceID();
				if (!_seen.Add(id) || _excluded.Contains(id)) continue;
				_biased[id] = (t, t.mipMapBias);
				t.mipMapBias += _bias;
				added++;
			}
			if (added > 0) MelonLogger.Msg($"{Mod.Name}: mip bias {_bias:0.###}: {added} new textures biased (light pass).");
		}

		static bool Candidate(Texture t) =>
			t != null && !(t is RenderTexture) && ((t is Texture2D t2 && t2.mipmapCount > 1) || (t is Texture2DArray a && a.mipmapCount > 1));

		static HashSet<int> Exclusions(out string parts)
		{
			var ex = new HashSet<int>();
			int sprites = 0, fonts = 0, ui = 0, post = 0;
			void Add(Texture t, ref int count) { if (t != null && ex.Add(t.GetInstanceID())) count++; }
			foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>()) Add(s.texture, ref sprites);
			foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>()) Add(f.atlasTexture, ref fonts);
			foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) Add(f.material != null ? f.material.mainTexture : null, ref fonts);
			foreach (var g in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Graphic>()) Add(g.mainTexture, ref ui);
			foreach (var r in PostMeshRenderer.renderers.Values)
			{
				if (r == null) continue;
				foreach (var m in r.sharedMaterials)
				{
					if (m == null) continue;
					foreach (int p in m.GetTexturePropertyNameIDs()) Add(m.GetTexture(p), ref post);
				}
			}
			parts = $"sprites {sprites}, fonts {fonts}, UI {ui}, PostMesh {post}";
			return ex;
		}

		static void Hook()
		{
			if (_hooked) return;
			_hooked = true;
			SceneManager.sceneLoaded += SceneLoaded;
			CameraSlot.ActiveInstanceChanged += SlotChanged;
			_camManager = CameraManager.Instance;
			if (_camManager != null) _camManager.UnitChangeComplete += SoonSweep;
		}

		static CameraManager _camManager;

		static void Unhook()
		{
			if (!_hooked) return;
			_hooked = false;
			SceneManager.sceneLoaded -= SceneLoaded;
			CameraSlot.ActiveInstanceChanged -= SlotChanged;
			if (_camManager != null) _camManager.UnitChangeComplete -= SoonSweep;
			_camManager = null;
		}

		static void SceneLoaded(Scene s, LoadSceneMode m) { Unhook(); SoonSweep(); }

		static void SlotChanged(CameraSlot s) => SoonSweep();
		static void SoonSweep() { if (_bias != 0f) _sweepAt = Time.frameCount + 3; }
	}
}
