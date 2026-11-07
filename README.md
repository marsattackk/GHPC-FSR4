# FSR 4 Upscaling for Gunner, HEAT, PC!

AMD FSR 4.1 in GHPC. The game renders at a lower resolution and FSR rebuilds the full picture, so you get more frames
and better anti-aliasing than the game's own. Cards without FSR 4 get FSR 3.1.

Reticles, bloom, depth of field and the rest of the effects are drawn after FSR at full resolution, so the sights stay
sharp.

> [!WARNING]
> **This is a janky mod.** GHPC is Direct3D 11 and FSR 4 only runs on Direct3D 12, so the mod runs FSR on a second
> device next to the game's and passes the frame back and forth every frame. It's a hack and it might give you
> glitches, crashes or worse performance. I've only tested it on my own PC (RX 9070 XT at 4K). If it doesn't work on
> yours, delete the DLL and you're back to the normal game.

## Installing it

1. Get [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) 0.7.3 or newer into GHPC.
2. Download `FSR4_<version>.zip` from [Releases](../../releases/latest).
3. Copy the `Mods` folder from the zip into the game folder, the one with `GHPC.exe` in it, so you end up with
   `Mods\FSR4.dll`.
4. Start the game. FSR is on straight away, in Quality mode.

Needs Windows 10 or 11 and a Direct3D 12 card. FSR 4 itself needs a Radeon RX 9000 (RDNA 4), anything else gets
FSR 3.1. No other mods needed.

To remove it, delete `Mods\FSR4.dll` and the `UserData\FSR4` folder.

## Modes

Press **F9** in game to step through them, or set one in the settings.

| Mode | At 1080p renders at | At 4K renders at |
|---|---|---|
| NativeAA (FSR as anti-aliasing only) | 1920x1080 | 3840x2160 |
| Quality (default) | 1280x720 | 2560x1440 |
| Balanced | about 1130x635 | about 2260x1270 |
| Performance | 960x540 | 1920x1080 |
| UltraPerformance | 640x360 | 1280x720 |
| Auto | 1920x1080 to 960x540 | 3840x2160 to 1920x1080 |

Auto moves the resolution to hold your target frame rate (90 by default), and only drops it when the graphics card is
actually what's holding you back.

The mod also switches off a few of the game's cameras that render every frame for nothing (first-person, map,
heat haze). Same picture, about 1 ms a frame back at 4K, and it works with FSR off too.

If FSR can't start, the mod says so in the MelonLoader console and the game carries on with its own anti-aliasing.

## Known limits

- The FSR 3.1 path has only been tested by forcing it on an FSR 4 card. If you're on something else, I'd like to hear
  how it goes.
- Trees are the most expensive thing in the sights and there's no free fix for them. Fewer pixels is what helps, which
  is what the modes are for.

## Bugs

Open an [issue](../../issues) with your graphics card, resolution and FSR mode, and attach `MelonLoader\Latest.log`
from the game folder.

<details>
<summary><b>Settings</b></summary>

After the first start, `UserData\MelonPreferences.cfg` has an `[FSR 4]` section (close the game before editing it):

| Setting | Values | Default |
|---|---|---|
| `Enabled` | true / false | true |
| `Quality` | NativeAA, Quality, Balanced, Performance, UltraPerformance, Auto | Quality |
| `Sharpness` | 0 to 1 | 0.2 |
| `Target frame rate` | Auto only, 0 is your display's refresh rate | 90 |
| `Auto minimum scale` | Auto only, 1.0 is full resolution | 1.0 |
| `Auto maximum scale` | Auto only, 2.0 is Performance | 2.0 |
| `Cycle key` | F1-F15, A-Z, 0-9 or None | F9 |

The camera switches are in `[FSR4_Performance]`: `FpCameraIdle`, `FpCameraNoShadows`, `DistortionCameraForward` and
`MapCameraIdle` (on), `InstancerCameraFilter` (off, no measurable gain).

</details>

<details>
<summary><b>How it works (for devs)</b></summary>

Two halves: the C# mod hooks into Unity and GHPC, and a C++ bridge (`native\bridge.cpp`) runs FSR. The bridge and
AMD's two DLLs are embedded in `FSR4.dll` and unpacked to `UserData\FSR4` at start.

**Where FSR sits in the frame.** GHPC uses Unity's Post Processing Stack v2. While FSR is on, I force the main camera's
`PostProcessLayer` to TAA and a Harmony transpiler on `PostProcessLayer.Render` swaps its two TAA calls (the jittered
projection and `TemporalAntialiasing.Render`) for FSR's jitter and FSR's upscale. That way FSR gets the HDR colour
before exposure, the real depth (`ResolvedDepth`) and the motion vectors, and everything after the TAA slot (the game's
reticles and scope mask, depth of field, bloom, exposure, grain, grading) runs on FSR's full-size output. The player's
own anti-aliasing setting is put back when FSR goes off.

**Rendering below display size.** The main camera gets a display-size render target of mine, and its `camera.rect` is
shrunk to the render size only for the length of its render (`OnPreCull` to `OnPostRender`), so Unity and PPv2 size
everything before the TAA slot to the render size. A presenter camera of mine then blits the final picture to the
screen, before the first-person camera and the UI. The main camera can't render straight to the screen, cos Unity
puts all of a frame's screen cameras in one buffer sized by the first of them, and a shrunk main camera makes that
buffer small, so everything comes out blurry.

**The D3D11 to D3D12 bridge.** Unity calls into the bridge on the render thread through `IssuePluginEvent`. The bridge
makes a D3D12 device on the same adapter as the game's D3D11 one (by LUID), and the two share the colour, depth,
motion and output textures and two fences, one per direction. Each frame D3D11 copies the inputs and signals, the
D3D12 queue waits on that, runs FSR and signals back, and D3D11 waits on that before reading the output. Three
command allocator slots so neither side waits on the other longer than it has to. The bridge asks AMD's loader which
upscaler versions the driver offers and takes FSR 4 when it's there, FSR 3.1 when it isn't.

**The rest.**
- Mip bias: log2(render / display) - 1 on the game's textures while FSR is on, UI, fonts and reticles left alone,
  everything put back on off.
- History resets come from the game's own view-change events (switching sights or cameras).
- Auto runs one FSR context with the dynamic resolution flag, so a change of scale doesn't reset the history.
- Guards: the transpiler checks the patched IL, and if a game update moves the code it hooks, or nothing gets upscaled,
  FSR switches off for the session. If the bridge hits an error, FSR stops and the game falls back to its own
  anti-aliasing.

</details>

<details>
<summary><b>Building from source</b></summary>

C# (.NET Framework 4.7.2) plus a C++ half in `native\`. You need the .NET SDK 6+, GHPC with MelonLoader, CMake and the
Visual Studio 2022 C++ build tools, and AMD's `amd_fidelityfx_loader_dx12.dll` and `amd_fidelityfx_upscaler_dx12.dll`
from the [FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) (`Kits\FidelityFX\signedbin`) in
`native\amd\` (or `-p:FsrSdkBin=<folder>`).

```
dotnet build -c Release -p:GameDir="<GHPC folder>\Bin"
```

The DLL ends up in `bin\Release\FSR4.dll` with the native bridge and AMD's DLLs embedded.

</details>

## License

[GPL-3.0](LICENSE). AMD's FidelityFX headers in `native\ffx\` and the FSR DLLs are AMD's, under the MIT licence that
comes with the FidelityFX SDK. FSR is AMD's, and this mod isn't made or endorsed by AMD.
