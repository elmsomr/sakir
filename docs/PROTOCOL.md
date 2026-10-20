# MSI MEG CoreLiquid LCD — what the panel actually wants

Notes from making Şakir work. None of this is documented by MSI; it was
recovered by reflecting over `API_Coreliquid.dll` and disassembling its IL.

Verified on a **MEG CoreLiquid S280**, firmware `0x1B`. The S360 declares the
same protocol but is untested here.

---

## 1. The device

The LCD is a **separate USB HID interface** from the pump, with its own PID:

| Constant | LCD PID | Note |
|---|---|---|
| `MS_6A11` | `0x5259` | |
| `MS_6A04` | `0x75B6` | S280 |
| `MS_6A15` | `0x8DBF` | |
| `MS_6A05` | `0x9BA6` | S360 |
| `MS_6A10` | `0xC7B2` | |
| `MS_6A16` | `0xD085` | |

Vendor IDs `0x0DB0` and `0x1462`. The constant name is the *pump's* PID, the
value is the *panel's* — looking for the pump PID on the HID interface is a
dead end.

The panel identifies as a Nuvoton HID device.

## 2. Getting at it

Everything goes through MSI Center's own managed assembly:

```
C:\Program Files (x86)\MSI\MSI Center\Coreliquid\API_Coreliquid.dll
  -> internal class API_Coreliquid.C_S360_DeviceControl
     -> P/Invokes into Coreliquid\MsiHid.dll  (plain C, __cdecl)
```

The class is `internal` and nearly every member is private, so it is driven by
reflection with `BindingFlags.NonPublic`. It has a public parameterless
constructor.

Three things bite immediately:

**It is 32-bit.** `API_Coreliquid.dll` and `MsiHid.dll` are x86. Your process
must be too.

**Working directory matters, before anything else.** Every P/Invoke references
its native DLL by a *relative* path — `Coreliquid\MsiHid.dll`,
`Coreliquid\ADLX.dll`. Set the process working directory to the MSI Center root
before touching any of it. Get the order wrong and the GPU sensors silently
report "unavailable" with no error.

**Use `Assembly.LoadFrom`, not `Load(byte[])`.** The constructor derives its
scratch paths from the assembly's own `Location`, which is empty for a
byte-loaded assembly, and it throws.

## 3. Geometry

The dynamic framebuffer is **320x240 landscape**, and it is **not scaled**. Push
a 240-wide bitmap and only the left 240 columns update — the rest keeps showing
the previous frame.

The module itself is a 240x320 portrait panel mounted rotated. `GetDeviceInfo`
reports its rotation in **degrees** (0/90/180/270), not as an index.

## 4. Pushing a frame

The documented-looking call is:

```csharp
UploadImage_Dynamic(string fileName, int direction)
SwitchMode_Dynamic()
```

Disassembling the first one shows what it really does:

```
ext = Path.GetExtension(fileName)
if (!ext.Contains(".png")) return false            // PNG only - checked by
                                                   // EXTENSION, not content
node = (direction is 90 or 270) ? "M7-S0-O-R1" + ext
                                : "M7-S0-O-R0" + ext
return UpdateFile(fileName, node)

UpdateFile(fileName, node):
    GetFileBuffer(fileName, out byte[] buf, out int len)   // just File.Open
    HMI_SendFileNode(len, node);   Thread.Sleep(1)
    HMI_StartSendData(buf, len);   Thread.Sleep(1)
```

`M7` is display mode 7 (Dynamic), `S0` slot 0, `R0`/`R1` the rotation class.

### Skip the file

`HMI_StartSendData` takes a **`byte[]`**. The file is incidental — it exists only
so `GetFileBuffer` can read it back. Encode the PNG to a `MemoryStream` and call
the last two directly:

```csharp
byte[] png = EncodeToPng(frame);
string node = (direction is 90 or 270) ? "M7-S0-O-R1.png" : "M7-S0-O-R0.png";
HMI_SendFileNode(png.Length, node);
Thread.Sleep(1);
HMI_StartSendData(png, (uint)png.Length);
Thread.Sleep(1);
SwitchMode_Dynamic();
```

This matters more than it sounds. At 10 fps, MSI's file path writes a 35–130 KB
PNG to disk **ten times a second** — roughly 109 GB a day. Going through memory
makes that zero.

### `SwitchMode_Dynamic` must be called on every frame

The panel latches the uploaded buffer only when this is called. Calling it once
"because the mode does not change" freezes the display on the first frame —
and every upload keeps returning success, so nothing anywhere reports a problem.

It costs about 0.6 ms against a ~100 ms upload. Just call it.

### Throughput

~100 ms per frame over USB, so **about 10 fps is the ceiling**, no matter how
fast you render.

## 5. `GetDisplayMode` does not tell you what is on the screen

