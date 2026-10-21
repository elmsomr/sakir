# Şakir

A replacement for MSI Center's control of the LCD on **MSI MEG CoreLiquid
S280 / S360** AIO coolers.

MSI Center lets you put a handful of stored images and GIFs on the 2.4" screen
in the pump head. Şakir puts live information there instead, and changes what it
shows on its own depending on what you are doing.

> Not affiliated with, endorsed by, or connected to Micro-Star International.
> "MSI" and "MEG CoreLiquid" are their trademarks, used here only to say what
> hardware this works with.

---

## What it does

Scenes switch automatically:

| When | The panel shows |
|---|---|
| A game is running | FPS, GPU temperature / hotspot / usage / power, CPU temperature and RAM — over that game's own artwork |
| Music is playing | Album art edge to edge, track, artist, and a live progress bar |
| Neither | CPU load ring, GPU stats, memory, clock |

You can also pin a scene, or mirror any window onto the panel.

**Now playing** reads the Windows media session, so Apple Music, Spotify,
browsers — anything that registers with Windows — works, artwork included.

**Game artwork** comes from your local Steam library cache, or your own images.
Nothing is downloaded. PUBG and CS2 ship as profiles. Adding a game is one small
JSON file in a `games` folder, no rebuild, and the accent colour can be picked
automatically from the game's artwork. See
[`docs/GAME-PROFILES.md`](docs/GAME-PROFILES.md).

**Window mirroring** captures the target window directly rather than copying
pixels off the screen, so the window can sit behind everything else and still
appear on the panel.

Plus: adjustable text size, frame rate and brightness, a tray icon, and optional
start with Windows.

## Screenshots

<!-- TODO: photos of the actual panel. Put game.png, music.png and
     dashboard.png in docs/img/, then delete this comment markers around the
     table below so it shows. A photo of the real panel beats a screenshot.

| Game | Now playing | Dashboard |
|---|---|---|
| ![game](docs/img/game.png) | ![music](docs/img/music.png) | ![dashboard](docs/img/dashboard.png) |
-->

Photos coming soon.

## Requirements

- Windows 10 1809 or newer
- **MSI Center installed.** Şakir drives the panel through the API MSI Center
  already ships, loading those DLLs from your own installation. It does not
  bundle or redistribute any MSI code.
- .NET 8 SDK, only if you are building it yourself. Releases are self-contained
  and need no runtime installed.

## Install

Download the latest release, unzip it anywhere, run `Sakir.exe`.

Or build it:

```bash
git clone https://github.com/elmsomr/sakir.git
cd sakir/src/Sakir
dotnet build -c Release
```

The output lands in
`src/Sakir/bin/Release/net8.0-windows10.0.19041.0/win-x86/Sakir.exe`.

It **must** be x86: MSI's `API_Coreliquid.dll` and `MsiHid.dll` are 32-bit. The
project is configured as a self-contained `win-x86` build, so it runs without an
x86 .NET runtime installed.

To check your hardware without opening the UI:

```bash
Sakir.exe --selftest
```

Command line: `--tray` starts hidden in the tray, `--selftest` runs the hardware
check and exits (it pushes one test frame, so the screen stays on it until Şakir
or MSI Center draws again), `--profiles` checks your game profile files,
`--snapshot <folder>` renders every scene to PNG without touching the panel.

### Mounting orientation

The pump head can be installed with the hoses pointing left, right, up or down.
MSI Center's "Select Direction of Your Device" setting records which, and Şakir
follows it: wide mounts get the landscape layout, tall mounts (hoses up or down)
get a portrait layout. If you change the setting in MSI Center while Şakir is
running it switches within a few seconds. The landscape layout is verified on an
S280; **the portrait layout has been checked in renders and with a test pattern
on a wide mount, but not yet on a real tall mount**, so reports are welcome.

## CPU temperature (optional): PawnIO + administrator

