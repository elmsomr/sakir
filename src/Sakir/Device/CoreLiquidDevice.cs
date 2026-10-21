using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;

namespace Sakir.Device;

/// <summary>
/// Drives the MEG CoreLiquid S280/S360 pump-head LCD through MSI Center's own
/// shipped API: API_Coreliquid.dll -> C_S360_DeviceControl -> MsiHid.dll.
///
/// That class is <c>internal</c> and nearly all of its members are private, so
/// every call goes through reflection with BindingFlags.NonPublic. The public
/// constructor is the one thing we can use directly.
///
/// "Dynamic" mode streams a frame straight to the panel without consuming one of
/// the device's persistent image slots - which is how this sidesteps MSI
/// Center's 10-image limit entirely.
/// </summary>
public sealed class CoreLiquidDevice : IDisposable
{
    /// <summary>
    /// The panel is 240x320 natively; these are its two side lengths. Which way
    /// round the framebuffer is depends on how the pump head is mounted - see
    /// <see cref="IsPortrait"/>. The firmware does NOT scale: hand it a buffer
    /// of the wrong shape and part of the screen keeps showing the previous
    /// frame.
    /// </summary>
    public const int LongSide = 320;
    public const int ShortSide = 240;

    /// <summary>
    /// MSI Center's "Select Direction of Your Device" setting is the stored
    /// rotation, and it describes how the pump head is installed. Verified on an
    /// S280 by pushing test patterns at each rotation:
    ///
    ///   90 / 270  -> 320x240 landscape buffer  (node R1)
    ///   0  / 180  -> 240x320 portrait buffer   (node R0)
    ///
    /// A 320x240 frame sent at 0/180 came out sideways and left stale pixels
    /// where the buffer did not reach; a 240x320 frame filled the glass. The
    /// firmware applies the rotation itself, so an upright buffer is always the
    /// right thing to send - no rotating on our side.
    /// </summary>
    public static bool IsPortrait(int direction) => direction is 0 or 180;

    public bool Portrait => IsPortrait(Direction);
    public int FrameWidth => Portrait ? ShortSide : LongSide;
    public int FrameHeight => Portrait ? LongSide : ShortSide;

    private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public |
                                    BindingFlags.Instance | BindingFlags.Static;

