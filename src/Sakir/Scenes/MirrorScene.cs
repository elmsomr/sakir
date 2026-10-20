using Sakir.Capture;

namespace Sakir.Scenes;

/// <summary>
/// Mirrors either a chosen window or a screen region onto the panel,
/// letterboxed.
///
/// Window capture is the useful mode: it redraws the target window into our own
/// buffer, so the window can sit behind everything else and still show up on the
/// panel. Screen capture is kept for cases where you want a fixed area of the
/// desktop regardless of which window is there.
///
/// This is also the only route for arbitrary moving content - the firmware
/// rejects device-side video on this model, so pushed frames are the ceiling.
/// Audio stays on the PC.
/// </summary>
public sealed class MirrorScene : IScene
{
    public string Id => "mirror";
    public string DisplayName => "Screen mirror";
    public bool CanRender(SceneContext ctx) => true;

    /// <summary>Empty means the whole primary screen.</summary>
    public Rectangle Region { get; set; } = Rectangle.Empty;

    /// <summary>Process name of the window to mirror; empty means capture the screen.</summary>
    public string TargetProcess { get; set; } = "";
    public string TargetTitle { get; set; } = "";

    private Bitmap? _capture;
    private IntPtr _hwnd;
    private DateTime _lastResolve = DateTime.MinValue;

    public void Render(Graphics g, SceneContext ctx)
    {
        g.Clear(Color.Black);
        // bilinear, not bicubic: this runs every frame on a full-size grab
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;

        Bitmap? source;
        bool ownsSource;

        if (!string.IsNullOrWhiteSpace(TargetProcess))
        {
            source = CaptureTargetWindow();
            ownsSource = true;
            if (source is null)
            {
                DrawWaiting(g, ctx);
                return;
            }
        }
        else
        {
            source = CaptureScreen();
            ownsSource = false;
            if (source is null) return;
        }

        try
        {
            double s = Math.Min((double)ctx.Width / source.Width, (double)ctx.Height / source.Height);
            int dw = (int)(source.Width * s), dh = (int)(source.Height * s);
            g.DrawImage(source, (ctx.Width - dw) / 2, (ctx.Height - dh) / 2, dw, dh);
        }
        finally { if (ownsSource) source.Dispose(); }
    }

    private Bitmap? CaptureTargetWindow()
    {
        // Handles die when the app closes; re-resolve periodically rather than
        // giving up permanently.
        if (_hwnd == IntPtr.Zero && (DateTime.Now - _lastResolve).TotalSeconds > 2)
        {
            _lastResolve = DateTime.Now;
            _hwnd = WindowCapture.Resolve(TargetProcess, TargetTitle);
        }
        if (_hwnd == IntPtr.Zero) return null;

        Bitmap? shot = WindowCapture.Capture(_hwnd);
        if (shot is null) _hwnd = IntPtr.Zero;   // gone or minimised - look again
        return shot;
    }

    private Bitmap? CaptureScreen()
    {
        Rectangle src = Region;
        if (src.Width <= 0 || src.Height <= 0)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                    ?? new Rectangle(0, 0, 1920, 1080);
            src = new Rectangle(b.X, b.Y, b.Width, b.Height);
        }

        if (_capture is null || _capture.Width != src.Width || _capture.Height != src.Height)
        {
            _capture?.Dispose();
            _capture = new Bitmap(src.Width, src.Height);
        }

        try
        {
            using Graphics cg = Graphics.FromImage(_capture);
            cg.CopyFromScreen(src.X, src.Y, 0, 0, new Size(src.Width, src.Height));
            return _capture;
        }
        catch { return null; }
    }

    private void DrawWaiting(Graphics g, SceneContext ctx)
    {
        using var f = new Font("Segoe UI", 10, FontStyle.Bold);
        using var fs = new Font("Segoe UI", 9);
        using var b = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        using var d = new SolidBrush(Color.FromArgb(130, 255, 255, 255));
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString($"waiting for {TargetProcess}",
            f, b, new RectangleF(10, ctx.Height / 2f - 22, ctx.Width - 20, 22), sf);
        g.DrawString("not running, or minimised",
            fs, d, new RectangleF(10, ctx.Height / 2f + 4, ctx.Width - 20, 20), sf);
    }
}
