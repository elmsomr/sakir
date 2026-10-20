using System.Diagnostics;
using Sakir.Device;
using Sakir.Games;
using Sakir.Media;
using Sakir.Scenes;
using Sakir.Sensors;

namespace Sakir.Core;

/// <summary>
/// Owns the panel: renders scenes on a background thread, pushes them to the
/// device, and decides which scene should be showing.
///
/// The auto policy is the thing MSI Center cannot do - a game launches and the
/// panel becomes a sensor readout on that game's artwork, music starts and it
/// becomes a now-playing screen, otherwise it falls back to the dashboard.
/// </summary>
public sealed class PanelService : IDisposable
{
    // Measured on an S280: ~100 ms per USB frame upload, so ~10 fps is the
    // hardware ceiling regardless of how fast we render.
    public const int MaxFps = 10;

    private readonly SystemSensors _system = new();
    private readonly GpuSensors _gpu = new();
    private readonly FpsProvider _fps = new();
    private readonly PumpSensors _pump = new();
    private readonly NowPlayingProvider _music = new();
    private readonly GameCatalog _games = new();

    private readonly DashboardScene _dashboard = new();
    private readonly NowPlayingScene _nowPlaying = new();
    private readonly GameScene _game = new();
    private readonly MirrorScene _mirror = new();

    private CoreLiquidDevice? _device;
    private Thread? _thread;
    private Thread? _sensorThread;
    private Thread? _pushThread;
    private volatile bool _running;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTime _lastSensors = DateTime.MinValue;
    private DateTime _lastMusic = DateTime.MinValue;
    private DateTime _lastGameScan = DateTime.MinValue;

    public AppConfig Config { get; } = AppConfig.Load();

    /// <summary>"auto", or a scene id to pin.</summary>
    public string Mode
    {
        get => Config.Mode;
        set { Config.Mode = value; Config.Save(); }
    }

    public int Fps
    {
        get => Config.Fps;
        set { Config.Fps = Math.Clamp(value, 1, MaxFps); Config.Save(); }
    }

    public const double MinTextScale = 1.0, MaxTextScale = 1.5;

    /// <summary>Bigger text and layout on the panel. See ApplyScale.</summary>
    public double TextScale
    {
        get => Math.Clamp(Config.TextScale, MinTextScale, MaxTextScale);
        set { Config.TextScale = Math.Clamp(value, MinTextScale, MaxTextScale); Config.Save(); }
    }

    /// <summary>
    /// Shrinks the logical canvas and scales the graphics transform to match, so
    /// scenes draw at their normal coordinates but everything lands bigger on
    /// the panel. Scaling only the fonts would have made text overlap a layout
    /// that did not grow with it; this way both grow together, and GDI+ still
    /// rasterises the glyphs at their final size so they stay sharp.
    /// </summary>
    private void ApplyScale(Graphics g, SceneContext ctx)
    {
        float s = (float)TextScale;
        if (Math.Abs(s - 1f) < 0.01f)
        {
            ctx.Width = CoreLiquidDevice.PanelWidth;
            ctx.Height = CoreLiquidDevice.PanelHeight;
            return;
        }
        g.ScaleTransform(s, s);
        ctx.Width = (int)(CoreLiquidDevice.PanelWidth / s);
        ctx.Height = (int)(CoreLiquidDevice.PanelHeight / s);
    }

    public Rectangle MirrorRegion
    {
        get => _mirror.Region;
        set { _mirror.Region = value; Config.MirrorRegion = value; Config.Save(); }
    }

    /// <summary>Window the mirror scene follows. Empty process = whole screen.</summary>
    public void SetMirrorTarget(string processName, string title)
    {
        _mirror.TargetProcess = processName ?? "";
        _mirror.TargetTitle = title ?? "";
        Config.MirrorProcess = _mirror.TargetProcess;
        Config.MirrorTitle = _mirror.TargetTitle;
        Config.Save();
        Log.Info(string.IsNullOrEmpty(_mirror.TargetProcess)
            ? "mirror target: whole screen"
            : $"mirror target: {_mirror.TargetProcess} - {_mirror.TargetTitle}");
    }

