using System.Drawing;
using System.Drawing.Imaging;

namespace HueContrast.Core;

/// <summary>
/// 元素分割与关系提取：
/// 颜色量化 → 相近相邻区域合并 → 大色块/面板/文字簇 → 包含与相邻关系。
/// </summary>
public static class ElementDetector
{
    public static ElementAnalysisResult Analyze(Bitmap source, ElementOptions opt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var q = ImageQuantizer.Run(source, opt.SeedColors, opt.MaxAnalysisWidth);
        int dw = q.Width, dh = q.Height, n = dw * dh;
        int srcW = source.Width, srcH = source.Height;
        double scale = q.Scale;

        // ---------- 1. 区域生长 ----------
        var map = new int[n];
        Array.Fill(map, -1);
        var regs = new List<RegionAcc>();
        var queue = new Queue<int>(4096);
        var rgba = q.Rgba;
        int stride = q.Stride;

        for (int i = 0; i < n; i++)
        {
            if (map[i] != -1 || q.Labels[i] >= 200) continue;
            int rid = regs.Count;
            var acc = new RegionAcc(q.Labels[i]);
            regs.Add(acc);
            map[i] = rid;
            queue.Enqueue(i);

            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                int x = p % dw, y = p / dw;
                int o = y * stride + x * 4;
                int b0 = rgba[o], g0 = rgba[o + 1], r0 = rgba[o + 2];
                acc.Add(x, y, ColorMath.GrayL(r0, g0, b0), r0, g0, b0);

                int nb;
                if (x > 0) { nb = p - 1; if (map[nb] == -1 && q.Labels[nb] == q.Labels[p]) { map[nb] = rid; queue.Enqueue(nb); } }
                if (x < dw - 1) { nb = p + 1; if (map[nb] == -1 && q.Labels[nb] == q.Labels[p]) { map[nb] = rid; queue.Enqueue(nb); } }
                if (y > 0) { nb = p - dw; if (map[nb] == -1 && q.Labels[nb] == q.Labels[p]) { map[nb] = rid; queue.Enqueue(nb); } }
                if (y < dh - 1) { nb = p + dw; if (map[nb] == -1 && q.Labels[nb] == q.Labels[p]) { map[nb] = rid; queue.Enqueue(nb); } }
            }
        }

        if (regs.Count == 0)
            return new ElementAnalysisResult { Width = srcW, Height = srcH, AnalysisWidth = dw, AnalysisHeight = dh, Profile = "UI", ElapsedMs = sw.ElapsedMilliseconds };

        // ---------- 2. 相近相邻区域合并 ----------
        bool uiLike = ResolveProfile(opt.Profile, q, dw, dh);
        int mergeLimit = uiLike ? 560 : 1300; // RGB 距离平方；UI 用更严格的同色判定，避免按钮面被并进背景
        var uf = new int[regs.Count];
        for (int i = 0; i < uf.Length; i++) uf[i] = i;