```csharp
GetDisplayMode(byte seek, out byte mode, out byte slideShow, out byte count, out byte[] screen)
```

It reports the device's **stored mode profile**, not the live state. It reads
`0 (HWmonitor)` for hours while dynamic frames are displaying perfectly, and it
does not change after `SwitchMode_Dynamic` succeeds.

Do not use it to decide whether your frames are showing. Push success is the
only signal that means anything. (The `slideShow` / `count` fields it returns
*are* real: e.g. four hardware screens rotating every two seconds.)

## 6. Detecting a dead handle

If the panel re-enumerates on the USB bus, output reports to the stale handle
**keep returning success** while nothing reaches the screen.

The only way to notice is a read round-trip. `GetDeviceInfo` returning false, or
a firmware version of zero, means the handle is dead — reopen the device.

Related: if the frame stream becomes very sparse (about one push per second,
which is easy to hit once you stop re-sending identical frames), the panel
appears to fall back to its own stored loop. Sending something at least every
~400 ms avoids it.

## 7. Display modes

```
0 HWmonitor   1 Image     2 Banner   3 FactoryColorTest
4 FactoryLogoTest         5 Clock    6 Weather          7 Dynamic
```

`SwitchMode_Dynamic` is a thin wrapper over `SetDisplayMode`.

## 8. HID command codes

Packet size 64 bytes; HMI packet size 1024.

| Command | Code | Command | Code |
|---|---|---|---|
| `GET_DEV_ID` | `0x10` | `SEND_HOST_CPU_INFO` | `0x30` |
| `GET_DEV_INFO` | `0x14` | `SEND_HOST_FAN_SPEED` | `0x32` |
| `SET_DEV_TIME` | `0x16` | `SEND_HOST_FPS` | `0x34` |
| `SET_LCM_BACKLIGHT` | `0x18` | `SEND_HOST_TIME` | `0x36` |
| `SET_LCM_DIR` | `0x1A` | `SEND_HOST_WEATHER` | `0x38` |
| `SET_LCM_RESET` | `0x1C` | `SEND_HOST_MSG` | `0x3A` |
| `SEND_FILE_NODE` | `0x20` | `SEND_HOST_GPU_INFO` | `0x3C` |
| `HMI_SEND_FILE_DATA_START` | `0x22` | `PLAY_MEDIA_NODE` | `0x40` |
| `HMI_SEND_FILE_DATA_END` | `0x26` | `STOP_MEDIA_NODE` | `0x42` |
| `SAVE_FILE_NODE` | `0x28` | `PAUSE_MEDIA_NODE` | `0x44` |
| `DEL_FILE_NODE` | `0x2A` | `SET_DISPLAY_MODE` | `0x50` |
| | | `GET_DISPLAY_MODE` | `0x52` |

## 9. Video does not work

`UploadVideo_Dynamic` returns `false` on this firmware under every condition
tested — MSI's own bundled `black.mp4`, all four rotations, even a nonexistent
path — while `UploadImage_Dynamic` succeeds on the same connection.

So there is no device-side decoding to lean on. Moving content has to be pushed
frame by frame, which caps it at that ~10 fps.

## 10. Sensors, for free

MSI already ships the vendor libraries and `API_Coreliquid.dll` already declares
the P/Invokes, so there is no need to write your own bindings or ship anything:

- **GPU** — `DLL_ADLX` (AMD) / `DLL_NDA` (NVIDIA).
  `ADLX_Initialize("<MSI Center>\Coreliquid")` then
  `ADLX_GetSensors(0, ref ADLX_Sensors)` gives temperature, hotspot, usage,
  clock, fan RPM, power, voltage, memory.
- **FPS** — the `DetectFPS` class shows the whole mechanism: `GetFPS()` reads
  `<system drive>\MSI\MSI Center\Coreliquid\FPS.txt` for `[FPS] FPS=n` (`-1`
  meaning nothing is rendering), `CheckDetectFPS()` looks for a process named
  `DetectFPS`, and `Initialization()` launches `Coreliquid\DetectFPS.exe` when
  it is missing.

There is **no CPU temperature** anywhere in here. Windows exposes none either
(`MSAcpi_ThermalZoneTemperature` is "not supported" on this board), and MSI's
`Lib\CPU_Engine.dll` sits behind their NTIOLib ring0 driver — calling
`GetCPUCoreTemp` with a guessed signature access-violates. LibreHardwareMonitor,
with administrator rights, is the practical answer.

## 11. Do not block the render loop

Both the sensor libraries and the panel write path can block for a long time, or
wedge entirely:

- A HID write to a panel another process has open **never returns and never
  errors**.
- Sensor reads go through kernel drivers.

Keep rendering, sensor polling and device writes on separate threads, and only
ever run one instance — two processes fighting over the panel is the fastest way
to reproduce that hang.
