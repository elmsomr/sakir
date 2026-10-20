using System.Runtime.InteropServices;
using System.Windows;
using Sakir.Core;
using Sakir.Device;
using WinForms = System.Windows.Forms;

namespace Sakir;

// UseWPF + UseWindowsForms means both System.Windows and System.Windows.Forms
// are implicitly imported, and both define Application - so qualify it.
public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private PanelService? _service;
    private MainWindow? _window;
    private WinForms.NotifyIcon? _tray;
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // A WinExe has no console of its own; attach to the launching one and
            // rebind Console.Out, otherwise writes go to the void.
            AttachConsole(-1);
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            int code = SelfTest();
            Shutdown(code);
            return;
        }

        // One instance only. Two of them fight over the panel: both open the
        // device, both push frames, and the display ends up frozen on whichever
        // lost. The wait covers the "restart as administrator" handover, where
        // the outgoing instance is still shutting down.
        _single = new Mutex(false, @"Local\Sakir.SingleInstance");
        bool acquired;
        try { acquired = _single.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }   // previous instance was killed

        if (!acquired)
        {
            System.Windows.MessageBox.Show(
                "\u015Eakir is already running - look for it in the system tray.",
                "\u015Eakir", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        bool startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        _ = StartAsync(startHidden);
    }

    private async Task StartAsync(bool startHidden)
    {
        _service = new PanelService();
        await _service.StartAsync();

        _window = new MainWindow(_service);
        if (!startHidden) _window.Show();

        BuildTray();
    }

    private void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show \u015Eakir", null, (_, _) => ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "\u015Eakir",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    /// <summary>
    /// Sakir himself. Loaded from the embedded multi-size .ico so Windows picks
    /// the right resolution for the tray, rather than scaling one bitmap down.
    /// Falls back to a drawn placeholder if the resource ever goes missing.
    /// </summary>
    private static System.Drawing.Icon MakeTrayIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("sakir.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using Stream? s = asm.GetManifestResourceStream(name);
                if (s is not null)
                    return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { /* fall through to the drawn placeholder */ }

        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(255, 22, 24, 34));
            g.FillRectangle(body, 2, 5, 28, 22);
            using var screen = new SolidBrush(Color.FromArgb(255, 255, 60, 90));
            g.FillRectangle(screen, 6, 9, 20, 14);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            // clone, then free the native handle GetHicon just allocated
            using var tmp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.ReallyClose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _service?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _service?.Dispose();
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Headless check of the riskiest part of the port: an x86 self-contained
    /// .NET 8 process reaching MSI's 32-bit DLLs and actually lighting the panel.
    /// </summary>
    private static int SelfTest()
    {
        Console.WriteLine();
        Console.WriteLine($"process is {(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}");
        Console.WriteLine($"MSI Center: {CoreLiquidDevice.MsiRoot}");

        if (!CoreLiquidDevice.IsMsiCenterInstalled())
        {
            Console.WriteLine("MSI Center not found.");
            return 2;
        }

        using var dev = new CoreLiquidDevice();
        if (!dev.Open())
        {
            Console.WriteLine("Open() failed - is MSI Center driving the panel?");
            return 3;
        }

        Console.WriteLine($"connected: VID=0x{dev.Vid:X4} PID=0x{dev.Pid:X4} model='{dev.Model}'");
        Console.WriteLine($"  firmware  0x{dev.Firmware:X}");
        Console.WriteLine($"  backlight {dev.Backlight}");
        Console.WriteLine($"  direction {dev.Direction} deg");

        var sensors = new Sensors.SystemSensors();
        sensors.Start();
        sensors.Update();
        Thread.Sleep(400);
        sensors.Update();
        Console.WriteLine($"  elevated  {Sensors.SystemSensors.IsElevated}");
        Console.WriteLine($"  cpu load  {sensors.CpuLoad:0} %");
        Console.WriteLine($"  cpu temp  {(sensors.CpuTempAvailable ? sensors.CpuTemp.ToString("0") + " C" : "unavailable (needs admin)")}");
        Console.WriteLine($"  ram       {sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} GB");

        var gpu = new Sensors.GpuSensors();
        if (gpu.Start())
        {
            gpu.Update();
            Console.WriteLine($"  gpu       {gpu.Vendor}  {gpu.Temp} C (hotspot {gpu.Hotspot})  {gpu.Usage}%  {gpu.Watts} W");
        }
        else Console.WriteLine("  gpu       unavailable");
        sensors.Dispose();

        using var bmp = new Bitmap(CoreLiquidDevice.PanelWidth, CoreLiquidDevice.PanelHeight);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Scenes.Draw.Prepare(g);
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                Color.FromArgb(255, 12, 14, 24), Color.FromArgb(255, 40, 16, 56), 60f);
            g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);
            using var accent = new SolidBrush(Color.FromArgb(255, 255, 60, 90));
            using var white = new SolidBrush(Color.White);
            using var fBig = new Font("Segoe UI", 22, FontStyle.Bold);
            using var fSmall = new Font("Segoe UI", 10);
            g.DrawString("Sakir", fBig, white, 18, 76);
            g.DrawString("x86 .NET 8  ->  CoreLiquid OK", fSmall, accent, 20, 122);
            g.DrawString(DateTime.Now.ToString("HH:mm:ss"), fSmall, white, 20, 146);
            using var pen = new Pen(Color.FromArgb(255, 255, 60, 90), 2);
            g.DrawRectangle(pen, 1, 1, bmp.Width - 3, bmp.Height - 3);
        }

        bool ok = dev.PushFrame(bmp);
        Console.WriteLine(ok ? "frame pushed OK" : "frame push FAILED");
        return ok ? 0 : 4;
    }
}
