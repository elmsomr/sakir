using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using Sakir.Core;

namespace Sakir.Games;

public sealed class GameProfile
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>Process name without .exe. Empty for the idle profile.</summary>
    public required string ProcessName { get; init; }
    public string SteamAppId { get; init; } = "";
    public required Color Bg1 { get; init; }
    public required Color Bg2 { get; init; }
    public string Motif { get; init; } = "none";

    /// <summary>True when the accent should be derived from the game's artwork.</summary>
    public bool AccentAuto { get; init; }
    /// <summary>Used when AccentAuto is set but the artwork has no usable colour.</summary>
    public Color FallbackAccent { get; init; } = Color.FromArgb(255, 255, 60, 90);

    // Stored as an int so a write from the sensor thread (when artwork loads)
    // cannot be observed half-done by the render thread.
    private int _accent = unchecked((int)0xFFFF3C5A);
    public Color Accent
    {
        get => Color.FromArgb(Volatile.Read(ref _accent));
        set => Volatile.Write(ref _accent, value.ToArgb());
    }

    public Bitmap? Hero { get; set; }
    public Bitmap? Logo { get; set; }
}

/// <summary>
/// Game profiles plus their artwork.
///
/// Profiles are JSON files. PUBG and CS2 ship embedded in the exe (so the
/// release stays a single file); anything in the <c>games</c> folder next to the
/// exe is loaded on top, replacing an embedded profile with the same key or
/// adding a new game. The folder is watched, so edits apply without a restart.
///
/// Artwork is never downloaded: Steam already keeps each game's hero banner and
/// logo on disk under appcache\librarycache\&lt;appid&gt;\, and a user-supplied
/// image in the backgrounds folder overrides it.
/// </summary>
public sealed class GameCatalog
{
    public string BackgroundsDir { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "backgrounds");

    public string GamesDir { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "games");

    private volatile IReadOnlyList<GameProfile> _profiles = Array.Empty<GameProfile>();
    private DateTime _gamesStamp = DateTime.MinValue;
    private DateTime _lastStampCheck = DateTime.MinValue;

    public GameCatalog()
    {
        Reload();
    }

    public GameProfile Idle { get; } = new()
    {
        Key = "idle", Name = "SYSTEM", ProcessName = "",
        Bg1 = Color.FromArgb(255, 12, 12, 18), Bg2 = Color.FromArgb(255, 26, 26, 38),
        Accent = Color.FromArgb(255, 255, 60, 90), Motif = "none"
    };

    public IReadOnlyList<GameProfile> Profiles => _profiles;

    /// <summary>Problems found while loading the last time, one line each.</summary>
    public IReadOnlyList<string> Issues => _issues;
    private volatile IReadOnlyList<string> _issues = Array.Empty<string>();

    // ---------------------------------------------------------------- loading --

    /// <summary>Rebuilds the profile list: embedded defaults, then the user's folder on top.</summary>
    private void Reload()
    {
        var byKey = new Dictionary<string, GameProfile>();
        var issues = new List<string>();

        Assembly asm = typeof(GameCatalog).Assembly;
        foreach (string res in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Games.Defaults.", StringComparison.Ordinal) &&
                                 n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using Stream? s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                using var sr = new StreamReader(s);
                // "Sakir.Games.Defaults.pubg.json" -> "pubg". Path.GetFileName*
                // would treat everything after the last dot as an extension.
                string stem = res[..^".json".Length].Split('.').Last();
                GameProfile? p = GameProfileFile.Parse(sr.ReadToEnd(), stem, out string? err);
                if (p is null) { issues.Add($"built-in {res}: {err}"); Log.Warn($"built-in profile {res} is invalid: {err}"); }
                else byKey[p.Key] = p;
            }
            catch (Exception ex) { Log.Throttled("profile-embedded", $"reading built-in profile {res}", ex); }
        }