    public static string MsiRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "MSI", "MSI Center");

    private static string ApiDllPath => Path.Combine(MsiRoot, "Coreliquid", "API_Coreliquid.dll");

    private readonly Type _type;
    private readonly object _dev;
    private readonly string[] _slots;
    private int _slot;
    private bool _open;

    public ushort Vid { get; private set; }
    public ushort Pid { get; private set; }
    public string Model { get; private set; } = "";
    public uint Firmware { get; private set; }
    public uint Backlight { get; private set; }
    /// <summary>Panel rotation in DEGREES (0/90/180/270), not an index.</summary>
    public int Direction { get; private set; }

    public bool IsOpen => _open;

    public static bool IsMsiCenterInstalled() => File.Exists(ApiDllPath);

    private static bool _cwdSet;

    /// <summary>
    /// Every P/Invoke in MSI's assemblies references its native DLLs by a
    /// RELATIVE path ("Coreliquid\MsiHid.dll", "Coreliquid\ADLX.dll"), so the
    /// process working directory has to be the MSI Center root before any of
    /// them binds. This must run before the sensor layer too, not just before
    /// the device - getting the order wrong makes ADLX silently unavailable.
    /// </summary>
    public static void EnsureWorkingDirectory()
    {
        if (_cwdSet || !Directory.Exists(MsiRoot)) return;
        Directory.SetCurrentDirectory(MsiRoot);
        _cwdSet = true;
    }

    public CoreLiquidDevice()
    {
        if (!IsMsiCenterInstalled())
            throw new FileNotFoundException($"MSI Center not found at {ApiDllPath}");

        EnsureWorkingDirectory();

        // Must be LoadFrom, not Load(byte[]): the C_S360_DeviceControl
        // constructor derives its scratch image paths from the assembly's own
        // Location, which is empty for a byte[]-loaded assembly and throws
        // "path is not of a legal form".
        Assembly asm = Assembly.LoadFrom(ApiDllPath);
        _type = asm.GetType("API_Coreliquid.C_S360_DeviceControl", throwOnError: true)!;
        _dev = Activator.CreateInstance(_type)!;

        string dir = Path.Combine(Path.GetTempPath(), "Sakir");
        Directory.CreateDirectory(dir);
        // MSI alternates two scratch files in dynamic mode (see its ImagePath /
        // SwapImagePath fields); mirroring that avoids handing the panel a file
        // we are still writing. The extension is appended per-frame so the
        // encoder can change without renaming these.
        _slots = new[] { Path.Combine(dir, "f0"), Path.Combine(dir, "f1") };
    }

    private object? Call(string name, params object?[] args)
    {
        MethodInfo mi = _type.GetMethod(name, BF)
            ?? throw new MissingMethodException($"C_S360_DeviceControl.{name}");
        return mi.Invoke(_dev, args);
    }

    private T? Field<T>(string name)
    {
        FieldInfo? fi = _type.GetField(name, BF);
        object? v = fi?.GetValue(_dev);
        return v is T t ? t : default;
    }

    public bool Open()
    {
        _open = Call("Init") is true;
        if (!_open) return false;

        Vid = Field<ushort>("VID_Current");
        Pid = Field<ushort>("PID_Current");
        Model = Field<string>("ModelName_Current") ?? "";

        var args = new object?[] { null, null, null, null, null };
        if (Call("GetDeviceInfo", args) is true)
        {
            Firmware = (uint)(args[0] ?? 0u);
            Backlight = (uint)(args[1] ?? 0u);
            Direction = (int)(uint)(args[2] ?? 0u);
        }
        return true;
    }

    public void SetBacklight(int percent) =>
        Call("SetBackLight", Math.Clamp(percent, 0, 100));

    /// <summary>
    /// Re-reads the stored rotation. The user can change it in MSI Center while
    /// we are running, and the framebuffer shape changes with it. Returns true
    /// if it changed.
    /// </summary>
    public bool RefreshDirection()
    {
        if (!_open) return false;
        try
        {
            var args = new object?[] { null, null, null, null, null };
            if (Call("GetDeviceInfo", args) is not true) return false;
            int d = (int)(uint)(args[2] ?? 0u);
            if (d == Direction) return false;
            Direction = d;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Round-trips a read to prove the handle is still connected to real
    /// hardware. Writes are not enough: if the panel re-enumerates on the USB
    /// bus, output reports to the stale handle keep reporting success while
    /// nothing reaches the screen, so the app happily pushes into a void.
    /// </summary>
    public bool IsAlive()
    {
        try
        {
            var args = new object?[] { null, null, null, null, null };
            if (Call("GetDeviceInfo", args) is not true) return false;
            uint fw = (uint)(args[0] ?? 0u);
            return fw != 0;              // a dead handle reads back zeros
        }
        catch { return false; }
    }

    /// <summary>
    /// enum_Modes: 0 HWmonitor, 1 Image, 2 Banner, 3 FactoryColorTest,
    /// 4 FactoryLogoTest, 5 Clock, 6 Weather, 7 Dynamic.
    ///
    /// WARNING - this does NOT tell you what is on the glass. It reports the
    /// device's stored mode profile: it reads 0 (HWmonitor) for hours while
    /// dynamic frames are displaying perfectly, and the value does not change
    /// after SwitchMode_Dynamic succeeds. Do not use it to decide whether the
    /// panel is showing our frames - push success is the only reliable signal.
    /// Kept because the slideShow/count fields it returns are genuinely the
    /// stored profile (e.g. 4 hardware screens rotating every 2 s).
    /// </summary>
    public (bool Ok, int Mode, int SlideShow, int Count) GetDisplayMode(byte seek = 0)
    {
        try
        {
            var args = new object?[] { seek, null, null, null, null };
            if (Call("GetDisplayMode", args) is not true) return (false, -1, -1, -1);
            return (true,
                Convert.ToInt32(args[1] ?? -1),
                Convert.ToInt32(args[2] ?? -1),
                Convert.ToInt32(args[3] ?? -1));
        }
        catch { return (false, -1, -1, -1); }
    }

    public static string ModeName(int mode) => mode switch
    {
        0 => "HWmonitor", 1 => "Image", 2 => "Banner", 3 => "FactoryColorTest",
        4 => "FactoryLogoTest", 5 => "Clock", 6 => "Weather", 7 => "Dynamic",
        _ => "?"
    };

    /// <summary>
    /// Pushes one frame. SwitchMode_Dynamic must be issued on EVERY frame - the
    /// panel latches the uploaded buffer only when it is called. Skipping it
    /// (0.6 ms against a ~100 ms upload) freezes the display on the first frame
    /// while every call still reports success.
    /// </summary>
    public bool PushFrame(Bitmap frame)
    {
        if (!_open) return false;
        if (_memoryPath && PushInMemory(frame)) return true;
        return PushViaFile(frame);
    }

    /// <summary>
    /// Sends the frame straight from memory - no file, no disk write at all.
    ///
    /// MSI's own UploadImage_Dynamic writes ~35-130 KB to disk per frame, which
    /// at 10 fps is tens of gigabytes a day. Disassembling it shows the file is
    /// incidental: it only reads the bytes back via GetFileBuffer and hands them
    /// to HMI_SendFileNode + HMI_StartSendData, and the latter takes a byte[].
    /// So we encode the PNG to a MemoryStream and call those two directly.
    ///
    /// The node name is exactly what MSI builds: "M7-S0-O-R{0|1}" + extension,
    /// where M7 is display mode 7 (Dynamic) and R1 covers 90/270 rotation.
    /// PNG is mandatory - the original checks the extension for ".png" and
    /// rejects anything else, which is why JPEG never worked.
    /// </summary>
    private bool PushInMemory(Bitmap frame)
    {
        try
        {
            using var ms = new MemoryStream(96 * 1024);
            frame.Save(ms, ImageFormat.Png);
            byte[] bytes = ms.ToArray();

            string node = (Direction is 90 or 270) ? "M7-S0-O-R1.png" : "M7-S0-O-R0.png";

            if (Call("HMI_SendFileNode", bytes.Length, node) is not true) return Fail();
            Thread.Sleep(1);
            if (Call("HMI_StartSendData", bytes, (uint)bytes.Length) is not true) return Fail();
            Thread.Sleep(1);

            _memoryProven = true;
            return Call("SwitchMode_Dynamic") is true;
        }
        catch (Exception)
        {
            return Fail();
        }

        bool Fail()
        {
            // Only abandon the in-memory route if it never worked at all; a
            // one-off failure on a proven path is just a contended device.
            if (!_memoryProven) _memoryPath = false;
            return false;
        }
    }

    private bool PushViaFile(Bitmap frame)
    {
        string path = _slots[_slot] + ".png";
        _slot = 1 - _slot;
        frame.Save(path, ImageFormat.Png);

        if (Call("UploadImage_Dynamic", path, Direction) is not true) return false;
        return Call("SwitchMode_Dynamic") is true;
    }

    private bool _memoryPath = true;
    private bool _memoryProven;

    /// <summary>Which upload route is in use - "memory" writes nothing to disk.</summary>
    public string FrameFormat =>
        _memoryPath ? (_memoryProven ? "memory" : "memory (unverified)") : "file";

    public void Dispose()
    {
        if (_open)
        {
            try { Call("CloseDevice"); } catch { /* shutting down anyway */ }
            _open = false;
        }
    }
}
