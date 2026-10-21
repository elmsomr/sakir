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

        int snap = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0)
        {
            // Renders every scene to PNG in both panel orientations. Never opens
            // the display, so it is safe while the app is running.
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            string dir = snap + 1 < e.Args.Length ? e.Args[snap + 1] : Path.Combine(AppContext.BaseDirectory, "snapshots");
            Shutdown(SnapshotReport(dir));
            return;
        }

        if (e.Args.Contains("--profiles", StringComparer.OrdinalIgnoreCase))
        {
            // Validates game profiles without touching the panel, so it is safe to
            // run while the app is already driving the display.
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Shutdown(ProfilesReport());
            return;
        }

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
    /// Writes scene-orientation-scale PNGs using real sensor readings, with
    /// sample values only where there is nothing real to show (FPS, a track).
    /// Reads the pump and GPU sensors but never opens the display.
    /// </summary>
    private static int SnapshotReport(string outDir)
    {
        Directory.CreateDirectory(outDir);
        CoreLiquidDevice.EnsureWorkingDirectory();

        var system = new Sensors.SystemSensors(); system.Start();
        var gpu = new Sensors.GpuSensors(); try { gpu.Start(); } catch { }
        var pump = new Sensors.PumpSensors(); try { pump.Start(); } catch { }
        system.Update(); Thread.Sleep(500); system.Update(); gpu.Update(); pump.Update();

        var fps = new Sensors.FpsProvider { Enabled = false };
        fps.Fps = 144;

        var catalog = new Games.GameCatalog();
        Games.GameProfile? game = catalog.ByKey("cs2") ?? catalog.Profiles.FirstOrDefault();

        // a stand-in cover so the music scene has something to draw
        using var art = new Bitmap(320, 320);
        using (Graphics ag = Graphics.FromImage(art))
        using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new Rectangle(0, 0, 320, 320), Color.FromArgb(255, 200, 70, 120),
                   Color.FromArgb(255, 40, 60, 160), 45f))
        {
            ag.FillRectangle(lg, 0, 0, 320, 320);
            ag.FillEllipse(Brushes.Gold, 90, 90, 140, 140);
        }
        var track = new Media.NowPlayingInfo("Apple Music", "Track Title", "Artist Name", "Album Name",
            "Playing", TimeSpan.FromSeconds(95), TimeSpan.FromSeconds(212), art);

        Scenes.IScene[] scenes =
        {
            new Scenes.DashboardScene(), new Scenes.NowPlayingScene(), new Scenes.GameScene()
        };

        int written = 0;
        foreach ((string orient, int fw, int fh) in new[] { ("landscape", 320, 240), ("portrait", 240, 320) })
        {
            foreach (double scale in new[] { 1.0, 1.25 })
            {
                foreach (Scenes.IScene scene in scenes)
                {
                    using var bmp = new Bitmap(fw, fh);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        Scenes.Draw.Prepare(g);
                        var ctx = new Scenes.SceneContext
                        {
                            System = system, Gpu = gpu, Fps = fps, Pump = pump,
                            NowPlaying = track, Game = game, Time = 1.0,
                            Width = (int)(fw / scale), Height = (int)(fh / scale)
                        };
                        if (scale != 1.0) g.ScaleTransform((float)scale, (float)scale);
                        scene.Render(g, ctx);
                    }
                    string file = Path.Combine(outDir, $"{scene.Id}-{orient}-{scale:0.00}.png");
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    written++;
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine($"wrote {written} images to {outDir}");
        system.Dispose(); pump.Dispose();
        return 0;
    }

    /// <summary>Lists every game profile, any file that failed to load, and the accent each resolves to.</summary>
    private static int ProfilesReport()
    {
        var catalog = new Games.GameCatalog();
        Console.WriteLine();
        Console.WriteLine($"games folder: {catalog.GamesDir}  ({(Directory.Exists(catalog.GamesDir) ? "exists" : "not created yet")})");
        Console.WriteLine($"profiles loaded: {catalog.Profiles.Count}");
        foreach (Games.GameProfile p in catalog.Profiles)
        {
            catalog.LoadArt(p);
            string accent = $"#{p.Accent.R:X2}{p.Accent.G:X2}{p.Accent.B:X2}";
            string how = !p.AccentAuto ? "fixed"
                : p.Hero is null ? "auto, but no artwork found -> fallback"
                : "auto, from artwork";
            Console.WriteLine($"  {p.Key,-14} process={p.ProcessName,-14} accent={accent} ({how})  art={(p.Hero is null ? "none" : $"{p.Hero.Width}x{p.Hero.Height}")}  logo={(p.Logo is null ? "none" : "yes")}");
        }
        if (catalog.Issues.Count > 0)
        {
            Console.WriteLine("problems:");
            foreach (string s in catalog.Issues) Console.WriteLine("  " + s);
        }
        return catalog.Issues.Count == 0 ? 0 : 1;
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

        // Same shape the app itself would push for this rotation.
        Console.WriteLine($"  orientation {(dev.Portrait ? "portrait" : "landscape")}, framebuffer {dev.FrameWidth}x{dev.FrameHeight}");
        using var bmp = new Bitmap(dev.FrameWidth, dev.FrameHeight);
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
