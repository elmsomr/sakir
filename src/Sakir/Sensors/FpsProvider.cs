using System.Diagnostics;
using System.Text.RegularExpressions;
using Sakir.Core;
using Sakir.Device;

namespace Sakir.Sensors;

/// <summary>
/// In-game frame rate, using MSI Center's own detector.
///
/// Disassembling API_Coreliquid's DetectFPS class shows the whole mechanism:
/// GetFPS() reads an INI at &lt;system drive&gt;\MSI\MSI Center\Coreliquid\FPS.txt
/// ([FPS] FPS=n, with -1 meaning "no game"), CheckDetectFPS() just looks for a
/// running process named DetectFPS, and Initialization() launches
/// Coreliquid\DetectFPS.exe when it is missing. We do exactly the same.
///
/// The detector is only started while a game scene actually wants a frame rate -
/// it hooks running games to measure, so leaving it running when nothing needs
/// it would be rude.
/// </summary>
public sealed class FpsProvider
{
    private static readonly string FpsFile = Path.Combine(
        Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
        "MSI", "MSI Center", "Coreliquid", "FPS.txt");

    private static string DetectorExe =>
        Path.Combine(CoreLiquidDevice.MsiRoot, "Coreliquid", "DetectFPS.exe");

    private DateTime _lastLaunchAttempt = DateTime.MinValue;
    private bool _loggedMissing;

    /// <summary>Last reading. 0 means "no game rendering", not "zero fps".</summary>
    public int Fps { get; internal set; }   // internal so --snapshot can show a sample value

    public bool DetectorRunning { get; private set; }

    /// <summary>Set false to never launch MSI's detector.</summary>
    public bool Enabled { get; set; } = true;

    public bool Available => DetectorRunning && Fps > 0;

    public void Update(bool wanted)
    {
        DetectorRunning = IsDetectorRunning();

        if (!wanted || !Enabled)
        {
            if (!wanted) Fps = 0;
            return;
        }

        if (!DetectorRunning) { TryLaunch(); return; }

        Fps = ReadFps();
    }

    private static bool IsDetectorRunning()
    {
        try { return Process.GetProcessesByName("DetectFPS").Length > 0; }
        catch { return false; }
    }

    private void TryLaunch()
    {
        // don't hammer it if launching keeps failing
        if ((DateTime.Now - _lastLaunchAttempt).TotalSeconds < 10) return;
        _lastLaunchAttempt = DateTime.Now;

        string exe = DetectorExe;
        if (!File.Exists(exe))
        {
            if (!_loggedMissing) { _loggedMissing = true; Log.Warn($"FPS detector not found: {exe}"); }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Log.Info("started MSI's DetectFPS.exe for in-game frame rate");
        }
        catch (Exception ex)
        {
            Log.Throttled("fpslaunch", "could not start DetectFPS.exe", ex, 120);
        }
    }

    private int ReadFps()
    {
        try
        {
            if (!File.Exists(FpsFile)) return 0;

            // the detector rewrites this constantly, so share the handle
            using var fs = new FileStream(FpsFile, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string text = sr.ReadToEnd();

            Match m = Regex.Match(text, @"^\s*FPS\s*=\s*(-?\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success) return 0;

            int v = int.Parse(m.Groups[1].Value);
            return v < 0 ? 0 : v;      // -1 means nothing is rendering
        }
        catch { return Fps; }          // keep the last good value on a transient read
    }
}
