# FSR 4 Upscaling for Gunner, HEAT, PC!

Puts AMD FSR 4.1 into GHPC. The game renders at a lower resolution and FSR rebuilds the full picture, so you get more
frames, and it takes over from the game's anti-aliasing while it's at it. Cards without FSR 4 get FSR 3.1.

GHPC runs on Direct3D 11 and FSR 4 only runs on Direct3D 12, so the mod basically runs FSR on a Direct3D 12 device of
its own next to the game's and hands the frame back and forth every frame. Reticles, depth of field, bloom and the rest
of the game's effects are drawn after FSR, at full resolution, so the sights stay sharp.

This README describes **version 0.1.0**. No other mod needed, just MelonLoader. The mod is a single DLL, nothing else
to install.

## Installing it

1. Get [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) (0.7.3 or newer) into GHPC if you don't have it already.
2. Download `FSR4_<version>.zip` from [Releases](../../releases/latest).
3. Copy the `Mods` folder from the zip into the game folder, the one with `GHPC.exe` in it (Steam: right-click GHPC >
   Manage > Browse local files). You should end up with:
   ```
   Mods\FSR4.dll
   ```
4. Start the game. FSR is on straight away, in Quality mode.

You need Windows 10 or 11 and a graphics card that does Direct3D 12. FSR 4 itself needs an AMD Radeon RX 9000 card
(RDNA 4), anything else gets FSR 3.1.

To remove it, delete `Mods\FSR4.dll` and the `UserData\FSR4` folder (the mod unpacks its native part and AMD's DLLs
there).

## What it does

**Upscaling.** Pick a mode in the settings, or press **F9** in game to step through them:

| Mode | Renders at | 4K renders at |
|---|---|---|
| NativeAA | full resolution, FSR as anti-aliasing only | 3840x2160 |
| Quality (default) | 1 / 1.5 | 2560x1440 |
| Balanced | 1 / 1.7 | about 2260x1270 |
| Performance | 1 / 2 | 1920x1080 |
| UltraPerformance | 1 / 3 | 1280x720 |
| Auto | whatever holds your target frame rate | |

**Auto** moves the render scale to hold a frame rate you set (90 by default), between 1x and 2x. It only goes coarser
when the graphics card is actually what's holding the frame rate back, and it stops going coarser once that stops
buying any frames. It's opt-in, cos a moving resolution isn't for everyone.

Textures get AMD's mip bias while FSR is on, so they don't go soft at the lower render resolution, and FSR's history is reset
when the game cuts to another view (switching sights or cameras) so you don't get a smear across the cut.

**Performance switches.** A few of the game's cameras render every frame even when they show nothing. These work with
FSR on or off, and each one was checked to leave the picture exactly the same. Measured at 4K:

| Switch | What it does | Saves per frame |
|---|---|---|
| `FpCameraIdle` | first-person camera (binoculars, hands) off while it has nothing to draw | 0.33 ms |
| `FpCameraNoShadows` | the sun's shadow map isn't rendered a second time for the first-person camera | 0.37 ms |
| `DistortionCameraForward` | the heat-haze camera renders forward, same picture without a full-screen deferred pass | 0.24 ms |
| `MapCameraIdle` | map camera off while the map is closed | 0.18 ms |
| `InstancerCameraFilter` | grass instancer skips cameras that can't show grass. Off by default, no measurable gain | ~0 |

If FSR can't start, or stops after an error, the mod says so in the MelonLoader console and the game carries on with
its own anti-aliasing, same as without the mod.

## Known limits

- The FSR 3.1 path has only been run by forcing it on a card that has FSR 4. If you're on another card, I'd like to
  hear how it goes.
- Trees are the most expensive thing GHPC draws in the sights, and there's no free fix for them in the game's own
  rendering. Fewer pixels is what helps, which is what the modes and Auto are for.

## Bugs and suggestions

Found a bug? Open an [issue](../../issues) with your graphics card, resolution and FSR mode, and attach
`MelonLoader\Latest.log` from the game folder.

---

## More details

Click to open.

<details>
<summary><b>Settings</b></summary>

After the first start, `UserData\MelonPreferences.cfg` in the game folder has an `[FSR 4]` section (close the game
before editing it):

| Setting | Values | Default |
|---|---|---|
| `Enabled` | true / false | true |
| `Quality` | NativeAA, Quality, Balanced, Performance, UltraPerformance, Auto | Quality |
| `Sharpness` | 0 to 1: extra sharpening | 0.2 |
| `Target frame rate` | Auto only: the frame rate it aims for, 0 is your display's refresh rate | 90 |
| `Auto minimum scale` | Auto only: the smallest ratio (1.0 renders at full resolution) | 1.0 |
| `Auto maximum scale` | Auto only: the largest ratio (2.0 is Performance) | 2.0 |
| `Cycle key` | key that steps through the modes and Off in game (F1-F15, A-Z, 0-9, or None) | F9 |

The performance switches are in their own `[FSR4_Performance]` section, each true / false, defaults as in the table
above.

</details>

<details>
<summary><b>How it works</b></summary>

- FSR sits where the game's temporal anti-aliasing would run in Unity's post-processing stack, so it gets the game's
  HDR colour, the real depth buffer and the motion vectors, and everything after it (depth of field, exposure, bloom,
  grain, colour grading, reticles) runs on FSR's full-size result.
- The camera renders into a full-size target with its viewport shrunk to the render size, and a second camera shows
  FSR's result on screen.
- The Direct3D 11 and 12 sides share the textures and two fences, one per direction, so neither waits on the other
  longer than it has to.
- If a game update changes the part of the game the mod hooks into, the mod notices and switches FSR off for the
  session instead of throwing errors.

</details>

<details>
<summary><b>Building from source</b></summary>

This repository holds the mod's C# source (.NET Framework 4.7.2) and its native half (`native\`, C++). You need:

- the .NET SDK (6 or newer) and GHPC with MelonLoader installed (the build references the game's assemblies)
- CMake and the Microsoft C++ build tools (Visual Studio 2022 or its Build Tools, with "Desktop development with C++")
- AMD's signed FSR DLLs: copy `amd_fidelityfx_loader_dx12.dll` and `amd_fidelityfx_upscaler_dx12.dll` from the
  [FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) (`Kits\FidelityFX\signedbin`) into
  `native\amd\`, or pass `-p:FsrSdkBin=<folder>`

```
dotnet build -c Release -p:GameDir="<GHPC folder>\Bin"
```

`GameDir` is the folder with `GHPC.exe` in it (defaults to the usual Steam location). The build compiles the native
bridge, embeds it and AMD's two DLLs in the mod, and the DLL ends up in `bin\Release\FSR4.dll`.

</details>

## License

The source code in this repository is under the [GNU General Public License v3.0](LICENSE). AMD's FidelityFX headers
in `native\ffx\` and AMD's FSR DLLs are AMD's, under the MIT licence that comes with the FidelityFX SDK (see each
header). AMD FidelityFX Super Resolution is AMD's, and this mod isn't made or endorsed by AMD.