    public string MirrorProcess => _mirror.TargetProcess;
    public string MirrorTitle => _mirror.TargetTitle;

    public string ActiveScene { get; private set; } = "";
    public string DeviceInfo { get; private set; } = "not connected";
    public bool Connected => _device?.IsOpen == true;
    public bool CpuTempAvailable => _system.CpuTempAvailable;
    public string GpuVendor => _gpu.Available ? _gpu.Vendor : "none";
    public double ActualFps { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised with a copy of each rendered frame, for the live preview.
    /// The subscriber owns the bitmap and must dispose it.</summary>
    public event Action<Bitmap>? FrameRendered;

    public IReadOnlyList<IScene> Scenes => new IScene[] { _dashboard, _nowPlaying, _game, _mirror };
    public GameCatalog Games => _games;

    public async Task StartAsync()
    {
        Log.Info($"--- start --- pid {Environment.ProcessId}, " +
                 $"{(Environment.Is64BitProcess ? "x64" : "x86")}, " +
                 $"elevated={SystemSensors.IsElevated}, .NET {Environment.Version}");

        // must happen before anything binds MSI's relatively-referenced natives
        CoreLiquidDevice.EnsureWorkingDirectory();

        try { _system.Start(); Log.Info($"cpu sensors: temp={_system.CpuTempAvailable}"); }
        catch (Exception ex) { Log.Error("SystemSensors.Start failed", ex); }

        try { Log.Info($"gpu sensors: {(_gpu.Start() ? _gpu.Vendor : "unavailable")}"); }
        catch (Exception ex) { Log.Error("GpuSensors.Start failed", ex); }

        try { _pump.Start(); }
        catch (Exception ex) { Log.Error("PumpSensors.Start failed", ex); }

        try { await _music.StartAsync(); Log.Info("SMTC ready"); }
        catch (Exception ex) { Log.Error("NowPlayingProvider.StartAsync failed", ex); }

        OpenDevice();

        _mirror.Region = Config.MirrorRegion;
        _mirror.TargetProcess = Config.MirrorProcess;
        _mirror.TargetTitle = Config.MirrorTitle;
        if (Connected && Config.Brightness is > 0 and <= 100) SetBrightness(Config.Brightness);

        _running = true;

        // sensors first, so the first frame already has values to draw
        _sensorThread = new Thread(SensorLoop) { IsBackground = true, Name = "Sakir sensors" };
        _sensorThread.Start();

        _pushThread = new Thread(PushLoop) { IsBackground = true, Name = "Sakir push" };
        _pushThread.Start();

        _thread = new Thread(Loop) { IsBackground = true, Name = "Sakir render" };
        _thread.Start();
        Log.Info("render, sensor and push threads started");
    }

    private void OpenDevice()
    {
        try
        {
            _device = new CoreLiquidDevice();
            if (_device.Open())
            {
                DeviceInfo = $"{_device.Model}  VID 0x{_device.Vid:X4}  PID 0x{_device.Pid:X4}  fw 0x{_device.Firmware:X}";
                Log.Info("device open: " + DeviceInfo);
            }
            else
            {
                DeviceInfo = "found, but Init() failed";
                LastError = "The LCD did not open. Close MSI Center's Coreliquid module and retry.";
                Log.Warn("device Init() returned false");
            }
        }
        catch (Exception ex)
        {
            DeviceInfo = "MSI Center not found";
            LastError = ex.Message;
            Log.Error("opening the device failed", ex);
        }
    }

    private void Reconnect()
    {
        try { _device?.Dispose(); } catch { }
        _device = null;
        Thread.Sleep(300);
        OpenDevice();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        // Don't block shutdown on these: a wedged sensor driver call or a HID
        // write to a contended panel cannot be joined. They are background
        // threads, so the process still exits.
        _sensorThread?.Join(500);
        _sensorThread = null;
        _pushThread?.Join(500);
        _pushThread = null;
        lock (_pushGate) { _pending?.Dispose(); _pending = null; }
        Log.Info("stopped");
    }

    private void Loop()
    {
        var ctx = new SceneContext { System = _system, Gpu = _gpu, Fps = _fps, Pump = _pump };
        var frame = new Bitmap(CoreLiquidDevice.PanelWidth, CoreLiquidDevice.PanelHeight);
        var fpsClock = Stopwatch.StartNew();
        int framesThisSecond = 0;

        bool loggedFirstFrame = false;

        while (_running)
        {
            long started = _clock.ElapsedMilliseconds;
            // Named so a failure tells us which stage died - the loop catches
            // everything, so without this a broken scene and a broken sensor
            // read look identical from the outside.
            string stage = "start";
            try
            {
                stage = "inputs";
                RefreshInputs(ctx);
                ctx.Time = _clock.Elapsed.TotalSeconds;

                stage = "pick";
                IScene scene = Pick(ctx);
                ActiveScene = scene.Id;

                stage = "render:" + scene.Id;
                using (Graphics g = Graphics.FromImage(frame))
                {
                    Draw.Prepare(g);
                    ApplyScale(g, ctx);
                    scene.Render(g, ctx);
                }

                // Hand the frame to the push thread instead of writing to the
                // device here. A HID write to a contended panel blocks forever
                // with no error, which used to wedge this loop outright.
                //
                // Only queue it if the pixels actually changed. Every push costs
                // a file write (MSI's API takes a path, not a buffer), and the
                // game scene's numbers move about once a second - so at 10 fps
                // roughly nine frames in ten are byte-identical and writing them
                // is pure waste.
                // A heartbeat push even when nothing changed. Skipping every
                // identical frame dropped us to about one push a second, and the
                // panel appears to fall back to its own stored loop when the
                // stream goes that sparse - so never go longer than this without
                // sending something.
                stage = "queue";
                bool changed = FrameChanged(frame);
                bool heartbeat = (DateTime.Now - _lastQueued).TotalMilliseconds >= HeartbeatMs;

                if (changed || heartbeat)
                {
                    if (!changed) _heartbeats++;
                    _lastQueued = DateTime.Now;
                    lock (_pushGate)
                    {
                        _pending?.Dispose();
                        _pending = (Bitmap)frame.Clone();
                    }
                }
                else _skipped++;

                stage = "preview";
                FrameRendered?.Invoke((Bitmap)frame.Clone());

                if (!loggedFirstFrame)
                {
                    loggedFirstFrame = true;
                    Log.Info($"first frame rendered and pushed (scene {ActiveScene})");
                }

                framesThisSecond++;
                if (fpsClock.ElapsedMilliseconds >= 1000)
                {
                    ActualFps = framesThisSecond * 1000.0 / fpsClock.ElapsedMilliseconds;
                    framesThisSecond = 0;
                    fpsClock.Restart();
                }
            }
            catch (Exception ex)
            {
                LastError = $"[{stage}] {ex.Message}";
                Log.Throttled("loop:" + stage, $"render loop failed at stage '{stage}'", ex);
            }

            int budget = 1000 / Math.Clamp(Fps, 1, MaxFps);
            int rest = budget - (int)(_clock.ElapsedMilliseconds - started);
            if (rest > 1) Thread.Sleep(rest);
        }

        frame.Dispose();
    }

    /// <summary>
    /// Sensor polling lives on its own thread and the render loop only ever
    /// reads the cached results.
    ///
    /// This is not just about speed. Both LibreHardwareMonitor and MSI Center
    /// talk to the hardware through their own kernel drivers, and a sensor read
    /// can block for a long time or wedge entirely. Calling it from the render
    /// loop meant one stuck driver call froze the panel with no error anywhere -
    /// the display just stopped at whatever frame it was on.
    /// </summary>
    private void SensorLoop()
    {
        while (_running)
        {
            try
            {
                long t0 = _clock.ElapsedMilliseconds;
                _system.Update();
                long t1 = _clock.ElapsedMilliseconds;
                _gpu.Update();
                _pump.Update();
                long t2 = _clock.ElapsedMilliseconds;
                _game_profile = _games.FindRunning();
                long t3 = _clock.ElapsedMilliseconds;

                // Only ask for a frame rate when a game screen is actually going
                // to show one - the detector hooks running games to measure.
                _fps.Update(_game_profile is not null || Mode == "game");

                if (t3 - t0 > 1500)
                    Log.Throttled("slowsensors",
                        $"slow sensor pass: cpu {t1 - t0} ms, gpu {t2 - t1} ms, games {t3 - t2} ms",
                        null, 60);

                SensorsAlive = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log.Throttled("sensorloop", "sensor poll failed", ex);
            }

            for (int i = 0; i < 10 && _running; i++) Thread.Sleep(100);
        }
    }

    private volatile GameProfile? _game_profile;
    public DateTime SensorsAlive { get; private set; } = DateTime.MinValue;

    // ---- identical-frame suppression ----------------------------------------
    /// <summary>Longest gap allowed between pushes, even with nothing changing.</summary>
    private const int HeartbeatMs = 400;

    private byte[]? _prevPixels, _curPixels;
    private bool _hasPrev;
    private int _skipped, _heartbeats;
    private DateTime _lastQueued = DateTime.MinValue;

    /// <summary>
    /// True when the frame differs from the one before it. Uses two reused
    /// buffers rather than allocating - at 10 fps a fresh 300 KB array per frame
    /// would be 3 MB/s of pointless GC churn.
    /// </summary>
    private bool FrameChanged(Bitmap frame)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        System.Drawing.Imaging.BitmapData data = frame.LockBits(
            rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int len = Math.Abs(data.Stride) * frame.Height;

            // Allocate BOTH buffers together. Allocating only one and letting
            // the swap below hand back a null left _prevPixels null on every
            // call, so nothing ever compared equal and no frame was ever
            // skipped - the feature silently did nothing.
            if (_curPixels is null || _curPixels.Length != len)
            {
                _curPixels = new byte[len];
                _prevPixels = new byte[len];
                _hasPrev = false;
            }
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _curPixels, 0, len);
        }
        finally { frame.UnlockBits(data); }