        int user = 0;
        if (Directory.Exists(GamesDir))
        {
            foreach (string file in Directory.EnumerateFiles(GamesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // sanity cap: a profile is a few hundred bytes
                    if (new FileInfo(file).Length > 64 * 1024) { issues.Add($"{Path.GetFileName(file)}: file is too large"); Log.Warn($"profile {Path.GetFileName(file)} skipped: file is too large"); continue; }

                    GameProfile? p = GameProfileFile.Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), out string? err);
                    if (p is null) { issues.Add($"{Path.GetFileName(file)}: {err}"); Log.Warn($"profile {Path.GetFileName(file)} skipped: {err}"); continue; }
                    byKey[p.Key] = p;
                    user++;
                }
                catch (Exception ex) { Log.Throttled("profile-" + file, $"reading profile {Path.GetFileName(file)}", ex); }
            }
        }

        _profiles = byKey.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        _issues = issues;
        _gamesStamp = FolderStamp();
        Log.Info($"game profiles: {_profiles.Count} ({user} from the games folder)");
    }

    /// <summary>Newest write time in the games folder, so edits are noticed.</summary>
    private DateTime FolderStamp()
    {
        try
        {
            if (!Directory.Exists(GamesDir)) return DateTime.MinValue;
            DateTime newest = Directory.GetLastWriteTimeUtc(GamesDir);
            foreach (string f in Directory.EnumerateFiles(GamesDir, "*.json"))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch { return _gamesStamp; }
    }

    private void ReloadIfChanged()
    {
        if ((DateTime.UtcNow - _lastStampCheck).TotalSeconds < 2) return;
        _lastStampCheck = DateTime.UtcNow;
        if (FolderStamp() != _gamesStamp) Reload();
    }

    /// <summary>
    /// Creates the games folder and an example profile the first time the user
    /// asks for it. The example is a .txt so it is never loaded as a real profile.
    /// </summary>
    public string EnsureGamesDir()
    {
        Directory.CreateDirectory(GamesDir);
        string example = Path.Combine(GamesDir, "example.json.txt");
        if (!File.Exists(example))
        {
            File.WriteAllText(example,
@"// Copy this file to e.g. mygame.json (remove the .txt) to add a game.
// See docs/GAME-PROFILES.md for every field.
{
  ""name"": ""My Game"",
  ""process"": ""MyGame"",          // process name, as shown in Task Manager, no .exe
  ""steamAppId"": ""123456"",       // optional: used to find artwork in your Steam cache
  ""accent"": ""auto"",             // ""auto"" = colour taken from the artwork, or ""#RRGGBB""
  ""fallbackAccent"": ""#4FA8DB"",  // used if the artwork has no usable colour
  ""background"": [""#0A1420"", ""#142C42""],
  ""motif"": ""scanline""           // none | diagonal | scanline
}
");
        }
        return GamesDir;
    }

    // --------------------------------------------------------------- lookup ---

    public GameProfile? FindRunning()
    {
        ReloadIfChanged();
        IReadOnlyList<GameProfile> profiles = _profiles;
        if (profiles.Count == 0) return null;

        // One process snapshot for all profiles; asking per profile gets slow
        // as the list grows.
        HashSet<string> running;
        Process[] all = Array.Empty<Process>();
        try
        {
            all = Process.GetProcesses();
            running = new HashSet<string>(all.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
        finally { foreach (Process p in all) p.Dispose(); }

        foreach (GameProfile p in profiles)
        {
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            if (running.Contains(p.ProcessName))
            {
                LoadArt(p);
                return p;
            }
        }
        return null;
    }

    public GameProfile? ByKey(string key)
    {
        GameProfile? p = key == "idle" ? Idle : _profiles.FirstOrDefault(x => x.Key == key);
        if (p is not null) LoadArt(p);
        return p;
    }

    // ----------------------------------------------------------------- art ----

    public void LoadArt(GameProfile p)
    {
        if (p.Hero is not null || p.Logo is not null) return;   // cached

        LoadImages(p);

        // Accent comes from the artwork when asked to, else the profile's own.
        if (p.AccentAuto)
        {
            Color? derived = p.Hero is not null ? AccentColor.FromBitmap(p.Hero) : null;
            p.Accent = derived ?? p.FallbackAccent;
        }
    }

    private void LoadImages(GameProfile p)
    {
        // 1. your own image wins
        foreach (string ext in new[] { "jpg", "jpeg", "png" })
        {
            string f = Path.Combine(BackgroundsDir, $"{p.Key}.{ext}");
            if (File.Exists(f)) { p.Hero = ReadBitmap(f); break; }
        }
        foreach (string ext in new[] { "png", "jpg" })
        {
            string f = Path.Combine(BackgroundsDir, $"{p.Key}-logo.{ext}");
            if (File.Exists(f)) { p.Logo = ReadBitmap(f); break; }
        }

        // 2. otherwise Steam's own library cache
        string? steam = SteamRoot();
        if (steam is null || string.IsNullOrEmpty(p.SteamAppId)) return;
        string lc = Path.Combine(steam, "appcache", "librarycache", p.SteamAppId);
        if (!Directory.Exists(lc)) return;

        if (p.Hero is null)
        {
            foreach (string n in new[] { "library_hero.jpg", "library_600x900.jpg", "header.jpg" })
            {
                string f = Path.Combine(lc, n);
                if (File.Exists(f)) { p.Hero = ReadBitmap(f); break; }
            }
        }
        if (p.Logo is null)
        {
            string f = Path.Combine(lc, "logo.png");
            if (File.Exists(f)) p.Logo = ReadBitmap(f);
        }
    }

    private static string? _steamRoot;
    private static bool _steamProbed;

    private static string? SteamRoot()
    {
        if (_steamProbed) return _steamRoot;
        _steamProbed = true;
        try
        {
            object? v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            string? p = v as string;
            if (!string.IsNullOrEmpty(p))
            {
                p = p.Replace('/', '\\');
                if (Directory.Exists(p)) { _steamRoot = p; return p; }
            }
        }
        catch { }
        return _steamRoot;
    }

    /// <summary>Loads through a byte[] so we never hold a lock on Steam's cache.</summary>
    private static Bitmap? ReadBitmap(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using Image img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }
}