Şakir runs unelevated and everything works **except CPU temperature**. Reading
it needs a kernel driver, and Şakir does not ship one. It uses
[PawnIO](https://pawnio.eu/), a separate signed driver that you install
yourself:

1. Install PawnIO from its official site.
2. Start Şakir as administrator (the UI has a "Restart as administrator"
   button).

Without PawnIO, or without administrator rights, the CPU gauge simply shows
load instead of temperature. Nothing else is affected.

Earlier versions bundled the WinRing0 driver through LibreHardwareMonitor.
Windows Defender flags WinRing0 as a vulnerable driver
(`VulnerableDriver:WinNT/Winring0`) and removes it, so it was dropped.

"Start with Windows" registers a scheduled task with highest privileges, so at
logon it starts elevated without a UAC prompt. You may need to run Şakir as
administrator once to create that task.

## Limitations

- Tested on the **S280**. The S360 uses the same protocol and should work, but
  it is untested — reports welcome.
- The panel tops out around **10 fps**; each frame upload takes roughly 100 ms
  over USB.
- Device-side video decoding does not work on this firmware, so moving content
  has to go through the mirror scene as pushed frames. Audio stays on the PC.
- Minimised windows cannot be mirrored — a minimised window has nothing to
  render. Some DRM-protected surfaces come back black.
- **CPU temperature is verified on AMD only.** Intel should work the same way
  (LibreHardwareMonitor supports it, with PawnIO and administrator rights) but
  has not been tried on real Intel hardware. If it shows load instead of a
  temperature, `sakir.log` lists the sensors that were found; attach it to an
  issue.
- **GPU sensors are verified on AMD only.** The NVIDIA path reads MSI's own
  NVIDIA structure (temperature, usage, clock, VRAM; MSI's API exposes no
  hotspot or power for NVIDIA) but has never run on real NVIDIA hardware. If
  the GPU gauge looks wrong, please run `Sakir.exe --selftest` and open an
  issue with the output. Intel GPUs are not supported; the GPU gauge shows
  "no sensors".

## How it works

Şakir talks to the panel through `API_Coreliquid.dll` from MSI Center, driving
its internal `C_S360_DeviceControl` class by reflection. Frames are PNGs sent
straight from memory — MSI's own upload function takes a *file path* and writes
every frame to disk, which at 10 fps works out to roughly 109 GB a day.

[`docs/PROTOCOL.md`](docs/PROTOCOL.md) documents what was found: the upload
chain, the display-mode node names, the FPS source, and the traps that cost the
most time.

Detailed development notes (Turkish) are in
[`docs/notes-tr.md`](docs/notes-tr.md).

## Contributing

Issues and pull requests welcome. Especially useful:

- S360 confirmation, or any other CoreLiquid model with this panel
- NVIDIA GPU sensor testing
- New game profiles

## License

[GPL-3.0](LICENSE). You can use, modify and redistribute this, but derivative
works have to stay open source under the same license.

## Third-party components

Şakir is built on, or works alongside, the following. Each keeps its own
license; this project does not change them.

| Component | Used for | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 | CPU temperature, via NuGet | MPL-2.0 |
| [PawnIO](https://pawnio.eu/) | Kernel driver LibreHardwareMonitor uses for CPU temperature. **Not bundled**; you install it yourself | See the [PawnIO repository](https://github.com/namazso/PawnIO) |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | Rendering frames | MIT |
| .NET 8 runtime | Bundled in the self-contained release exe | MIT |
| MSI Center | Şakir loads its libraries from your own installation at runtime. **Nothing from MSI is bundled or redistributed** | MSI's own terms |

The Steam artwork shown in the game scene is read from your own local Steam
library cache and is not included in this repository. Game names, logos and
artwork belong to their owners.

Not affiliated with, endorsed by, or connected to MSI, Apple, NVIDIA, AMD or
Valve. All product names and trademarks belong to their respective owners.

## The name

Şakir is my cockatiel. He is also the icon.
