using System.Drawing;

namespace HueContrast.Core;

public enum ProbeMode { Browse, Compare, Profile, Pick }

/// <summary>框选区域的明度采样结果。</summary>
public sealed class ProbeSample
{
    public Rectangle Bounds { get; set; }
    public int Count { get; set; }
    public double MeanL { get; set; }
    public double P5 { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public Color MeanColor { get; set; }

    public string Hex => ColorMath.Hex(MeanColor);
    public string RangeText => $"{P50:0}（{P5:0}~{P95:0}）";
}

/// <summary>线上某一点的明度。</summary>
public sealed class ProbePoint
{
    public Point At { get; set; }
    public double L { get; set; }
    public Color Color { get; set; }
}

/// <summary>手动测量状态（坐标均为原图像素）。</summary>
public sealed class ManualProbe
{
    public Rectangle? BoxA { get; set; }
    public Rectangle? BoxB { get; set; }
    public Point? ProfileStart { get; set; }
    public Point? ProfileEnd { get; set; }
    public Point? PickAt { get; set; }

    public bool IsEmpty => BoxA == null && BoxB == null && ProfileStart == null && PickAt == null;

    public void ClearCompare() { BoxA = null; BoxB = null; }
    public void ClearProfile() { ProfileStart = null; ProfileEnd = null; }
    public void ClearPick() { PickAt = null; }
    public void ClearAll() { BoxA = BoxB = null; ProfileStart = ProfileEnd = null; PickAt = null; }
}

/// <summary>手动测量工具：双框对比、明度剖面、点选拾取。</summary>
public static class Probe
{
    public static ProbeSample Measure(Bitmap bmp, Rectangle rect)
    {
        rect = Clamp(rect, bmp.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        var hist = new int[101];
        long sr = 0, sg = 0, sb = 0;
        int count = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var c = bmp.GetPixel(x, y);
                double l = ColorMath.GrayL(c);
                hist[(int)Math.Max(0, Math.Min(100, Math.Round(l)))]++;
                sr += c.R; sg += c.G; sb += c.B;
                count++;
            }
        }
        if (count == 0) return null;

        return new ProbeSample
        {
            Bounds = rect,
            Count = count,
            MeanL = Math.Round(sr == 0 ? 0 : ColorMath.GrayL((int)(sr / count), (int)(sg / count), (int)(sb / count)), 1),
            P5 = ColorMath.Percentile(hist, 0.05),
            P50 = ColorMath.Percentile(hist, 0.5),
            P95 = ColorMath.Percentile(hist, 0.95),
            MeanColor = Color.FromArgb((int)(sr / count), (int)(sg / count), (int)(sb / count))
        };
    }

    /// <summary>沿线段逐点取样（1px 步长，最长取 4000 点）。</summary>
    public static List<ProbePoint> AlongLine(Bitmap bmp, Point a, Point b)
    {
        var list = new List<ProbePoint>();
        int dx = b.X - a.X, dy = b.Y - a.Y;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps <= 0) return list;
        int count = Math.Min(steps, 4000);

        for (int i = 0; i <= count; i++)
        {
            double t = i / (double)count;
            int x = (int)Math.Round(a.X + dx * t);
            int y = (int)Math.Round(a.Y + dy * t);
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
            var c = bmp.GetPixel(x, y);
            list.Add(new ProbePoint { At = new Point(x, y), L = Math.Round(ColorMath.GrayL(c), 1), Color = c });
        }
        return list;
    }

    /// <summary>点选拾取：取 3×3 邻域平均，避免踩到噪点。</summary>
    public static ProbePoint Pick(Bitmap bmp, Point p)
    {
        double sum = 0; long sr = 0, sg = 0, sb = 0;
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = p.X + dx, y = p.Y + dy;
                if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
                var c = bmp.GetPixel(x, y);
                sum += ColorMath.GrayL(c);
                sr += c.R; sg += c.G; sb += c.B;
                count++;
            }
        }
        if (count == 0) return null;
        return new ProbePoint
        {
            At = p,
            L = Math.Round(sum / count, 1),
            Color = Color.FromArgb((int)(sr / count), (int)(sg / count), (int)(sb / count))
        };
    }

    /// <summary>两个框的对比结论，例如 “ΔL 15 · 1.5 : 1 · WCAG 1.4（弱）”。</summary>
    public static string Describe(ProbeSample a, ProbeSample b)
    {
        if (a == null || b == null) return "请框选 A、B 两个区域（A 直接拖拽，B 按住 Shift 拖拽）";
        double dark = Math.Min(a.P50, b.P50);
        double bright = Math.Max(a.P50, b.P50);
        double ratio = bright / Math.Max(1, dark);
        double wcag = ColorMath.Wcag(a.MeanColor, b.MeanColor);
        return $"ΔL {bright - dark:0}　比值 {bright:0} : {dark:0}　({ratio:0.00})　WCAG {wcag:0.00}（{ColorMath.ContrastLevel(wcag)}对比）";
    }

    /// <summary>剖面的明度起伏结论。</summary>
    public static string DescribeLine(List<ProbePoint> line)
    {
        if (line == null || line.Count < 2) return "在图上按住左键拖一条直线，即可查看沿线明度剖面";
        double min = line.Min(p => p.L);
        double max = line.Max(p => p.L);
        double start = line[0].L, end = line[line.Count - 1].L;
        double avg = line.Average(p => p.L);
        return $"长度 {line.Count}px　起 {start:0} → 止 {end:0}　最低 {min:0}　最高 {max:0}　跨度 {max - min:0}　均值 {avg:0}";
    }

    private static Rectangle Clamp(Rectangle r, Size size)
    {
        int x = Math.Max(0, Math.Min(size.Width, r.X));
        int y = Math.Max(0, Math.Min(size.Height, r.Y));
        int right = Math.Max(x, Math.Min(size.Width, r.Right));
        int bottom = Math.Max(y, Math.Min(size.Height, r.Bottom));
        return new Rectangle(x, y, right - x, bottom - y);
    }
}
