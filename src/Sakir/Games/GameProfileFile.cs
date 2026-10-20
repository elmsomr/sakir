using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sakir.Games;

/// <summary>
/// The on-disk shape of a game profile (one JSON file per game) and the
/// validation that turns it into a <see cref="GameProfile"/>.
///
/// Profile files are user-editable input, so nothing in them is trusted. The
/// <c>key</c> in particular is used to build file names
/// (<c>backgrounds\&lt;key&gt;.jpg</c>); unvalidated, a key such as
/// <c>..\..\x</c> would read files outside that folder. Every field is checked
/// here and a bad file is skipped with a reason instead of being half-loaded.
/// </summary>
public static class GameProfileFile
{
    private sealed class Dto
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Process { get; set; }
        public string? SteamAppId { get; set; }
        /// <summary>"#RRGGBB" or "auto" (default): derive from the game's artwork.</summary>
        public string? Accent { get; set; }
        /// <summary>Used when accent is "auto" but there is no usable artwork.</summary>
        public string? FallbackAccent { get; set; }
        /// <summary>Two colours for the gradient shown when there is no artwork.</summary>
        public string[]? Background { get; set; }
        public string? Motif { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // allow // notes in profiles
        AllowTrailingCommas = true
    };

    private static readonly Regex KeyRx = new(@"^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex ProcRx = new(@"^[A-Za-z0-9_. -]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex AppIdRx = new(@"^\d{1,10}$", RegexOptions.Compiled);
    private static readonly string[] Motifs = { "none", "diagonal", "scanline" };

    private static readonly Color DefaultBg1 = Color.FromArgb(255, 12, 12, 18);
    private static readonly Color DefaultBg2 = Color.FromArgb(255, 26, 26, 38);
    private static readonly Color DefaultAccent = Color.FromArgb(255, 255, 60, 90);

    /// <summary>Parses one profile. Returns null and sets <paramref name="error"/> if it is unusable.</summary>
    public static GameProfile? Parse(string json, string fileStem, out string? error)
    {
        error = null;
        Dto? d;
        try { d = JsonSerializer.Deserialize<Dto>(json, Options); }
        catch (JsonException ex) { error = "not valid JSON: " + ex.Message; return null; }
        if (d is null) { error = "empty file"; return null; }

        string key = (d.Key ?? fileStem).Trim().ToLowerInvariant();
        if (!KeyRx.IsMatch(key))
        {
            error = $"key '{key}' must be 1-32 characters of a-z, 0-9, '-' or '_'";
            return null;
        }

        string proc = (d.Process ?? "").Trim();
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc[..^4];
        if (!ProcRx.IsMatch(proc))
        {
            error = "'process' is required: the game's process name, e.g. \"cs2\" (no path)";
            return null;
        }

        string appId = (d.SteamAppId ?? "").Trim();
        if (appId.Length > 0 && !AppIdRx.IsMatch(appId))
        {
            error = "'steamAppId' must be digits only";
            return null;
        }

        string motif = (d.Motif ?? "none").Trim().ToLowerInvariant();
        if (!Motifs.Contains(motif)) motif = "none";

        string name = (d.Name ?? key).Trim();
        if (name.Length == 0) name = key;
        if (name.Length > 40) name = name[..40];

        Color bg1 = DefaultBg1, bg2 = DefaultBg2;
        if (d.Background is { Length: >= 1 } bgs)
        {
            if (!TryHex(bgs[0], out bg1)) { error = $"background colour '{bgs[0]}' is not #RRGGBB"; return null; }
            bg2 = bg1;
            if (bgs.Length >= 2 && !TryHex(bgs[1], out bg2)) { error = $"background colour '{bgs[1]}' is not #RRGGBB"; return null; }
        }

        Color fallback = DefaultAccent;
        if (!string.IsNullOrWhiteSpace(d.FallbackAccent) && !TryHex(d.FallbackAccent, out fallback))
        {
            error = $"fallbackAccent '{d.FallbackAccent}' is not #RRGGBB";
            return null;
        }

        string accentText = (d.Accent ?? "auto").Trim();
        bool auto = accentText.Equals("auto", StringComparison.OrdinalIgnoreCase);
        Color accent = fallback;
        if (!auto && !TryHex(accentText, out accent))
        {
            error = $"accent '{accentText}' must be \"auto\" or #RRGGBB";
            return null;
        }

        return new GameProfile
        {
            Key = key,
            Name = name,
            ProcessName = proc,
            SteamAppId = appId,
            Bg1 = bg1,
            Bg2 = bg2,
            Motif = motif,
            AccentAuto = auto,
            FallbackAccent = fallback,
            Accent = accent
        };
    }

    /// <summary>Accepts #RRGGBB or #AARRGGBB.</summary>
    public static bool TryHex(string? text, out Color colour)
    {
        colour = Color.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('#');
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        colour = s.Length == 6
            ? Color.FromArgb(255, (int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF))
            : Color.FromArgb((int)(v >> 24 & 0xFF), (int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF));
        return true;
    }
}