        if (_hasPrev && _curPixels.AsSpan().SequenceEqual(_prevPixels))
            return false;

        (_prevPixels, _curPixels) = (_curPixels, _prevPixels);   // swap, no allocation
        _hasPrev = true;
        return true;
    }

    // ---- device writes, isolated from rendering -----------------------------
    private readonly object _pushGate = new();
    private Bitmap? _pending;
    private DateTime _pushStartedAt = DateTime.MinValue;

    public DateTime LastPushOk { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// True while a write to the panel has been outstanding for too long. A HID
    /// write to a device another process is holding never returns and never
    /// errors, so this is the only way the condition is visible at all.
    /// </summary>
    public bool PanelStalled =>
        _pushStartedAt != DateTime.MinValue &&
        (DateTime.Now - _pushStartedAt).TotalSeconds > 5;

    private void PushLoop()
    {
        int failures = 0;
        bool loggedFirstPush = false;
        int okCount = 0, failCount = 0;
        DateTime lastStats = DateTime.Now;

        while (_running)
        {
            Bitmap? f;
            lock (_pushGate) { f = _pending; _pending = null; }
            if (f is null) { Thread.Sleep(15); continue; }

            try
            {
                _pushStartedAt = DateTime.Now;
                bool ok = _device is not null && _device.PushFrame(f);
                _pushStartedAt = DateTime.MinValue;

                if (ok) okCount++; else failCount++;

                // Periodic proof of life.
                //
                // Deliberately does NOT log GetDisplayMode. That call reports the
                // device's stored mode profile, not what is on the glass: it
                // reads 0 (HWmonitor) for hours while our pushed frames are
                // displaying perfectly. Logging it once sent this investigation
                // down a dead end. Push success is the signal that means
                // something.
                if ((DateTime.Now - lastStats).TotalSeconds >= 20)
                {
                    // Prove the handle still reaches hardware. Output reports to
                    // a re-enumerated device keep "succeeding", so only a read
                    // back can tell us the panel is really there.
                    bool alive = _device?.IsAlive() ?? false;

                    Log.Info($"pushes ok={okCount} fail={failCount} skipped={_skipped} " +
                             $"beats={_heartbeats} in 20s ({_device?.FrameFormat}) alive={alive}");

                    if (!alive)
                    {
                        Log.Warn("device stopped answering reads - reopening");
                        LastError = "Panel dropped off the USB bus; reconnecting.";
                        Reconnect();
                    }

                    okCount = 0; failCount = 0; _skipped = 0; _heartbeats = 0;
                    lastStats = DateTime.Now;
                }

                if (ok)
                {
                    LastPushOk = DateTime.Now;
                    failures = 0;
                    if (!loggedFirstPush)
                    {
                        loggedFirstPush = true;
                        Log.Info("first frame reached the panel");
                    }
                }
                else if (++failures == 15)
                {
                    Log.Warn("15 consecutive frame pushes failed - reopening the device");
                    LastError = "Panel stopped accepting frames; reconnecting.";
                    Reconnect();
                    failures = 0;
                }
            }
            catch (Exception ex)
            {
                _pushStartedAt = DateTime.MinValue;
                Log.Throttled("push", "frame push failed", ex);
            }
            finally { f.Dispose(); }
        }
    }

    private void RefreshInputs(SceneContext ctx)
    {
        DateTime now = DateTime.Now;

        if ((now - _lastMusic).TotalMilliseconds > 450)
        {
            _lastMusic = now;
            _ = _music.RefreshAsync();          // fire and forget; Current updates in place
        }
        ctx.NowPlaying = _music.Current;
        ctx.Game = _game_profile;
    }

    private IScene Pick(SceneContext ctx)
    {
        if (Mode != "auto")
        {
            IScene? pinned = Scenes.FirstOrDefault(s => s.Id == Mode);
            if (pinned is not null)
            {
                // a pinned game scene should still have a profile to draw
                if (pinned is GameScene && ctx.Game is null) ctx.Game = _games.Idle;
                return pinned;
            }
        }

        // priority: a running game, then music, then the dashboard
        if (ctx.Game is not null) return _game;
        if (ctx.NowPlaying is { IsPlaying: true }) return _nowPlaying;
        return _dashboard;
    }

    /// <summary>Renders one frame off the loop, for previewing a scene in the UI.</summary>
    public Bitmap RenderPreview(string sceneId)
    {
        // uses the cached profile - never call FindRunning from the UI thread
        var ctx = new SceneContext { System = _system, Gpu = _gpu, Fps = _fps, Pump = _pump, NowPlaying = _music.Current };
        ctx.Game = _game_profile ?? _games.Idle;
        ctx.Time = _clock.Elapsed.TotalSeconds;

        IScene scene = Scenes.FirstOrDefault(s => s.Id == sceneId) ?? _dashboard;
        var bmp = new Bitmap(CoreLiquidDevice.PanelWidth, CoreLiquidDevice.PanelHeight);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Draw.Prepare(g);
            ApplyScale(g, ctx);
            scene.Render(g, ctx);
        }
        return bmp;
    }

    public void SetBrightness(int percent)
    {
        Config.Brightness = Math.Clamp(percent, 0, 100);
        Config.Save();
        _device?.SetBacklight(Config.Brightness);
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
        _system.Dispose();
        _pump.Dispose();
        _music.Dispose();
    }
}
