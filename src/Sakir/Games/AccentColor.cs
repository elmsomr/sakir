using System.Drawing.Imaging;

namespace Sakir.Games;

/// <summary>
/// Picks an accent colour from a game's artwork.
///
/// An average of all pixels is the wrong tool: most game art is dark and
/// desaturated, so the mean comes out a muddy brown. Instead this finds the
/// hue that carries the most *vivid* pixels (weighted by saturation and
/// brightness), then returns that hue at a strength that stays readable on the
/// dark overlay the game screen draws over the artwork.
///
/// Returns null for art with no real colour (greyscale, near-black), so the
/// caller can fall back to the profile's own colour rather than invent one.
/// </summary>
public static class AccentColor
{
    private const int Bins = 24;           // 15 degrees each
    private const int SampleW = 64, SampleH = 36;

    public static Color? FromBitmap(Bitmap source)
    {
        try
        {
            using var small = new Bitmap(SampleW, SampleH, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, 0, 0, SampleW, SampleH);
            }

            BitmapData data = small.LockBits(new Rectangle(0, 0, SampleW, SampleH),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] px = new byte[Math.Abs(data.Stride) * SampleH];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            small.UnlockBits(data);

            var weight = new double[Bins];
            var hueSum = new double[Bins];     // weighted hue offset within the bin
            var satSum = new double[Bins];
            double total = 0;

            for (int y = 0; y < SampleH; y++)
            {
                for (int x = 0; x < SampleW; x++)
                {
                    int i = y * data.Stride + x * 4;
                    ToHsv(px[i + 2], px[i + 1], px[i], out double h, out double s, out double v);

                    // ignore greys and near-black: they have no meaningful hue
                    if (s < 0.30 || v < 0.30) continue;

                    double w = s * v;
                    int bin = (int)(h / 360.0 * Bins) % Bins;
                    weight[bin] += w;
                    hueSum[bin] += w * h;
                    satSum[bin] += w * s;
                    total += w;
                }
            }

            // art with next to no colour: let the caller use its fallback
            if (total < 8) return null;

            // a hue bin plus its neighbours, so a colour straddling a bin edge
            // is not split in two and out-voted
            int best = 0;
            double bestW = -1;
            for (int b = 0; b < Bins; b++)
            {
                double w = weight[b] + 0.5 * (weight[(b + 1) % Bins] + weight[(b + Bins - 1) % Bins]);
                if (w > bestW) { bestW = w; best = b; }
            }
            if (weight[best] <= 0) return null;

            double hue = hueSum[best] / weight[best];
            double sat = satSum[best] / weight[best];

            // Readable on the dark overlay: always bright, and never washed out.
            return FromHsv(hue, Math.Clamp(sat, 0.65, 0.95), 0.95);
        }
        catch
        {
            return null;
        }
    }

    private static void ToHsv(int r, int g, int b, out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min;

        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }

        if (max == rf) h = 60 * (((gf - bf) / d) % 6);
        else if (max == gf) h = 60 * ((bf - rf) / d + 2);
        else h = 60 * ((rf - gf) / d + 4);
        if (h < 0) h += 360;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromArgb(255,
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}