        int Find(int x)
        {
            while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) uf[rb] = ra;
        }

        for (int y = 0; y < dh; y++)
        {
            int rowoff = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int idx = rowoff + x;
                int a = map[idx];
                if (a < 0) continue;
                if (x + 1 < dw)
                {
                    int b = map[idx + 1];
                    if (b >= 0 && b != a && ShouldMerge(regs[a].Palette, regs[b].Palette, q.Palette, mergeLimit)) Union(a, b);
                }
                if (y + 1 < dh)
                {
                    int b = map[idx + dw];
                    if (b >= 0 && b != a && ShouldMerge(regs[a].Palette, regs[b].Palette, q.Palette, mergeLimit)) Union(a, b);
                }
            }
        }

        var groups = new Dictionary<int, RegionAcc>();
        for (int i = 0; i < regs.Count; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var acc))
            {
                acc = new RegionAcc(regs[i].Palette);
                groups[root] = acc;
            }
            acc.MergeFrom(regs[i]);
        }

        var rootMap = new int[n];
        for (int i = 0; i < n; i++) rootMap[i] = map[i] >= 0 ? Find(map[i]) : -1;

        // ---------- 3. 生成候选组 ----------
        int minLarge = (int)Math.Max(60, n * 0.0004);
        int minShape = (int)Math.Max(220, n * 0.0015);
        int minMember = 30;

        var raw = new List<RawGroup>();
        foreach (var acc in groups.Values)
        {
            if (acc.Area < minMember) continue;
            acc.FinalizeStats();
            var rg = new RawGroup
            {
                Acc = acc,
                Bounds = new Rectangle(acc.MinX, acc.MinY, acc.MaxX - acc.MinX + 1, acc.MaxY - acc.MinY + 1),
                Area = acc.Area,
                FillRatio = acc.Area / (double)Math.Max(1, (acc.MaxX - acc.MinX + 1) * (acc.MaxY - acc.MinY + 1))
            };
            raw.Add(rg);
        }

        // 背景：面积最大的组
        if (raw.Count == 0)
            return new ElementAnalysisResult { Width = srcW, Height = srcH, AnalysisWidth = dw, AnalysisHeight = dh, Profile = uiLike ? "UI" : "插画", ElapsedMs = sw.ElapsedMilliseconds };

        // 描边闭合轮廓：按钮面与背景几乎同色时，只能靠边框把它分出来
        foreach (var o in FindOutlineBoxes(rgba, stride, dw, dh, n))
        {
            bool dup = raw.Any(r => !r.IsOutline && r.Area >= o.Area * 0.45 &&
                                    Math.Abs(r.Bounds.X - o.Bounds.X) <= 5 &&
                                    Math.Abs(r.Bounds.Y - o.Bounds.Y) <= 5 &&
                                    Math.Abs(r.Bounds.Width - o.Bounds.Width) <= 10 &&
                                    Math.Abs(r.Bounds.Height - o.Bounds.Height) <= 10);
            if (!dup) raw.Add(o);
        }

        RawGroup bg = raw.Where(r => !r.IsOutline).OrderByDescending(r => r.Area).First();

        // ---------- 4. 文字簇（先吸收笔画组，避免它们又被当成大色块） ----------
        var textClusters = uiLike ? FindTextClusters(raw, bg, dw, dh, n) : new List<RawGroup>();

        var large = raw.Where(r =>
        {
            if (r.Area <= 0) return false;                 // 被文字簇吸收
            if (ReferenceEquals(r, bg)) return true;
            if (r.IsOutline) return true;                  // 描边识别出的控件
            if (r.Area < minLarge) return false;
            bool rectLike = r.FillRatio >= 0.82 && Math.Min(r.Bounds.Width, r.Bounds.Height) >= 12
                            && r.Bounds.Width <= r.Bounds.Height * 8 && r.Bounds.Height <= r.Bounds.Width * 8;
            return rectLike || r.Area >= minShape;
        }).OrderByDescending(r => r.Area).Take(50).ToList();

        // ---------- 5. 组装元素 ----------
        var elements = new List<VisualElement>();
        var rawInfo = new List<(VisualElement el, RawGroup g)>();
        var parents = new Dictionary<VisualElement, VisualElement>();

        foreach (var rg in large)
        {
            var el = MakeElement(rg, srcW, srcH, scale, n, ReferenceEquals(rg, bg));
            elements.Add(el);
            rawInfo.Add((el, rg));
        }

        var bgEl = rawInfo.First(x => x.el.Kind == ElementKind.Background).el;

        // 文字簇：挂到包含它的最小元素上
        foreach (var tc in textClusters)
        {
            VisualElement best = null;
            int bestArea = int.MaxValue;
            foreach (var (el, g) in rawInfo)
            {
                if (el.Kind == ElementKind.Background) continue;
                if (!Contains(g.Bounds, tc.Bounds, 2)) continue;
                if (g.FillRatio < 0.15) continue;   // 细描边/线框不作为文字底色
                if (g.Area < bestArea) { bestArea = g.Area; best = el; }
            }
            var parentEl = best ?? bgEl;
            if (Math.Abs(parentEl.P50 - tc.Acc.P50) < 12) continue;

            var textEl = MakeElement(tc, srcW, srcH, scale, n, false);
            textEl.Kind = ElementKind.Text;
            elements.Add(textEl);
            parents[textEl] = parentEl;
        }

        // 其他元素：找面积最小且能包含自己的父元素
        foreach (var (el, g) in rawInfo)
        {
            if (ReferenceEquals(el, bgEl)) continue;
            VisualElement parent = null;
            int parentArea = int.MaxValue;
            foreach (var (pe, pg) in rawInfo)
            {
                if (ReferenceEquals(pe, el)) continue;
                if (pg.Area <= g.Area) continue;
                if (!Contains(pg.Bounds, g.Bounds, 2)) continue;
                if (pg.Area < parentArea) { parentArea = pg.Area; parent = pe; }
            }
            if (parent != null) parents[el] = parent;
        }

        // 大字号标题：把相邻、同高、同色的字形块并成一行文字
        MergeTitleRuns(elements, rawInfo, parents, srcW, srcH, scale, n);

        // 标题合并会移除成员元素，父引用可能指向已移除对象，这里按包含关系整体重建
        parents.Clear();
        foreach (var el in elements)
        {
            if (el.Kind == ElementKind.Background) continue;
            VisualElement parent = null;
            long parentArea = long.MaxValue;
            foreach (var other in elements)
            {
                if (ReferenceEquals(other, el)) continue;
                // 用包围盒面积判断“谁包含谁”，细描边框的像素很少但不能因此被跳过
                long areaO = (long)other.Bounds.Width * other.Bounds.Height;
                long areaE = (long)el.Bounds.Width * el.Bounds.Height;
                if (areaO <= areaE) continue;
                if (!Contains(other.Bounds, el.Bounds, 3)) continue;
                if (el.Kind == ElementKind.Text)
                {
                    // 文字不该挂在另一段文字上，否则得不到真正的底色对比
                    if (other.Kind == ElementKind.Text) continue;
                }
                if (areaO < parentArea) { parentArea = areaO; parent = other; }
            }
            if (parent != null) parents[el] = parent;
        }

        // 细描边矩形框 + 含文字 → 按钮：明度要测“框内的填充色”，而不是边框线的颜色
        foreach (var el in elements)
        {
            if (el.Kind == ElementKind.Text || el.Kind == ElementKind.Background) continue;
            if (el.FillRatio >= 0.15) continue;
            if (!parents.Any(kv => ReferenceEquals(kv.Value, el) && kv.Key.Kind == ElementKind.Text)) continue;
            MeasureInnerFill(el, rgba, stride, scale, dw, dh);
            el.Kind = ElementKind.Button;
        }

        // 矩形 + 含文字 → 按钮
        foreach (var el in elements)
        {
            if (el.Kind != ElementKind.Panel) continue;
            if (parents.Any(kv => kv.Value == el && kv.Key.Kind == ElementKind.Text))
                el.Kind = ElementKind.Button;
        }

        // 编号：按面积降序（背景固定 1），父 id 由对象引用重映射
        var ordered = elements.Where(e => !ReferenceEquals(e, bgEl))
                              .OrderByDescending(e => e.AreaPixels)
                              .ToList();
        bgEl.Id = 1;
        int id = 2;
        foreach (var e in ordered) e.Id = id++;
        foreach (var e in elements)
            e.ParentId = parents.TryGetValue(e, out var p) && p != null ? p.Id : -1;

        var result = new ElementAnalysisResult
        {
            Width = srcW,
            Height = srcH,
            AnalysisWidth = dw,
            AnalysisHeight = dh,
            Profile = uiLike ? "UI" : "插画",
            Elements = elements.OrderBy(e => e.Id).ToList()
        };

        // ---------- 6. 关系 ----------
        result.Relations = BuildRelations(result);
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static List<ElementRelation> BuildRelations(ElementAnalysisResult result)
    {
        var rels = new List<ElementRelation>();
        var all = result.Elements;

        foreach (var e in all)
        {
            if (e.Kind == ElementKind.Background) continue;
            if (e.ParentId < 1) continue;
            var p = result.Find(e.ParentId);
            if (p == null) continue;
            AddRelation(rels, p, e, e.Kind == ElementKind.Text ? RelationKind.TextOnFill : RelationKind.Contained);
        }

        // 相邻关系（只看大块）
        var top = all.Where(e => e.Kind != ElementKind.Text).OrderByDescending(e => e.AreaPixels).Take(10).ToList();
        for (int i = 0; i < top.Count; i++)
        {
            for (int j = i + 1; j < top.Count; j++)
            {
                var a = top[i]; var b = top[j];
                if (a.Kind == ElementKind.Background && b.Kind == ElementKind.Background) continue;
                if (IsParentChild(a, b)) continue;
                Rectangle ra = a.Bounds; Rectangle rb = b.Bounds;
                ra.Inflate(3, 3); rb.Inflate(3, 3);
                if (!ra.IntersectsWith(rb)) continue;
                if (Math.Abs(a.P50 - b.P50) < 3) continue;
                AddRelation(rels, a.AreaPixels >= b.AreaPixels ? a : b, a.AreaPixels >= b.AreaPixels ? b : a, RelationKind.Adjacent);
            }
        }

        rels.Sort((x, y) =>
        {
            int kx = x.Kind == RelationKind.TextOnFill ? 0 : x.Kind == RelationKind.Contained ? 1 : 2;
            int ky = y.Kind == RelationKind.TextOnFill ? 0 : y.Kind == RelationKind.Contained ? 1 : 2;
            if (kx != ky) return kx.CompareTo(ky);
            return y.DeltaL.CompareTo(x.DeltaL);
        });
        if (rels.Count > 40) rels.RemoveRange(40, rels.Count - 40);
        return rels;
    }

    private static bool IsParentChild(VisualElement a, VisualElement b)
    {
        return a.ParentId == b.Id || b.ParentId == a.Id;
    }

    private static void AddRelation(List<ElementRelation> rels, VisualElement container, VisualElement content, RelationKind kind)
    {
        if (container == null || content == null) return;
        double dark = Math.Min(container.P50, content.P50);
        double bright = Math.Max(container.P50, content.P50);
        var rel = new ElementRelation
        {
            AId = container.Id,
            BId = content.Id,
            Kind = kind,
            DeltaL = bright - dark,
            Ratio = bright / Math.Max(1, dark),
            Wcag = ColorMath.Wcag(container.MeanColor, content.MeanColor),
            HueDiff = Math.Round(ColorMath.HueDiff(container.MeanHue, content.MeanHue)),
            Note = $"{content.KindName}在{container.KindName}上"
        };
        rels.Add(rel);
    }

    /// <summary>把水平相邻、高度接近、颜色接近的字形块合并成一行标题文字。</summary>
    private static void MergeTitleRuns(List<VisualElement> elements,
        List<(VisualElement el, RawGroup g)> rawInfo,
        Dictionary<VisualElement, VisualElement> parents,
        int srcW, int srcH, double scale, int n)
    {
        var cands = rawInfo.Where(x => !x.g.IsOutline &&
                                       x.el.Kind != ElementKind.Background &&
                                       x.el.Kind != ElementKind.Text &&
                                       x.g.Bounds.Height >= 14 &&
                                       x.g.FillRatio < 0.72)
                           .OrderBy(x => x.g.Bounds.Y).ThenBy(x => x.g.Bounds.X).ToList();
        if (cands.Count < 3) return;

        // 1) 先按竖直重叠分行
        var rows = new List<List<(VisualElement el, RawGroup g)>>();
        foreach (var c in cands)
        {
            List<(VisualElement el, RawGroup g)> hit = null;
            foreach (var row in rows)
            {
                var rb = row[0].g.Bounds;
                int ov = Math.Min(rb.Bottom, c.g.Bounds.Bottom) - Math.Max(rb.Top, c.g.Bounds.Top);
                if (ov >= Math.Min(rb.Height, c.g.Bounds.Height) * 0.5) { hit = row; break; }
            }
            if (hit == null) rows.Add(new List<(VisualElement el, RawGroup g)> { c });
            else hit.Add(c);
        }

        // 2) 行内按 X 排序，按间隙/高度/颜色切成连续段
        var runs = new List<List<(VisualElement el, RawGroup g)>>();
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.g.Bounds.X.CompareTo(b.g.Bounds.X));
            int medH = MedianHeight(row);
            List<(VisualElement el, RawGroup g)> run = null;
            foreach (var c in row)
            {
                if (c.g.Bounds.Height < medH * 0.6) { run = null; continue; }
                bool extend = run != null;
                if (extend)
                {
                    var last = run[^1].g;
                    double gap = c.g.Bounds.Left - last.Bounds.Right;
                    if (gap > medH * 0.9 || gap < -medH * 0.4) extend = false;
                    else if (ColorDist(last.Acc.MeanColor, c.g.Acc.MeanColor) > 3200) extend = false;
                }
                if (!extend)
                {
                    run = new List<(VisualElement el, RawGroup g)> { c };
                    runs.Add(run);
                }
                else run.Add(c);
            }
        }

        foreach (var run in runs)
        {
            if (run.Count < 3) continue;
            Rectangle u = run[0].g.Bounds;
            for (int i = 1; i < run.Count; i++) u = Rectangle.Union(u, run[i].g.Bounds);
            if (u.Width < u.Height * 2.2) continue;

            var acc = new RegionAcc(run[0].g.Acc.Palette);
            foreach (var m in run) acc.MergeFrom(m.g.Acc);
            acc.FinalizeStats();

            var txt = MakeElement(new RawGroup
            {
                Acc = acc,
                Bounds = u,
                Area = acc.Area,
                FillRatio = acc.Area / (double)Math.Max(1, u.Width * u.Height)
            }, srcW, srcH, scale, n, false);
            txt.Kind = ElementKind.Text;

            foreach (var m in run)
            {
                elements.Remove(m.el);
                parents.Remove(m.el);
            }
            elements.Add(txt);

            VisualElement parent = null;
            int parentArea = int.MaxValue;
            foreach (var other in elements)
            {
                if (ReferenceEquals(other, txt)) continue;
                if (!Contains(other.Bounds, txt.Bounds, 4)) continue;
                if (other.AreaPixels < parentArea) { parentArea = other.AreaPixels; parent = other; }
            }
            if (parent != null) parents[txt] = parent;
        }
    }

    private static int MedianHeight(List<(VisualElement el, RawGroup g)> row)
    {
        var hs = row.Select(x => x.g.Bounds.Height).OrderBy(h => h).ToList();
        return hs[hs.Count / 2];
    }

    private static int MedianHeight(List<RawGroup> row)
    {
        var hs = row.Select(r => r.Bounds.Height).OrderBy(h => h).ToList();
        return hs[hs.Count / 2];
    }

    private static int ColorDist(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>把元素的明度改测为其边框内部的填充色，并顺带拟合这一区域是否有渐变。</summary>
    private static void MeasureInnerFill(VisualElement el, byte[] rgba, int stride, double scale, int dw, int dh)
    {
        int x0 = (int)Math.Floor(el.Bounds.X / scale), y0 = (int)Math.Floor(el.Bounds.Y / scale);
        int x1 = Math.Min(dw, (int)Math.Ceiling((el.Bounds.X + el.Bounds.Width) / scale));
        int y1 = Math.Min(dh, (int)Math.Ceiling((el.Bounds.Y + el.Bounds.Height) / scale));
        int w = x1 - x0, h = y1 - y0;
        if (w < 8 || h < 6) return;

        int inset = Math.Max(2, Math.Min(w, h) / 12);
        var r = new Rectangle(x0 + inset, y0 + inset, w - inset * 2, h - inset * 2);
        if (r.Width < 6 || r.Height < 4) return;

        var hist = new int[101];
        var idx = new List<int>(r.Width * r.Height);
        for (int y = r.Top; y < r.Bottom; y++)
        {
            for (int x = r.Left; x < r.Right; x++)
            {
                int o = y * stride + x * 4;
                double L = ColorMath.GrayL(rgba[o + 2], rgba[o + 1], rgba[o]);
                hist[Math.Max(0, Math.Min(100, (int)Math.Round(L)))]++;
                idx.Add(y * dw + x);
            }
        }
        double median = ColorMath.Percentile(hist, 0.5);

        var acc = new RegionAcc(0);
        foreach (int i in idx)
        {
            int x = i % dw, y = i / dw;
            int o = y * stride + x * 4;
            int b0 = rgba[o], g0 = rgba[o + 1], r0 = rgba[o + 2];
            double L = ColorMath.GrayL(r0, g0, b0);
            if (Math.Abs(L - median) > 14) continue;   // 跳过框内的文字笔画
            acc.Add(x, y, L, r0, g0, b0);
        }
        if (acc.Area < 60) return;
        acc.FinalizeStats();

        el.MeanL = Math.Round(acc.MeanL, 1);
        el.P5 = acc.P5; el.P50 = acc.P50; el.P95 = acc.P95;
        el.MeanColor = acc.MeanColor;
        el.MeanHue = Math.Round(acc.MeanHue, 1);
        el.MeanSat = Math.Round(acc.MeanSat, 1);
        el.Hex = ColorMath.Hex(acc.MeanColor);
        el.AreaPixels = idx.Count;
        el.FillRatio = 1.0;

        if (acc.Area >= 300 && acc.TryFitPlane(out double gx, out double gy))
        {
            double delta = Math.Abs(gx) * r.Width + Math.Abs(gy) * r.Height;
            if (delta >= 4)
            {
                el.HasGradient = true;
                el.GradientDeltaL = Math.Round(delta, 1);
                el.GradientAngleDeg = Math.Round(Math.Atan2(gy, gx) * 180.0 / Math.PI, 0);
            }
        }
    }

    private static bool Contains(Rectangle outer, Rectangle inner, int pad)
    {
        var o = outer;
        o.Inflate(pad, pad);
        return o.Contains(inner);
    }

    private static VisualElement MakeElement(RawGroup rg, int srcW, int srcH, double scale, int totalPixels, bool isBackground)
    {
        var p = rg.Acc;
        var b = rg.Bounds;
        Rectangle mapped = MapRect(b, scale, srcW, srcH);
        int minSide = Math.Min(b.Width, b.Height);
        ElementKind kind;
        if (isBackground) kind = ElementKind.Background;
        else if (rg.FillRatio >= 0.82 && minSide >= 12) kind = ElementKind.Panel;
        else if (rg.Area >= 120) kind = ElementKind.Icon;
        else kind = ElementKind.Shape;

        var el = new VisualElement
        {
            Kind = kind,
            Bounds = mapped,
            AreaPixels = rg.Area,
            FillRatio = rg.FillRatio,
            SharePercent = Math.Round(rg.Area * 100.0 / Math.Max(1, totalPixels), 2),
            MeanL = Math.Round(p.MeanL, 1),
            P5 = p.P5,
            P50 = p.P50,
            P95 = p.P95,
            MeanHue = Math.Round(p.MeanHue, 1),
            MeanSat = Math.Round(p.MeanSat, 1),
            MeanColor = p.MeanColor,
            Hex = ColorMath.Hex(p.MeanColor),
            HasGradient = false
        };

        // 渐变拟合
        if (!isBackground && p.Area >= 300)
        {
            if (p.TryFitPlane(out double gx, out double gy))
            {
                double delta = Math.Abs(gx) * b.Width + Math.Abs(gy) * b.Height;
                if (delta >= 5)
                {
                    el.HasGradient = true;
                    el.GradientDeltaL = Math.Round(delta, 1);
                    el.GradientAngleDeg = Math.Round(Math.Atan2(gy, gx) * 180.0 / Math.PI, 0);
                }
            }
        }
        else if (isBackground && p.Area >= 300)
        {
            if (p.TryFitPlane(out double gx2, out double gy2))
            {
                double delta = Math.Abs(gx2) * b.Width + Math.Abs(gy2) * b.Height;
                if (delta >= 6)
                {
                    el.HasGradient = true;
                    el.GradientDeltaL = Math.Round(delta, 1);
                    el.GradientAngleDeg = Math.Round(Math.Atan2(gy2, gx2) * 180.0 / Math.PI, 0);
                }
            }
        }
        return el;
    }

    private static Rectangle MapRect(Rectangle r, double scale, int srcW, int srcH)
    {
        int x = (int)Math.Floor(r.X / scale);
        int y = (int)Math.Floor(r.Y / scale);
        int w = (int)Math.Ceiling(r.Width / scale);
        int h = (int)Math.Ceiling(r.Height / scale);
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > srcW) w = srcW - x;
        if (y + h > srcH) h = srcH - y;
        return new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static bool ResolveProfile(string profile, QuantResult q, int dw, int dh)
    {
        if (!string.IsNullOrEmpty(profile) && !profile.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return profile.Equals("ui", StringComparison.OrdinalIgnoreCase);

        // 自动判型：既看“填满率高的大块”（面板/按钮），也看“字形大小且成行排列的小块”（文字）
        int n = dw * dh;
        var map = new int[n];
        Array.Fill(map, -1);
        var qq = new Queue<int>();
        var labels = q.Labels;
        var comps = new List<(Rectangle box, int area)>();

        for (int i = 0; i < n; i++)
        {
            if (map[i] != -1 || labels[i] >= 200) continue;
            int minX = i % dw, maxX = minX, minY = i / dw, maxY = minY;
            int area = 0;
            map[i] = 1;
            qq.Enqueue(i);
            while (qq.Count > 0)
            {
                int p = qq.Dequeue();
                int x = p % dw, y = p / dw;
                area++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                int nb;
                if (x > 0) { nb = p - 1; if (map[nb] == -1 && labels[nb] == labels[p]) { map[nb] = 1; qq.Enqueue(nb); } }
                if (x < dw - 1) { nb = p + 1; if (map[nb] == -1 && labels[nb] == labels[p]) { map[nb] = 1; qq.Enqueue(nb); } }
                if (y > 0) { nb = p - dw; if (map[nb] == -1 && labels[nb] == labels[p]) { map[nb] = 1; qq.Enqueue(nb); } }
                if (y < dh - 1) { nb = p + dw; if (map[nb] == -1 && labels[nb] == labels[p]) { map[nb] = 1; qq.Enqueue(nb); } }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (area < 30) continue;
            comps.Add((new Rectangle(minX, minY, bw, bh), area));
        }

        int rectLike = comps.Count(c =>
            c.area >= n * 0.004 && Math.Min(c.box.Width, c.box.Height) >= 12 &&
            c.area / (double)(c.box.Width * c.box.Height) >= 0.85);

        // 字形候选：小、偏方、笔画密度适中（真实界面文字通常不超过 44px）
        var glyphs = comps.Where(c =>
            c.box.Height >= 4 && c.box.Height <= Math.Min(dh * 0.08, 44) &&
            c.box.Width >= 2 && c.box.Width <= c.box.Height * 6 &&
            c.area / (double)(c.box.Width * c.box.Height) >= 0.1 &&
            c.area / (double)(c.box.Width * c.box.Height) <= 0.9).ToList();

        // 成行排列：同一行至少 3 个字形块
        int textRows = 0;
        var rows = new List<List<Rectangle>>();
        foreach (var c in glyphs.OrderBy(c => c.box.Y))
        {
            List<Rectangle> hit = null;
            foreach (var row in rows)
            {
                Rectangle rb = row[0];
                int overlap = Math.Min(rb.Bottom, c.box.Bottom) - Math.Max(rb.Top, c.box.Top);
                if (overlap >= Math.Min(rb.Height, c.box.Height) * 0.6) { hit = row; break; }
            }
            if (hit == null) rows.Add(new List<Rectangle> { c.box });
            else hit.Add(c.box);
        }
        textRows = rows.Count(r => r.Count >= 3);

        return rectLike >= 2 || (glyphs.Count >= 24 && textRows >= 4);
    }

    private static bool ShouldMerge(int pa, int pb, Color[] palette, int limit)
    {
        if (pa == pb) return true;
        var a = palette[pa];
        var b = palette[pb];
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db <= limit;
    }

    // ---------------- 文字簇 ----------------

    private static List<RawGroup> FindTextClusters(List<RawGroup> raw, RawGroup bg, int dw, int dh, int n)
    {
        var cand = raw.Where(r =>
            !ReferenceEquals(r, bg) &&
            !r.IsOutline &&
            r.Area >= 30 &&
            r.Bounds.Height <= dh * 0.16 &&
            r.Bounds.Width <= dw * 0.55 &&
            r.Bounds.Width <= r.Bounds.Height * 5 &&
            r.FillRatio >= 0.06 && r.FillRatio <= 0.95 &&
            Math.Abs(r.Acc.P50 - bg.Acc.P50) >= 10
        ).OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();

        if (cand.Count < 3) return new List<RawGroup>();

        // 行聚类：要求竖直中心接近、字号接近、水平间隔不大；
        // 用成员中位高度而不是 union 包围盒，避免一行把整屏内容一路吸进来
        var lines = new List<List<RawGroup>>();
        foreach (var c in cand)
        {
            List<RawGroup> best = null;
            double bestGap = double.MaxValue;
            foreach (var line in lines)
            {
                int medH = MedianHeight(line);
                Rectangle lb = UnionBounds(line);
                int cy = c.Bounds.Top + c.Bounds.Height / 2;
                // 行的基准高度用成员中位中心，而不是包围盒中心（包围盒会越滚越大）
                int ly = line.Select(r => r.Bounds.Top + r.Bounds.Height / 2)
                             .OrderBy(v => v).ElementAt(line.Count / 2);
                if (Math.Abs(cy - ly) > Math.Max(5, Math.Min(c.Bounds.Height, medH) * 0.5)) continue;
                if (Math.Abs(c.Bounds.Height - medH) > medH * 0.5) continue;
                double gap = c.Bounds.Left - lb.Right;
                if (gap > medH * 2.5) continue;
                if (gap < bestGap) { bestGap = gap; best = line; }
            }
            if (best == null) lines.Add(new List<RawGroup> { c });
            else best.Add(c);
        }

        // 行合并成块
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < lines.Count && !merged; i++)
            {
                for (int j = i + 1; j < lines.Count; j++)
                {
                    Rectangle a = UnionBounds(lines[i]);
                    Rectangle b = UnionBounds(lines[j]);
                    int hOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    int vGap = Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom);
                    int hMax = Math.Max(a.Height, b.Height), hMin = Math.Min(a.Height, b.Height);
                    // 只合并“同样字号的相邻行”，避免把标题和正文连成一片
                    if (hMax > hMin * 2.2) continue;
                    if (hOverlap >= Math.Min(a.Width, b.Width) * 0.3 && vGap <= hMin * 0.55)
                    {
                        lines[i].AddRange(lines[j]);
                        lines.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        }

        var clusters = new List<(RawGroup g, List<RawGroup> members)>();
        foreach (var line in lines)
        {
            // 一行里若有大间隔（正则词距、或误并入的远处色块），按间隔切开
            int medH = MedianHeight(line);
            foreach (var seg in SplitByGaps(line, medH * 1.5))
            {
                if (seg.Count < 3) continue;
                Rectangle b = UnionBounds(seg);
                if (b.Width < b.Height * 2.0) continue;
                if (b.Height > dh * 0.30 || b.Width > dw * 0.8) continue;
                int stroke = seg.Sum(r => r.Area);
                if (stroke / (double)b.Width / b.Height > 0.62) continue;

                var acc = new RegionAcc(seg[0].Acc.Palette);
                foreach (var r in seg) acc.MergeFrom(r.Acc);
                acc.FinalizeStats();

                double contrastToBg = Math.Abs(acc.P50 - bg.Acc.P50);
                if (contrastToBg < 12 && seg.Count < 5) continue;

                clusters.Add((new RawGroup
                {
                    Acc = acc,
                    Bounds = b,
                    Area = stroke,
                    FillRatio = stroke / (double)(b.Width * b.Height)
                }, seg));
            }
        }

        clusters = clusters.OrderByDescending(c => c.Item1.Area).Take(10).ToList();
        // 只把真正的成员标为已吸收，避免包围盒顺带吞掉远处的图形/节点
        foreach (var (_, members) in clusters)
            foreach (var r in members) r.Area = 0;
        return clusters.Select(c => c.Item1).ToList();
    }

    private static Rectangle UnionBounds(List<RawGroup> list)
    {
        Rectangle b = list[0].Bounds;
        for (int i = 1; i < list.Count; i++) b = Rectangle.Union(b, list[i].Bounds);
        return b;
    }

    /// <summary>按水平间隔把一行切成若干连续段。</summary>
    private static List<List<RawGroup>> SplitByGaps(List<RawGroup> line, double maxGap)
    {
        var segs = new List<List<RawGroup>>();
        var cur = new List<RawGroup>();
        foreach (var r in line.OrderBy(r => r.Bounds.X))
        {
            if (cur.Count > 0 && r.Bounds.Left - cur[^1].Bounds.Right > maxGap)
            {
                segs.Add(cur);
                cur = new List<RawGroup>();
            }
            cur.Add(r);
        }
        if (cur.Count > 0) segs.Add(cur);
        return segs;
    }

    // ---------------- 内部数据结构 ----------------

    /// <summary>用边缘闭合轮廓找控件框（按钮/输入框），内部填充色可与背景几乎相同。</summary>
    private static List<RawGroup> FindOutlineBoxes(byte[] rgba, int stride, int dw, int dh, int n)
    {
        var lum = new float[n];
        for (int y = 0; y < dh; y++)
        {
            int o = y * stride, ro = y * dw;
            for (int x = 0; x < dw; x++, o += 4)
                lum[ro + x] = (float)ColorMath.GrayL(rgba[o + 2], rgba[o + 1], rgba[o]);
        }

        var edge = new bool[n];
        for (int y = 0; y < dh - 1; y++)
        {
            int ro = y * dw;
            for (int x = 0; x < dw - 1; x++)
            {
                int i = ro + x;
                if (Math.Abs(lum[i] - lum[i + 1]) > 4f || Math.Abs(lum[i] - lum[i + dw]) > 4f) edge[i] = true;
            }
        }

        // 3×3 膨胀：补上抗锯齿造成的断口
        var dil = new bool[n];
        for (int y = 0; y < dh; y++)
        {
            int ro = y * dw;
            for (int x = 0; x < dw; x++)
            {
                if (!edge[ro + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= dh) continue;
                    int r2 = yy * dw;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx >= 0 && xx < dw) dil[r2 + xx] = true;
                    }
                }
            }
        }

        var res = new List<RawGroup>();
        var map = new int[n];
        Array.Fill(map, -1);
        var queue = new Queue<int>();

        for (int i = 0; i < n; i++)
        {
            if (!dil[i] || map[i] != -1) continue;
            int minX = i % dw, maxX = minX, minY = i / dw, maxY = minY;
            map[i] = 1;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                int x = p % dw, y = p / dw;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                int nb;
                if (x > 0) { nb = p - 1; if (map[nb] == -1 && dil[nb]) { map[nb] = 1; queue.Enqueue(nb); } }
                if (x < dw - 1) { nb = p + 1; if (map[nb] == -1 && dil[nb]) { map[nb] = 1; queue.Enqueue(nb); } }
                if (y > 0) { nb = p - dw; if (map[nb] == -1 && dil[nb]) { map[nb] = 1; queue.Enqueue(nb); } }
                if (y < dh - 1) { nb = p + dw; if (map[nb] == -1 && dil[nb]) { map[nb] = 1; queue.Enqueue(nb); } }
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < 40 || bh < 20) continue;
            if (bw > dw * 0.92 || bh > dh * 0.92) continue;

            // 四条边被边缘像素覆盖的比例
            int border = 0, cover = 0;
            for (int x = minX; x <= maxX; x++)
            {
                border += 2;
                if (dil[minY * dw + x]) cover++;
                if (dil[maxY * dw + x]) cover++;
            }
            for (int y = minY + 1; y < maxY; y++)
            {
                border += 2;
                if (dil[y * dw + minX]) cover++;
                if (dil[y * dw + maxX]) cover++;
            }
            if (border == 0 || cover / (double)border < 0.42) continue;

            int inset = Math.Max(3, Math.Min(bw, bh) / 14);
            var r = new Rectangle(minX + inset, minY + inset, bw - inset * 2, bh - inset * 2);
            int cnt = r.Width * r.Height;
            if (r.Width < 12 || r.Height < 8 || cnt < 120) continue;

            var hist = new int[101];
            var idx = new List<int>(cnt);
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int ro2 = y * dw;
                for (int x = r.Left; x < r.Right; x++)
                {
                    int i2 = ro2 + x;
                    hist[Math.Max(0, Math.Min(100, (int)Math.Round(lum[i2]))) ]++;
                    idx.Add(i2);
                }
            }
            double median = ColorMath.Percentile(hist, 0.5);
            int inlier = 0;
            foreach (int i2 in idx) if (Math.Abs(lum[i2] - median) <= 14) inlier++;
            if (inlier < cnt * 0.55) continue;   // 内部太杂乱，不是控件面

            var acc = new RegionAcc(0);
            foreach (int i2 in idx)
            {
                double li = lum[i2];
                if (Math.Abs(li - median) > 14) continue;   // 跳过内部文字笔画
                int x = i2 % dw, y = i2 / dw;
                int o = y * stride + x * 4;
                acc.Add(x, y, li, rgba[o + 2], rgba[o + 1], rgba[o]);
            }
            acc.FinalizeStats();

            res.Add(new RawGroup
            {
                Acc = acc,
                Bounds = new Rectangle(minX, minY, bw, bh),
                Area = acc.Area,
                FillRatio = 1.0,
                IsOutline = true
            });
        }

        return res.OrderByDescending(g => g.Bounds.Width * g.Bounds.Height).Take(10).ToList();
    }

    private sealed class RawGroup
    {
        public RegionAcc Acc;
        public Rectangle Bounds;
        public int Area;
        public double FillRatio;
        /// <summary>由描边闭合轮廓识别的控件（按钮/输入框等），内部填充可能与背景几乎同色。</summary>
        public bool IsOutline;
    }

    private sealed class RegionAcc
    {
        public int Palette;
        public int Area;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
        public int[] Hist = new int[101];
        public double SR, SG, SB;
        public double Sx, Sy, Sxx, Syy, Sxy, SL, SxL, SyL;

        public double MeanL, P5, P50, P95, MeanHue, MeanSat;
        public Color MeanColor;

        public RegionAcc(int palette) { Palette = palette; }

        public void Add(int x, int y, double l, int r, int g, int b)
        {
            Area++;
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
            int li = Math.Max(0, Math.Min(100, (int)Math.Round(l)));
            Hist[li]++;
            SR += r; SG += g; SB += b;
            Sx += x; Sy += y; Sxx += x * x; Syy += y * y; Sxy += x * y;
            SL += l; SxL += x * l; SyL += y * l;
        }

        public void MergeFrom(RegionAcc o)
        {
            if (o == this) return;
            Area += o.Area;
            if (o.MinX < MinX) MinX = o.MinX;
            if (o.MaxX > MaxX) MaxX = o.MaxX;
            if (o.MinY < MinY) MinY = o.MinY;
            if (o.MaxY > MaxY) MaxY = o.MaxY;
            for (int i = 0; i < 101; i++) Hist[i] += o.Hist[i];
            SR += o.SR; SG += o.SG; SB += o.SB;
            Sx += o.Sx; Sy += o.Sy; Sxx += o.Sxx; Syy += o.Syy; Sxy += o.Sxy;
            SL += o.SL; SxL += o.SxL; SyL += o.SyL;
        }

        public void FinalizeStats()
        {
            if (Area <= 0)
            {
                MeanL = P5 = P50 = P95 = 0;
                MeanHue = MeanSat = 0;
                MeanColor = Color.Black;
                return;
            }
            MeanColor = Color.FromArgb(
                (int)Math.Round(SR / Area), (int)Math.Round(SG / Area), (int)Math.Round(SB / Area));
            MeanL = ColorMath.GrayL(MeanColor);
            P5 = ColorMath.Percentile(Hist, 0.05);
            P50 = ColorMath.Percentile(Hist, 0.5);
            P95 = ColorMath.Percentile(Hist, 0.95);
            MeanHue = ColorMath.HueOf(MeanColor.R, MeanColor.G, MeanColor.B);
            MeanSat = ColorMath.SatOf(MeanColor.R, MeanColor.G, MeanColor.B);
        }

        public bool TryFitPlane(out double gx, out double gy)
        {
            gx = 0; gy = 0;
            double n = Area;
            if (n < 50) return false;
            // 法方程：[n sx sy; sx sxx sxy; sy sxy syy] * [a, gx, gy] = [sL, sxL, syL]
            double[,] m =
            {
                { n, Sx, Sy },
                { Sx, Sxx, Sxy },
                { Sy, Sxy, Syy }
            };
            double[] v = { SL, SxL, SyL };
            double det = Det3(m);
            if (Math.Abs(det) < 1e-9) return false;
            gx = Det3Replace(m, v, 1) / det;
            gy = Det3Replace(m, v, 2) / det;
            return true;
        }

        private static double Det3(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static double Det3Replace(double[,] m, double[] v, int col)
        {
            double[,] t = (double[,])m.Clone();
            for (int i = 0; i < 3; i++) t[i, col] = v[i];
            return Det3(t);
        }
    }
}
