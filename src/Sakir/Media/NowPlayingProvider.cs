using System.Text.RegularExpressions;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Sakir.Media;

public sealed record NowPlayingInfo(
    string Source,
    string Title,
    string Artist,
    string Album,
    string Status,
    TimeSpan Position,
    TimeSpan Duration,
    Bitmap? Art)
{
    public bool IsPlaying => Status == "Playing";
}

/// <summary>
/// Reads whatever Windows is playing from the System Media Transport Controls.
/// Apple Music for Windows registers there as
/// "AppleInc.AppleMusicWin_...!App" and publishes title, artist, album artwork
/// and a live position - so the panel shows real metadata, not scraped pixels.
/// Spotify, browsers and anything else that registers with SMTC work the same.
/// </summary>
public sealed class NowPlayingProvider : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;

    private string _artKey = "";
    private Bitmap? _art;

    // raw timeline anchor, so the bar can advance between SMTC updates
    private TimeSpan _rawPos;
    private DateTime _rawAt = DateTime.MinValue;

    public NowPlayingInfo? Current { get; private set; }

    /// <summary>Size the artwork is cached at - it fills the whole panel.</summary>
    public int ArtSize { get; init; } = 320;

    public async Task StartAsync()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { _mgr = null; }
    }

    public async Task RefreshAsync()
    {
        if (_mgr is null) return;
        try
        {
            GlobalSystemMediaTransportControlsSession? s = _mgr.GetCurrentSession();
            if (s is null) { Current = null; return; }

            GlobalSystemMediaTransportControlsSessionMediaProperties props =
                await s.TryGetMediaPropertiesAsync();
            var timeline = s.GetTimelineProperties();
            var playback = s.GetPlaybackInfo();
            string status = playback.PlaybackStatus.ToString();

            // Apple Music packs "Artist <dash> Album" into the artist field and
            // leaves AlbumTitle empty. \p{Pd} matches any Unicode dash.
            string artist = props.Artist ?? "";
            string album = props.AlbumTitle ?? "";
            Match m = Regex.Match(artist, @"^(.*?)\s+\p{Pd}\s+(.*)$");
            if (m.Success)
            {
                artist = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(album)) album = m.Groups[2].Value.Trim();
            }
            // "<Album> - Single" / "- EP" is Apple naming noise on a 320px panel
            album = Regex.Replace(album, @"\s+\p{Pd}\s+(Single|EP)$", "").Trim();

            string title = props.Title ?? "";
            if (album == title) album = "";     // singles repeat the track name

            // Anchor the position on the value SMTC last published, then let it
            // run on wall-clock so the progress bar moves smoothly between the
            // roughly once-per-second updates.
            if (timeline.Position != _rawPos)
            {
                _rawPos = timeline.Position;
                _rawAt = DateTime.Now;
            }
            TimeSpan pos = _rawPos;
            if (status == "Playing" && _rawAt != DateTime.MinValue)
            {
                TimeSpan d = DateTime.Now - _rawAt;
                if (d > TimeSpan.Zero && d < TimeSpan.FromHours(1)) pos = _rawPos + d;
            }
            if (timeline.EndTime > TimeSpan.Zero && pos > timeline.EndTime) pos = timeline.EndTime;

            string key = title + "|" + props.Artist;
            Bitmap? art = await GetArtAsync(props.Thumbnail, key);

            Current = new NowPlayingInfo(
                FriendlySource(s.SourceAppUserModelId),
                title, artist, album, status, pos, timeline.EndTime, art);
        }
        catch
        {
            // leave Current as-is; a transient SMTC hiccup shouldn't blank the panel
        }
    }

    private static string FriendlySource(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "NOW PLAYING";
        if (aumid.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (aumid.Contains("iTunes", StringComparison.OrdinalIgnoreCase)) return "iTunes";
        if (aumid.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (aumid.Contains("YouTubeMusic", StringComparison.OrdinalIgnoreCase)) return "YouTube Music";
        if (Regex.IsMatch(aumid, "msedge|chrome|firefox", RegexOptions.IgnoreCase)) return "Browser";

        string head = aumid.Split('!', '_', '.')[0];
        return Regex.Replace(head, "(?<!^)(?=[A-Z])", " ").Trim();
    }

    private async Task<Bitmap?> GetArtAsync(IRandomAccessStreamReference? thumb, string key)
    {
        if (key == _artKey) return _art;      // decoding is expensive; only on track change
        _artKey = key;
        _art?.Dispose();
        _art = null;
        if (thumb is null) return null;

        try
        {
            using IRandomAccessStreamWithContentType stream = await thumb.OpenReadAsync();
            uint size = (uint)stream.Size;
            if (size == 0) return null;

            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            byte[] bytes = new byte[size];
            reader.ReadBytes(bytes);
            reader.Dispose();

            using var ms = new MemoryStream(bytes);
            using Image img = Image.FromStream(ms);

            // Apple Music hands out 800x800; downscale once here instead of
            // rescaling on every frame.
            var bmp = new Bitmap(ArtSize, ArtSize);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, ArtSize, ArtSize);
            }
            _art = bmp;
            return _art;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _art?.Dispose();
        _art = null;
    }
}
