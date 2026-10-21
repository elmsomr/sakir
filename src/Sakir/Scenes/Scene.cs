using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Sakir.Games;
using Sakir.Media;
using Sakir.Sensors;

namespace Sakir.Scenes;

public sealed class SceneContext
{
    /// <summary>
    /// Logical canvas size, NOT the panel's pixel size. When the text scale is
    /// above 1 the renderer applies a ScaleTransform and shrinks this, so a
    /// scene that positions things relative to Width/Height keeps working while
    /// everything - text included - comes out bigger on the glass.
    ///
    /// Scenes should anchor to these rather than hardcoding 320/240, and anchor
    /// bottom rows to Height - n rather than absolute y.
    /// </summary>
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 240;
    public required SystemSensors System { get; init; }
    public required GpuSensors Gpu { get; init; }
    public FpsProvider? Fps { get; init; }
    public PumpSensors? Pump { get; init; }
    public NowPlayingInfo? NowPlaying { get; set; }
    public GameProfile? Game { get; set; }
    /// <summary>Seconds since the loop started - drive animation from this.</summary>
    public double Time { get; set; }
}

public interface IScene
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>False when the scene has nothing to show (no music playing, no
    /// game running), so the director can fall through to the next one.</summary>
    bool CanRender(SceneContext ctx);
    void Render(Graphics g, SceneContext ctx);
}

/// <summary>Shared drawing helpers. The degree sign is built from a char code so
/// every source file in this project stays pure ASCII.</summary>
public static class Draw
{
    public static readonly string Deg = ((char)0x00B0).ToString();

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    public static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Scales an image to cover the target and centre-crops the rest.</summary>
    public static void DrawCover(Graphics g, Image img, int w, int h)
    {
        double s = Math.Max((double)w / img.Width, (double)h / img.Height);
        int dw = (int)(img.Width * s), dh = (int)(img.Height * s);
        g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
    }

    public static void VerticalScrim(Graphics g, int x, int y, int w, int h, int topAlpha, int bottomAlpha)
    {
        var r = new Rectangle(x, y, w, h);
        using var b = new LinearGradientBrush(r,
            Color.FromArgb(topAlpha, 0, 0, 0), Color.FromArgb(bottomAlpha, 0, 0, 0), 90f);
        // Without this GDI+ can paint a thin dark line at the gradient's first
        // row (the brush tiles, and the seam picks up the far colour). It showed
        // up across the middle of the portrait music screen.
        b.WrapMode = WrapMode.TileFlipXY;
        g.FillRectangle(b, r);
    }

    public static void Bar(Graphics g, int x, int y, int w, int h, double fraction, Color fill)
    {
        using var track = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
        g.FillRectangle(track, x, y, w, h);
        int fw = (int)(w * Math.Clamp(fraction, 0, 1));
        if (fw <= 0) return;
        using var b = new SolidBrush(fill);
        g.FillRectangle(b, x, y, fw, h);
    }

    public static StringFormat NoWrapEllipsis() => new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    public static StringFormat RightAligned() => new() { Alignment = StringAlignment.Far };

    /// <summary>White below 75 C, amber approaching the limit, red past 85 C.</summary>
    /// <summary>
    /// Fixed warning colour. It must not follow a game's accent: with an
    /// auto-picked blue or green accent, "getting hot" would stop looking like a
    /// warning.
    /// </summary>
    public static readonly Color Warning = Color.FromArgb(255, 255, 190, 60);

    public static Color TempColor(double c, Color warm) =>
        c >= 85 ? Color.FromArgb(255, 255, 70, 70) : c >= 75 ? warm : Color.White;
}
