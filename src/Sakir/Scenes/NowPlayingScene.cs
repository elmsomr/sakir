using System.Drawing.Drawing2D;

namespace Sakir.Scenes;

/// <summary>
/// Album art edge to edge, with a source badge, track details and a live
/// progress bar over a scrim.
/// </summary>
public sealed class NowPlayingScene : IScene
{
    public string Id => "nowplaying";
    public string DisplayName => "Now playing";

    public bool CanRender(SceneContext ctx) =>
        ctx.NowPlaying is { } np && !string.IsNullOrWhiteSpace(np.Title);

    public void Render(Graphics g, SceneContext ctx)
    {
        int W = ctx.Width, H = ctx.Height;
        var np = ctx.NowPlaying;

        // A player that is open but idle still reports a session (status
        // "Opened", empty title, zero duration). Drawing that gives an empty
        // shell that reads as broken, so treat it as nothing playing. Written
        // inline rather than via a flag so the compiler can see np is non-null
        // after this block.
        if (np is null || (string.IsNullOrWhiteSpace(np.Title) && np.Duration == TimeSpan.Zero))
        {
            using var bgIdle = new SolidBrush(Color.FromArgb(255, 12, 12, 18));
            g.FillRectangle(bgIdle, 0, 0, W, H);
            using var fIdle = new Font("Segoe UI", 10, FontStyle.Bold);
            using var bIdle = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
            using var sfc = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("NOTHING PLAYING", fIdle, bIdle, new RectangleF(0, 0, W, H), sfc);
            return;
        }

        // ---- full-bleed artwork ------------------------------------------
        if (np.Art is not null)
        {
            Draw.DrawCover(g, np.Art, W, H);
        }
        else
        {
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                Color.FromArgb(255, 26, 14, 42), Color.FromArgb(255, 8, 8, 12), 90f);
            g.FillRectangle(bg, 0, 0, W, H);
        }

        // scrims keep the text readable over any cover
        int scrimTop = (int)(H * 0.40);
        Draw.VerticalScrim(g, 0, 0, W, 60, 175, 0);
        Draw.VerticalScrim(g, 0, scrimTop, W, H - scrimTop, 0, 238);

        // ---- source badge -------------------------------------------------
        const int bx = 13, by = 11, bs = 26;
        using (GraphicsPath badge = Draw.RoundedRect(bx, by, bs, bs, 7))
        using (var bb = new LinearGradientBrush(new Rectangle(bx, by, bs, bs),
                   Color.FromArgb(255, 250, 45, 85), Color.FromArgb(255, 205, 25, 105), 90f))
        {
            g.FillPath(bb, badge);
        }

        // hand-drawn note: a symbol font glyph would render as tofu if missing
        using (var note = new SolidBrush(Color.White))
        {
            g.FillEllipse(note, bx + 6, by + 14, 7, 5.5f);
            g.FillEllipse(note, bx + 14, by + 12, 7, 5.5f);
            g.FillRectangle(note, bx + 12, by + 6, 1.6f, 10);
            g.FillRectangle(note, bx + 20, by + 4, 1.6f, 10);
            g.FillRectangle(note, bx + 12, by + 5, 9.6f, 2.4f);
        }

        using var white = new SolidBrush(Color.White);
        string source = np.IsPlaying ? np.Source : $"{np.Source}  -  {np.Status}";
        using (var fSrc = new Font("Segoe UI", 10, FontStyle.Bold))
            g.DrawString(source, fSrc, white, bx + bs + 8, by + 4);

        // ---- track --------------------------------------------------------
        const int tx = 14;
        int tw = W - 28;
        using StringFormat sf = Draw.NoWrapEllipsis();

        // anchored to the bottom, so a bigger text scale (which shrinks the
        // logical canvas) moves these up instead of off the panel
        int titleY = H - 102, subY = H - 74;

        using (var fTitle = new Font("Segoe UI", 16, FontStyle.Bold))
            g.DrawString(np.Title, fTitle, white, new RectangleF(tx, titleY, tw, 30), sf);

        string sub = string.IsNullOrEmpty(np.Album) ? np.Artist : $"{np.Artist}  -  {np.Album}";
        using (var fSub = new Font("Segoe UI", 10))
        using (var bSub = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            g.DrawString(sub, fSub, bSub, new RectangleF(tx, subY, tw, 22), sf);

        // ---- progress -----------------------------------------------------
        int py = H - 36;
        double frac = np.Duration.TotalSeconds > 0
            ? np.Position.TotalSeconds / np.Duration.TotalSeconds
            : 0;
        Draw.Bar(g, tx, py, tw, 4, frac, Color.White);
        int knob = tx + (int)(tw * Math.Clamp(frac, 0, 1));
        g.FillEllipse(white, knob - 4, py - 3, 10, 10);

        using var fTime = new Font("Segoe UI", 8);
        using var bTime = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        using StringFormat right = Draw.RightAligned();
        g.DrawString(Fmt(np.Position), fTime, bTime, tx, py + 9);
        g.DrawString(Fmt(np.Duration), fTime, bTime, new RectangleF(tx, py + 9, tw, 18), right);
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
