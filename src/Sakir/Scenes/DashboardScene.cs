using System.Drawing.Drawing2D;

namespace Sakir.Scenes;

/// <summary>
/// The idle screen: matching CPU and GPU gauges side by side, coolant
/// temperature and a small clock beneath them, memory along the bottom.
///
/// Each gauge shows two things at once — the arc is utilisation, the number in
/// the middle is temperature. That is denser than it sounds: at a glance the arc
/// tells you how hard it is working and the colour of the number tells you
/// whether that is a problem.
/// </summary>
public sealed class DashboardScene : IScene
{
    public string Id => "dashboard";
    public string DisplayName => "Dashboard";
    public bool CanRender(SceneContext ctx) => true;      // always a valid fallback

    private static readonly Color Accent = Color.FromArgb(255, 255, 60, 90);
    private static readonly Color Track = Color.FromArgb(255, 38, 40, 58);

    public void Render(Graphics g, SceneContext ctx)
    {
        int W = ctx.Width, H = ctx.Height;
        var rect = new Rectangle(0, 0, W, H);

        using (var bg = new LinearGradientBrush(rect,
                   Color.FromArgb(255, 10, 12, 20), Color.FromArgb(255, 24, 12, 42), 90f))
            g.FillRectangle(bg, rect);

        using var accent = new SolidBrush(Accent);
        using var mute = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
        using var white = new SolidBrush(Color.White);
        using var fLabel = new Font("Segoe UI", 9, FontStyle.Bold);
        using var fTiny = new Font("Segoe UI", 8);
        using StringFormat centre = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using StringFormat right = Draw.RightAligned();

        // ---- vertical budget, from the bottom up ---------------------------
        int barH = Math.Max(6, (int)(H * 0.033));
        int by = H - 12 - barH;          // memory bar
        int infoY = by - 40;             // liquid + clock line
        int floor = infoY - 4;           // gauges must stay above this

        // ---- two gauges ----------------------------------------------------
        int cyG = (24 + floor) / 2;
        // bounded by height and by width, so the two rings never touch
        int r = Math.Min((infoY - 28) / 2 - 2, (int)(W * 0.19));
        int cxCpu = (int)(W * 0.27), cxGpu = (int)(W * 0.73);

        // CPU: arc is load, number is temperature when we can read it
        string cpuBig = ctx.System.CpuTempAvailable ? $"{ctx.System.CpuTemp:0}" : $"{ctx.System.CpuLoad:0}";
        string cpuSub = ctx.System.CpuTempAvailable
            ? $"{Draw.Deg}C   {ctx.System.CpuLoad:0}%"
            : "% load";
        Color cpuColour = ctx.System.CpuTempAvailable
            ? Draw.TempColor(ctx.System.CpuTemp, Accent)
            : Color.White;
        Gauge(g, cxCpu, cyG, r, "CPU", cpuBig, cpuSub, ctx.System.CpuLoad / 100.0, cpuColour,
              fLabel, fTiny, mute, centre);

        if (ctx.Gpu.Available)
        {
            Gauge(g, cxGpu, cyG, r, "GPU", $"{ctx.Gpu.Temp}", $"{Draw.Deg}C   {ctx.Gpu.Usage}%",
                  ctx.Gpu.Usage / 100.0, Draw.TempColor(ctx.Gpu.Temp, Accent),
                  fLabel, fTiny, mute, centre);
        }
        else
        {
            Gauge(g, cxGpu, cyG, r, "GPU", "--", "no sensors", 0, Color.FromArgb(120, 255, 255, 255),
                  fLabel, fTiny, mute, centre);
        }

        // ---- coolant, pump and clock on one compact line ---------------------
        // The clock used to be a large block that dominated the panel. It is
        // useful but it is not why you look at this screen, so it gets a corner.
        string clock = DateTime.Now.ToString("HH:mm");
        g.DrawString(clock, fLabel, mute, new RectangleF(14, infoY, W - 28, 16), right);

        if (ctx.Pump is { Available: true } pump)
        {
            using var bLiquid = new SolidBrush(Draw.TempColor(pump.LiquidTemp, Accent));
            string liquid = $"LIQUID  {pump.LiquidTemp}{Draw.Deg}C";
            g.DrawString(liquid, fLabel, bLiquid, 14, infoY);

            // Pump speed goes in the middle when it fits. A stopped pump is
            // called out in red rather than shown as a quiet zero.
            string rpm = pump.PumpStopped ? "PUMP STOPPED" : $"PUMP  {pump.PumpRpm}";
            float used = 14 + g.MeasureString(liquid, fLabel).Width
                            + g.MeasureString(clock, fLabel).Width + 14;
            float free = W - used;
            float rpmW = g.MeasureString(rpm, fTiny).Width;

            if (rpmW + 16 < free)
            {
                using var bRpm = new SolidBrush(pump.PumpStopped
                    ? Color.FromArgb(255, 255, 70, 70)
                    : Color.FromArgb(170, 255, 255, 255));
                g.DrawString(rpm, pump.PumpStopped ? fLabel : fTiny, bRpm,
                    new RectangleF(14, infoY + 1, W - 28, 16), centre);
            }
        }

        // ---- memory ---------------------------------------------------------
        g.DrawString("MEMORY", fTiny, mute, 14, by - 16);
        g.DrawString($"{ctx.System.RamUsedGb:0.0} / {ctx.System.RamTotalGb:0.0} GB",
            fTiny, mute, new RectangleF(14, by - 16, W - 28, 16), right);
        Draw.Bar(g, 14, by, W - 28, barH, ctx.System.RamPercent / 100.0, Accent);

        // a slow pulse so it is obvious the panel is live, not a stored image
        float pulse = (float)(6 + 4 * Math.Sin(ctx.Time * 1.1));
        g.FillEllipse(accent, W - 18 - pulse / 2, 12 - pulse / 2, pulse, pulse);
    }

    /// <summary>One ring: label above, arc for the rate, value in the middle.</summary>
    private static void Gauge(Graphics g, int cx, int cy, int r,
                              string label, string big, string sub,
                              double fraction, Color valueColour,
                              Font fLabel, Font fTiny, Brush mute, StringFormat centre)
    {
        g.DrawString(label, fLabel, mute, new RectangleF(cx - 60, cy - r - 17, 120, 14), centre);

        int thickness = Math.Max(7, r / 5);
        using (var back = new Pen(Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(back, cx - r, cy - r, 2 * r, 2 * r, 135, 270);

        float sweep = (float)(270.0 * Math.Clamp(fraction, 0, 1));
        if (sweep > 0)
        {
            using var arc = new Pen(Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, cx - r, cy - r, 2 * r, 2 * r, 135, sweep);
        }

        // sized off the ring so it keeps fitting as the canvas shrinks
        using var fBig = new Font("Segoe UI", Math.Max(14f, r * 0.55f), FontStyle.Bold);
        using var bValue = new SolidBrush(valueColour);
        g.DrawString(big, fBig, bValue, new RectangleF(cx - r, cy - r * 0.55f, 2 * r, r * 0.95f), centre);
        g.DrawString(sub, fTiny, mute, new RectangleF(cx - r, cy + r * 0.34f, 2 * r, 14), centre);
    }
}
