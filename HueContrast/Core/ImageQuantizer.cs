using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HueContrast.Core;

public sealed class QuantResult
{
    public byte[] Labels;
    public Color[] Palette;
    /// <summary>分析图像的 BGRA 原始像素（B,G,R,A 顺序），供后续取色/明度计算。</summary>
    public byte[] Rgba;
    public int Stride;
    public int Width;
    public int Height;
    public double Scale;
}

/// <summary>中位切分颜色量化：把画面压成 N 个主色，供元素分割使用。</summary>
public static class ImageQuantizer
{
    private const int Bits = 16;
    private const int Bins = Bits * Bits * Bits;

    public static QuantResult Run(Bitmap source, int targetColors, int maxWidth)
    {
        int sw = source.Width, sh = source.Height;
        double scale = 1.0;
        if (maxWidth > 0 && sw > maxWidth) scale = maxWidth / (double)sw;
        int dw = Math.Max(1, (int)Math.Round(sw * scale));
        int dh = Math.Max(1, (int)Math.Round(sh * scale));

        Bitmap temp = null;
        Bitmap work = source;
        if (dw != sw || dh != sh)
        {
            temp = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(temp);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(source, new Rectangle(0, 0, dw, dh));
            work = temp;
        }

        var bd = work.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(bd.Stride);
        var buf = new byte[stride * dh];
        Marshal.Copy(bd.Scan0, buf, 0, stride * dh);
        work.UnlockBits(bd);

        // 4 位直方图
        var counts = new long[Bins];
        var rs = new double[Bins];
        var gs = new double[Bins];
        var bs = new double[Bins];
        for (int y = 0; y < dh; y++)
        {
            int row = y * stride;
            for (int x = 0; x < dw; x++)
            {
                int o = row + x * 4;
                if (buf[o + 3] < 200) continue;
                int r = buf[o + 2], g = buf[o + 1], b = buf[o];
                int idx = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                counts[idx]++;
                rs[idx] += r; gs[idx] += g; bs[idx] += b;
            }
        }

        var boxes = new List<ColorBox> { new ColorBox(0, Bits - 1, 0, Bits - 1, 0, Bits - 1, counts) };
        int target = Math.Max(2, Math.Min(32, targetColors));

        while (boxes.Count < target)
        {
            ColorBox best = null;
            long bestScore = 0;
            foreach (var box in boxes)
            {
                if (box.Count <= 1) continue;
                long score = (box.LongestRange() + 1) * box.Count;
                if (score > bestScore) { bestScore = score; best = box; }
            }
            if (best == null) break;

            ColorBox a, b;
            if (!best.Split(counts, out a, out b)) break;
            boxes.Remove(best);
            boxes.Add(a);
            boxes.Add(b);
        }

        var palette = new Color[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box.Count == 0) { palette[i] = Color.Black; continue; }
            palette[i] = Color.FromArgb(
                ClampByte(box.SumR / box.Count),
                ClampByte(box.SumG / box.Count),
                ClampByte(box.SumB / box.Count));
        }

        // 分配像素
        var labels = new byte[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            int row = y * stride;
            int rowoff = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int o = row + x * 4;
                if (buf[o + 3] < 200) { labels[rowoff + x] = 255; continue; }
                int r = buf[o + 2], g = buf[o + 1], b = buf[o];
                int best = 0;
                int bestDist = int.MaxValue;
                for (int p = 0; p < palette.Length; p++)
                {
                    int dr = r - palette[p].R, dg = g - palette[p].G, db = b - palette[p].B;
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist) { bestDist = dist; best = p; }
                }
                labels[rowoff + x] = (byte)best;
            }
        }

        temp?.Dispose();
        return new QuantResult
        {
            Labels = labels,
            Palette = palette,
            Rgba = buf,
            Stride = stride,
            Width = dw,
            Height = dh,
            Scale = scale
        };
    }

    private static byte ClampByte(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));

    private sealed class ColorBox
    {
        public int R0, R1, G0, G1, B0, B1;
        public long Count;
        public double SumR, SumG, SumB;
        private readonly long[] _counts;

        public ColorBox(int r0, int r1, int g0, int g1, int b0, int b1, long[] counts)
        {
            R0 = r0; R1 = r1; G0 = g0; G1 = g1; B0 = b0; B1 = b1;
            _counts = counts;
            for (int r = r0; r <= r1; r++)
                for (int g = g0; g <= g1; g++)
                    for (int b = b0; b <= b1; b++)
                    {
                        int idx = (r << 8) | (g << 4) | b;
                        long c = counts[idx];
                        if (c == 0) continue;
                        Count += c;
                        // 用桶中心近似累计，用于调色板取色
                        SumR += c * (r * 16 + 8);
                        SumG += c * (g * 16 + 8);
                        SumB += c * (b * 16 + 8);
                    }
        }

        public long LongestRange()
        {
            int dr = R1 - R0, dg = G1 - G0, db = B1 - B0;
            return Math.Max(dr, Math.Max(dg, db));
        }

        public bool Split(long[] counts, out ColorBox a, out ColorBox b)
        {
            a = null; b = null;
            int dr = R1 - R0, dg = G1 - G0, db = B1 - B0;
            int max = Math.Max(dr, Math.Max(dg, db));
            if (max <= 0) return false;

            int channel = (max == dr) ? 0 : (max == dg ? 1 : 2);
            int lo = channel == 0 ? R0 : channel == 1 ? G0 : B0;
            int hi = channel == 0 ? R1 : channel == 1 ? G1 : B1;

            long[] perBin = new long[hi - lo + 1];
            long total = 0;
            for (int r = R0; r <= R1; r++)
                for (int g = G0; g <= G1; g++)
                    for (int bb = B0; bb <= B1; bb++)
                    {
                        int pos = channel == 0 ? r : channel == 1 ? g : bb;
                        long c = counts[(r << 8) | (g << 4) | bb];
                        if (c == 0) continue;
                        perBin[pos - lo] += c;
                        total += c;
                    }
            if (total == 0) return false;

            long half = total / 2, cum = 0;
            int cut = -1;
            for (int i = 0; i < perBin.Length; i++)
            {
                cum += perBin[i];
                if (cum >= half) { cut = lo + i; break; }
            }
            if (cut < lo || cut >= hi) return false;

            if (channel == 0)
            {
                a = new ColorBox(R0, cut, G0, G1, B0, B1, counts);
                b = new ColorBox(cut + 1, R1, G0, G1, B0, B1, counts);
            }
            else if (channel == 1)
            {
                a = new ColorBox(R0, R1, G0, cut, B0, B1, counts);
                b = new ColorBox(R0, R1, cut + 1, G1, B0, B1, counts);
            }
            else
            {
                a = new ColorBox(R0, R1, G0, G1, B0, cut, counts);
                b = new ColorBox(R0, R1, G0, G1, cut + 1, B1, counts);
            }
            return a.Count > 0 && b.Count > 0;
        }
    }
}
