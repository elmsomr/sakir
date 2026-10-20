using Microsoft.Win32;

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
    public required Color Accent { get; init; }
    public string Motif { get; init; } = "none";

    public Bitmap? Hero { get; set; }
    public Bitmap? Logo { get; set; }
}

/// <summary>
/// Game profiles plus their artwork.
///
/// Artwork is never downloaded: Steam already keeps each game's hero banner and
/// logo on disk under appcache\librarycache\&lt;appid&gt;\, and a user-supplied
/// image in the backgrounds folder overrides it.
/// </summary>
public sealed class GameCatalog
{
    public string BackgroundsDir { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "backgrounds");

    private readonly List<GameProfile> _profiles = new()
    {
        new GameProfile
        {
            Key = "pubg", Name = "PUBG: BATTLEGROUNDS",
            ProcessName = "TslGame", SteamAppId = "578080",
            Bg1 = Color.FromArgb(255, 20, 26, 15), Bg2 = Color.FromArgb(255, 44, 42, 24),
            Accent = Color.FromArgb(255, 232, 163, 61), Motif = "diagonal"
        },
        new GameProfile
        {
            Key = "cs2", Name = "COUNTER-STRIKE 2",
            ProcessName = "cs2", SteamAppId = "730",
            Bg1 = Color.FromArgb(255, 10, 20, 32), Bg2 = Color.FromArgb(255, 20, 44, 66),
            Accent = Color.FromArgb(255, 79, 168, 219), Motif = "scanline"
        }
    };

    public GameProfile Idle { get; } = new()
    {
        Key = "idle", Name = "SYSTEM", ProcessName = "",
        Bg1 = Color.FromArgb(255, 12, 12, 18), Bg2 = Color.FromArgb(255, 26, 26, 38),
        Accent = Color.FromArgb(255, 255, 60, 90), Motif = "none"
    };

    public IReadOnlyList<GameProfile> Profiles => _profiles;

    public GameProfile? FindRunning()
    {
        foreach (GameProfile p in _profiles)
        {
            if (string.IsNullOrEmpty(p.ProcessName)) continue;
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(p.ProcessName).Length > 0)
                {
                    LoadArt(p);
                    return p;
                }
            }
            catch { }
        }
        return null;
    }

    public GameProfile? ByKey(string key)
    {
        GameProfile? p = key == "idle" ? Idle : _profiles.FirstOrDefault(x => x.Key == key);
        if (p is not null) LoadArt(p);
        return p;
    }

    public void LoadArt(GameProfile p)
    {
        if (p.Hero is not null || p.Logo is not null) return;   // cached

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
