using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sakir.Core;

/// <summary>
/// Settings, stored next to the executable so the whole thing stays portable
/// (copy the folder to another machine and it keeps its configuration).
/// </summary>
public sealed class AppConfig
{
    public string Mode { get; set; } = "auto";
    public int Fps { get; set; } = 5;
    public int Brightness { get; set; } = 100;

    /// <summary>Screen area for mirror mode; all zero means the whole screen.</summary>
    public int MirrorX { get; set; }
    public int MirrorY { get; set; }
    public int MirrorWidth { get; set; }
    public int MirrorHeight { get; set; }

    /// <summary>
    /// Window to mirror. Stored as process name plus title rather than a window
    /// handle, because handles do not survive the target app being restarted.
    /// Empty process name means mirror the screen instead.
    /// </summary>
    public string MirrorProcess { get; set; } = "";
    public string MirrorTitle { get; set; } = "";

    /// <summary>
    /// Links shown in the Support section. Each button is hidden while its URL
    /// is empty, so a fork with no funding page simply does not show one.
    /// </summary>
    public string DonateUrl { get; set; } = "";
    public string ProjectUrl { get; set; } = "";

    /// <summary>
    /// How much bigger everything on the panel is drawn. 1.0 is the original
    /// layout; higher values scale text and layout together, so less fits but
    /// it is far easier to read across a room.
    /// </summary>
    public double TextScale { get; set; } = 1.15;

    [JsonIgnore]
    public Rectangle MirrorRegion
    {
        get => new(MirrorX, MirrorY, MirrorWidth, MirrorHeight);
        set
        {
            MirrorX = value.X; MirrorY = value.Y;
            MirrorWidth = value.Width; MirrorHeight = value.Height;
        }
    }

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Sakir.config.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { /* a corrupt config should never stop the app from starting */ }
        return new AppConfig();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch { /* read-only install folder - run from a writable location to persist */ }
    }
}
