using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HueContrast.Core;

/// <summary>
/// 色相分组 + 灰度明度统计。
/// 明度定义：灰度亮度 0.299R + 0.587G + 0.114B，映射到 0-100（黑=0，白=100）。
/// 分组：饱和度 &lt; 阈值归“无彩色”，其余按 30° 色相桶分为 12 组。
/// </summary>
public static class HueAnalyzer
{
    public const int AchromaticSlot = 12;
    public const string AchromaticName = "无彩色";
    public static readonly string[] BucketNames =
        { "红", "橙", "黄", "黄绿", "绿", "青绿", "青", "蓝", "蓝紫", "紫", "品红", "玫红" };

    public static AnalysisResult Analyze(Bitmap source, AnalyzerOptions opt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int srcW = source.Width, srcH = source.Height;
        double scale = 1.0;
        if (opt.MaxAnalysisWidth > 0 && srcW > opt.MaxAnalysisWidth)
            scale = opt.MaxAnalysisWidth / (double)srcW;
        int dw = Math.Max(1, (int)Math.Round(srcW * scale));
        int dh = Math.Max(1, (int)Math.Round(srcH * scale));

        Bitmap temp = null;
        Bitmap work = source;
        if (dw != srcW || dh != srcH)
        {
            temp = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(temp))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, dw, dh));
            }
            work = temp;
        }

        // 取像素
        var bd = work.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(bd.Stride);
        byte[] buf = new byte[stride * dh];
        Marshal.Copy(bd.Scan0, buf, 0, stride * dh);
        work.UnlockBits(bd);
        temp?.Dispose();

        // ---------------- 第一遍：分组 + 明度直方图 ----------------
        int[] counts = new int[13];
        int[][] hist = new int[13][];
        for (int i = 0; i < 13; i++) hist[i] = new int[101];

        int[,] bx0 = new int[13, 101];
        int[,] bx1 = new int[13, 101];
        int[,] by0 = new int[13, 101];
        int[,] by1 = new int[13, 101];
        for (int s = 0; s < 13; s++)
            for (int l = 0; l < 101; l++)
            {
                bx0[s, l] = int.MaxValue; by0[s, l] = int.MaxValue;
                bx1[s, l] = -1; by1[s, l] = -1;
            }

        double[] sumR = new double[13];
        double[] sumG = new double[13];
        double[] sumB = new double[13];

        byte[] labels = new byte[dw * dh];
        byte[] lums = new byte[dw * dh];
        Array.Fill(labels, (byte)255);

        int srcSat = Math.Max(0, Math.Min(100, opt.AchromaticSatThreshold));
        long opaque = 0;

        for (int y = 0; y < dh; y++)
        {
            int row = y * stride;
            int rowoff = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int o = row + x * 4;
                int b0 = buf[o], g0 = buf[o + 1], r0 = buf[o + 2], a0 = buf[o + 3];
                if (a0 < 200) continue;

                int mx = Math.Max(r0, Math.Max(g0, b0));
                int mn = Math.Min(r0, Math.Min(g0, b0));
                int sat = mx == 0 ? 0 : (mx - mn) * 100 / mx;

                int slot;
                if (sat < srcSat) slot = AchromaticSlot;
                else
                {
                    int d = mx - mn;
                    float h;
                    if (mx == r0) h = 60f * (((g0 - b0) / (float)d) + 6f) % 360f;
                    else if (mx == g0) h = 60f * ((b0 - r0) / (float)d + 2f);
                    else h = 60f * ((r0 - g0) / (float)d + 4f);
                    slot = (int)(h / 30f);
                    if (slot < 0) slot = 0;
                    if (slot > 11) slot = 11;
                }

                int gray255 = (299 * r0 + 587 * g0 + 114 * b0) / 1000;
                int lum = (gray255 * 100 + 127) / 255;
                if (lum < 0) lum = 0;
                if (lum > 100) lum = 100;

                int idx = rowoff + x;
                labels[idx] = (byte)slot;
                lums[idx] = (byte)lum;

                counts[slot]++;
                opaque++;
                hist[slot][lum]++;
                sumR[slot] += r0; sumG[slot] += g0; sumB[slot] += b0;

                if (x < bx0[slot, lum]) bx0[slot, lum] = x;
                if (x > bx1[slot, lum]) bx1[slot, lum] = x;
                if (y < by0[slot, lum]) by0[slot, lum] = y;
                if (y > by1[slot, lum]) by1[slot, lum] = y;
            }
        }

        // ---------------- 组装色相组 ----------------
        var result = new AnalysisResult
        {
            Width = srcW,
            Height = srcH,
            AnalysisWidth = dw,
            AnalysisHeight = dh,
            Options = opt
        };

        var reported = new List<GroupResult>();
        for (int slot = 0; slot <= 12; slot++)
        {
            if (counts[slot] <= 0) continue;
            double share = counts[slot] * 100.0 / Math.Max(1, opaque);
            if (share < opt.MinSharePercent) continue;

            int p1 = Percentile(hist[slot], counts[slot], 0.01);
            int p99 = Percentile(hist[slot], counts[slot], 0.99);
            int absMin = -1, absMax = -1;
            for (int l = 0; l <= 100; l++)
            {
                if (hist[slot][l] > 0)
                {
                    if (absMin < 0) absMin = l;
                    absMax = l;
                }
            }

            var gr = new GroupResult
            {
                Slot = slot,
                Name = slot == AchromaticSlot ? AchromaticName : BucketNames[slot],
                SharePercent = Math.Round(share, 1),
                PixelCount = counts[slot],
                DarkValue = p1,
                BrightValue = p99,
                AbsMin = absMin,
                AbsMax = absMax,
                Ratio = Math.Round(p99 / (double)Math.Max(1, p1), 2),
                Level = LevelOf(p99 / (double)Math.Max(1, p1)),
                ColorHex = SlotColorHex(slot)
            };
            reported.Add(gr);
        }

        // ---------------- 第二遍：最暗/最亮取样框 ----------------
        int block = Math.Max(4, opt.SampleBlockSize);
        int gw = (dw + block - 1) / block;
        int gh = (dh + block - 1) / block;

        var bySlot = new List<SampleTarget>[13];
        foreach (var g in reported)
        {
            var dark = new SampleTarget(g.Slot, Math.Max(0, g.DarkValue - 2), Math.Min(100, g.DarkValue + 2), true, gw, gh, block);
            var bright = new SampleTarget(g.Slot, Math.Max(0, g.BrightValue - 2), Math.Min(100, g.BrightValue + 2), false, gw, gh, block);
            (bySlot[g.Slot] ??= new List<SampleTarget>()).Add(dark);
            bySlot[g.Slot].Add(bright);
            g.DarkBox = Rectangle.Empty;
            g.BrightBox = Rectangle.Empty;
            dark.Owner = g;
            bright.Owner = g;
        }

        var allTargets = new List<SampleTarget>();
        foreach (var sig in bySlot) if (sig != null) allTargets.AddRange(sig);

        for (int y = 0; y < dh; y++)
        {
            int rowoff = y * dw;
            int cy = y / block;
            for (int x = 0; x < dw; x++)
            {
                int idx = rowoff + x;
                byte ls = labels[idx];
                if (ls > 12) continue;
                var sig = bySlot[ls];
                if (sig == null) continue;
                int lum = lums[idx];
                int cx = x / block;
                int cell = cy * gw + cx;

                for (int t = 0; t < sig.Count; t++)
                {
                    var tr = sig[t];
                    if (lum < tr.Lo || lum > tr.Hi) continue;
                    tr.Cells[cell]++;
                    if (x < tr.PxMinX) tr.PxMinX = x;
                    if (x > tr.PxMaxX) tr.PxMaxX = x;
                    if (y < tr.PxMinY) tr.PxMinY = y;
                    if (y > tr.PxMaxY) tr.PxMaxY = y;
                }
            }
        }

        foreach (var t in allTargets)
        {
            Rectangle box = ResolveTargetBox(t, gw, gh, block, dw, dh);
            Rectangle mapped = MapToSource(box, scale, srcW, srcH);
            if (t.IsDark) t.Owner.DarkBox = mapped;
            else t.Owner.BrightBox = mapped;
        }

        // ---------------- 第三遍：同色系连通区域 ----------------
        int minArea = Math.Max(16, (int)(dw * dh * opt.MinRegionAreaPercent / 100.0));
        foreach (var g in reported)
        {
            if (opt.MaxRegionsPerGroup <= 0) continue;
            var boxes = FindRegions(labels, dw, dh, g.Slot, minArea, opt.MaxRegionsPerGroup);
            foreach (var b in boxes)
                g.Regions.Add(MapToSource(b, scale, srcW, srcH));
        }

        reported.Sort((a, b) => b.SharePercent.CompareTo(a.SharePercent));
        result.Groups = reported;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    // ---------------- 辅助 ----------------

    private sealed class SampleTarget
    {
        public int Slot, Lo, Hi;
        public bool IsDark;
        public int Block, Gw, Gh;
        public int[] Cells;
        public GroupResult Owner;
        public int PxMinX = int.MaxValue, PxMinY = int.MaxValue, PxMaxX = -1, PxMaxY = -1;

        public SampleTarget(int slot, int lo, int hi, bool dark, int gw, int gh, int block)
        {
            Slot = slot; Lo = lo; Hi = hi; IsDark = dark; Gw = gw; Gh = gh; Block = block;
            Cells = new int[gw * gh];
        }
    }

    private static Rectangle ResolveTargetBox(SampleTarget t, int gw, int gh, int block, int dw, int dh)
    {
        int best = 0, bestCell = -1;
        for (int i = 0; i < t.Cells.Length; i++)
            if (t.Cells[i] > best) { best = t.Cells[i]; bestCell = i; }

        if (bestCell >= 0 && best > 0)
        {
            int threshold = Math.Max(2, (int)(best * 0.3));
            var visited = new bool[gw * gh];
            var queue = new Queue<int>();
            queue.Enqueue(bestCell);
            visited[bestCell] = true;
            int cx0 = bestCell % gw, cx1 = cx0, cy0 = bestCell / gw, cy1 = cy0;

            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int cx = c % gw, cy = c / gw;
                if (cx < cx0) cx0 = cx;
                if (cx > cx1) cx1 = cx;
                if (cy < cy0) cy0 = cy;
                if (cy > cy1) cy1 = cy;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        int ni = ny * gw + nx;
                        if (visited[ni] || t.Cells[ni] < threshold) continue;
                        visited[ni] = true;
                        queue.Enqueue(ni);
                    }
            }

            int x = cx0 * block, y = cy0 * block;
            int w = Math.Min(dw, (cx1 + 1) * block) - x;
            int h = Math.Min(dh, (cy1 + 1) * block) - y;
            return new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        if (t.PxMaxX >= 0)
            return new Rectangle(t.PxMinX, t.PxMinY, t.PxMaxX - t.PxMinX + 1, t.PxMaxY - t.PxMinY + 1);
        return Rectangle.Empty;
    }

    private static Rectangle MapToSource(Rectangle r, double scale, int srcW, int srcH)
    {
        if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
        int x = (int)Math.Floor(r.X / scale);
        int y = (int)Math.Floor(r.Y / scale);
        int w = (int)Math.Ceiling(r.Width / scale);
        int h = (int)Math.Ceiling(r.Height / scale);

        const int minSide = 34;
        if (w < minSide)
        {
            x -= (minSide - w) / 2;
            w = minSide;
        }
        if (h < minSide)
        {
            y -= (minSide - h) / 2;
            h = minSide;
        }

        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > srcW) w = srcW - x;
        if (y + h > srcH) h = srcH - y;
        if (w < 2) w = 2;
        if (h < 2) h = 2;
        return new Rectangle(x, y, w, h);
    }

    private static List<Rectangle> FindRegions(byte[] labels, int dw, int dh, int slot, int minArea, int maxCount)
    {
        var found = new List<(Rectangle Box, int Area)>();
        var visited = new bool[dw * dh];
        var queue = new Queue<int>(4096);

        for (int y = 0; y < dh; y++)
        {
            int rowoff = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int i = rowoff + x;
                if (visited[i] || labels[i] != slot) continue;

                queue.Clear();
                queue.Enqueue(i);
                visited[i] = true;
                int area = 0;
                int x0 = x, x1 = x, y0 = y, y1 = y;

                while (queue.Count > 0)
                {
                    int p = queue.Dequeue();
                    int px = p % dw, py = p / dw;
                    area++;
                    if (px < x0) x0 = px;
                    if (px > x1) x1 = px;
                    if (py < y0) y0 = py;
                    if (py > y1) y1 = py;

                    int n;
                    if (px > 0) { n = p - 1; if (!visited[n] && labels[n] == slot) { visited[n] = true; queue.Enqueue(n); } }
                    if (px < dw - 1) { n = p + 1; if (!visited[n] && labels[n] == slot) { visited[n] = true; queue.Enqueue(n); } }
                    if (py > 0) { n = p - dw; if (!visited[n] && labels[n] == slot) { visited[n] = true; queue.Enqueue(n); } }
                    if (py < dh - 1) { n = p + dw; if (!visited[n] && labels[n] == slot) { visited[n] = true; queue.Enqueue(n); } }
                }

                if (area >= minArea)
                    found.Add((new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1), area));
            }
        }

        found.Sort((a, b) => b.Area.CompareTo(a.Area));
        var boxes = new List<Rectangle>();
        foreach (var f in found)
        {
            if (boxes.Count >= maxCount) break;
            boxes.Add(f.Box);
        }

        return MergeOverlapping(boxes);
    }

    private static List<Rectangle> MergeOverlapping(List<Rectangle> boxes)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    Rectangle a = boxes[i];
                    Rectangle b = boxes[j];
                    a.Inflate(6, 6);
                    if (a.IntersectsWith(b))
                    {
                        boxes[i] = Rectangle.Union(boxes[i], boxes[j]);
                        boxes.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
                if (merged) break;
            }
        }
        return boxes;
    }

    public static int Percentile(int[] hist, int total, double q)
    {
        if (total <= 0) return 0;
        int target = (int)Math.Ceiling(total * q);
        int cum = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            cum += hist[i];
            if (cum >= target) return i;
        }
        return hist.Length - 1;
    }

    public static string LevelOf(double ratio)
    {
        if (ratio < 1.5) return "弱对比";
        if (ratio < 3.0) return "中对比";
        if (ratio < 7.0) return "强对比";
        return "极强对比";
    }

    public static string SlotColorHex(int slot)
    {
        Color c = slot == AchromaticSlot ? Color.FromArgb(160, 160, 160) : HsvToRgb(slot * 30 + 15, 0.85, 0.62);
        return ColorTranslator.ToHtml(c);
    }

    public static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        int seg = (int)(h / 60);
        switch (seg)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        int R = (int)Math.Round((r + m) * 255);
        int G = (int)Math.Round((g + m) * 255);
        int B = (int)Math.Round((b + m) * 255);
        return Color.FromArgb(Math.Min(255, R), Math.Min(255, G), Math.Min(255, B));
    }
}
