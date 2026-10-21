using System.Drawing.Drawing2D;
using Sakir.Games;

namespace Sakir.Scenes;

/// <summary>
/// Sensor readout on the running game's own artwork. GPU temperature gets the
/// large number because that is what matters while playing; CPU temperature
/// appears only when the app is elevated (LibreHardwareMonitor needs it).
/// </summary>
public sealed class GameScene : IScene
{
    public string Id => "game";
    public string DisplayName => "Game";

    public bool CanRender(SceneContext ctx) => ctx.Game is not null;

    public void Render(Graphics g, SceneContext ctx)
    {
        int W = ctx.Width, H = ctx.Height;
        GameProfile p = ctx.Game!;
        var rect = new Rectangle(0, 0, W, H);

        // ---- background ---------------------------------------------------
        if (p.Hero is not null)
        {
            Draw.DrawCover(g, p.Hero, W, H);
            // this screen carries a lot of small text - legibility beats the picture
            using (var dark = new SolidBrush(Color.FromArgb(145, 0, 0, 0)))
                g.FillRectangle(dark, rect);
            Draw.VerticalScrim(g, 0, 130, W, H - 130, 0, 190);
        }
        else
        {
            using (var bg = new LinearGradientBrush(rect, p.Bg1, p.Bg2, 55f))
                g.FillRectangle(bg, rect);

            if (p.Motif == "diagonal")
            {
                using var pen = new Pen(Color.FromArgb(18, 255, 255, 255), 7);
                for (int x = -260; x < W; x += 28) g.DrawLine(pen, x, H, x + 260, 0);
            }
            else if (p.Motif == "scanline")
            {
                using var pen = new Pen(Color.FromArgb(15, 255, 255, 255), 1);
                for (int y = 0; y < H; y += 4) g.DrawLine(pen, 0, y, W, y);
            }
        }

        using var accent = new SolidBrush(p.Accent);
        using var white = new SolidBrush(Color.White);
        using var mute = new SolidBrush(Color.FromArgb(165, 255, 255, 255));
        using var fLabel = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var fValue = new Font("Segoe UI", 11, FontStyle.Bold);
        // Proportional to the logical height: a fixed 44pt would keep its pixel
        // size while the canvas shrinks under a higher text scale, and start
        // colliding with the line underneath it.
        // Also bounded by width: a portrait panel is tall and narrow, and a
        // height-only size would run off the side.
        using var fHuge = new Font("Segoe UI", Math.Min(ctx.Height * 0.175f, ctx.Width * 0.30f), FontStyle.Bold);
        using var fTiny = new Font("Segoe UI", 8);
        using StringFormat right = Draw.RightAligned();

        // ---- header: the game's own logo when Steam has it -----------------
        if (p.Logo is not null)
        {
            const int maxW = 160, maxH = 26;
            double s = Math.Min((double)maxW / p.Logo.Width, (double)maxH / p.Logo.Height);
            int lw = (int)(p.Logo.Width * s), lh = (int)(p.Logo.Height * s);
            g.DrawImage(p.Logo, 13, 6 + (maxH - lh) / 2, lw, lh);
        }
        else
        {
            using var fHead = new Font("Segoe UI", 12, FontStyle.Bold);
            g.DrawString(p.Name, fHead, white, 13, 7);
        }
        g.FillRectangle(accent, 14, 30, W - 28, 2);

        // Bottom block is anchored to the bottom EDGE, not a fraction of H, so
        // no band of dead pixels forms under the last bar. Everything above
        // then gets whatever is left.
        int barH = Math.Max(5, (int)(H * 0.03));
        int ramBarY = H - 12 - barH;
        int ramLabelY = ramBarY - 17;
        int cpuBarY = ramLabelY - 16 - barH;
        int cpuLabelY = cpuBarY - 17;
        int bottomTop = cpuLabelY - 6;
        int midTop = (int)(H * 0.135) + 8;

        // ---- FPS: the number you actually stare at while playing -----------
        g.DrawString("FPS", fLabel, accent, 15, midTop - 2);
        int fps = ctx.Fps?.Fps ?? 0;
        int hugeY = midTop + 4;
        if (fps > 0)
        {
            g.DrawString(fps.ToString(), fHuge, white, 8, hugeY);
        }
        else
        {
            using var faded = new SolidBrush(Color.FromArgb(110, 255, 255, 255));
            g.DrawString("--", fHuge, faded, 8, hugeY);
        }

        // Second line under the FPS number. Coolant temperature earns this spot
        // over GPU hotspot: it is an AIO, and under load the coolant is what
        // tells you whether the loop is keeping up.
        string subLine;
        if (fps <= 0)
        {
            subLine = ctx.Fps is null ? ""
                : ctx.Fps.DetectorRunning ? "no game rendering" : "starting detector";
        }
        else if (ctx.Pump is { Available: true } p2)
        {
            subLine = $"LIQUID  {p2.LiquidTemp}{Draw.Deg}C";
        }
        else
        {
            subLine = ctx.Gpu.Available && ctx.Gpu.HasHotspotAndPower ? $"HOTSPOT  {ctx.Gpu.Hotspot}{Draw.Deg}C" : "";
        }

        // Portrait (hoses up/down mount, 240x320): the figures cannot sit in a
        // column beside the FPS number, so they stack underneath it, full width.
        bool portrait = H > W;
        // From the font size rather than MeasureString: for a large font that
        // returns the full line box, which is about half again taller than the
        // digits, and the wasted gap pushed rows off the bottom at 1.25x.
        // Digits end roughly 1.1 em below where the text is drawn.
        int hugeBottom = hugeY + (int)(fHuge.Size * 96f / 72f * 1.1f);

        if (subLine.Length > 0)
            g.DrawString(subLine, fTiny, mute, 15, portrait ? hugeBottom : bottomTop - 16);

        // ---- figures: temperatures first -------------------------------------
        int rx, rw, ry, rowStep;
        if (portrait)
        {
            rx = 14; rw = W - 28;
            ry = hugeBottom + 22;
            rowStep = 20;
        }
        else
        {
            rx = (int)(W * 0.525); rw = W - rx - 14; ry = midTop + 4;
            rowStep = Math.Max(17, (bottomTop - ry - 6) / 4);
        }
        var rows = new List<(string Label, string Value, Color Colour)>();

        // Ordered by what actually matters mid-game, because the last rows get
        // dropped when the text scale leaves no room for them.
        if (ctx.Gpu.Available)
            rows.Add(("GPU", $"{ctx.Gpu.Temp}{Draw.Deg}C", Draw.TempColor(ctx.Gpu.Temp, Draw.Warning)));
        if (ctx.System.CpuTempAvailable)
            rows.Add(("CPU", $"{ctx.System.CpuTemp:0}{Draw.Deg}C", Draw.TempColor(ctx.System.CpuTemp, Draw.Warning)));
        if (ctx.Pump is { Available: true } pump)
        {
            rows.Add(pump.PumpStopped
                ? ("PUMP", "STOPPED", Color.FromArgb(255, 255, 70, 70))
                : ("PUMP", $"{pump.PumpRpm}", Color.White));
        }
        if (ctx.Gpu.Available)
        {
            rows.Add(("USAGE", $"{ctx.Gpu.Usage} %", Color.White));
            if (ctx.Gpu.HasHotspotAndPower)
                rows.Add(("POWER", $"{ctx.Gpu.Watts} W", Color.White));
        }
        if (rows.Count == 0)
            rows.Add(("SENSORS", "n/a", Color.White));

        foreach ((string label, string value, Color colour) in rows)
        {
            // drop rows that no longer fit rather than drawing over the bars
            if (ry + 18 > bottomTop) break;
            g.DrawString(label, fLabel, mute, rx, ry + 3);
            using var bv = new SolidBrush(colour);
            g.DrawString(value, fValue, bv, new RectangleF(rx, ry, rw, 20), right);
            ry += rowStep;
        }

        // ---- CPU / RAM -----------------------------------------------------
        int bx = 14, bw = W - 28;

        string cpuLabel = ctx.System.CpuTempAvailable
            ? $"CPU  {ctx.System.CpuTemp:0}{Draw.Deg}C"
            : "CPU";
        g.DrawString(cpuLabel, fLabel, mute, bx, cpuLabelY);
        g.DrawString($"{ctx.System.CpuLoad:0} %", fLabel, white,
            new RectangleF(bx, cpuLabelY, bw, 18), right);
        Draw.Bar(g, bx, cpuBarY, bw, barH, ctx.System.CpuLoad / 100.0, p.Accent);

        g.DrawString("RAM", fLabel, mute, bx, ramLabelY);
        g.DrawString($"{ctx.System.RamUsedGb:0.0} / {ctx.System.RamTotalGb:0.0} GB",
            fLabel, white, new RectangleF(bx, ramLabelY, bw, 18), right);
        Draw.Bar(g, bx, ramBarY, bw, barH, ctx.System.RamPercent / 100.0, p.Accent);
    }
}
