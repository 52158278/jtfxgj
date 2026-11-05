using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace HueContrast.Core;

public sealed class RenderOptions
{
    public bool DrawElements { get; set; } = true;
    public bool DrawRelations { get; set; } = true;
    public bool DrawLabels { get; set; } = true;
    public int HighlightId { get; set; } = -1;
    public ManualProbe Probe { get; set; }
    public List<ProbePoint> ProfileLine { get; set; }
}

/// <summary>元素标注渲染：轮廓框 + 编号徽标 + 关系连线 + 底部双栏图例 + 手动探针叠加。</summary>
public static class ElementsRenderer
{
    public static Bitmap Render(Bitmap original, ElementAnalysisResult result, RenderOptions opt)
    {
        opt ??= new RenderOptions();
        int w = original.Width, h = original.Height;
        float fs = Math.Max(9f, Math.Min(18f, w / 190f));

        var shown = result.Elements.Where(e => !e.Suppressed).ToList();
        var rels = opt.DrawRelations ? result.Relations.Take(10).ToList() : new List<ElementRelation>();

        int legendRows = Math.Max(shown.Count, rels.Count);
        int legendH = (int)(fs * 2.2f * (Math.Min(legendRows, 16) + 1) + fs * 3.0f + 20);

        var canvas = new Bitmap(w, h + legendH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawImage(original, 0, 0, w, h);

        using var fBold = new Font("Microsoft YaHei UI", fs, FontStyle.Bold);
        using var fNormal = new Font("Microsoft YaHei UI", fs, FontStyle.Regular);
        using var fSmall = new Font("Microsoft YaHei UI", Math.Max(8f, fs * 0.85f), FontStyle.Regular);

        // 1) 元素轮廓
        if (opt.DrawElements)
        {
            foreach (var e in shown)
            {
                bool hot = opt.HighlightId == e.Id;
                Color frame = e.FrameColor;
                float thick = hot ? Math.Max(3f, fs / 3f) : Math.Max(1f, fs / 8f);
                using var pen = new Pen(Color.FromArgb(hot ? (byte)255 : (byte)205, frame), thick)
                { DashStyle = e.Kind == ElementKind.Text ? DashStyle.Dot : DashStyle.Solid };
                Rectangle r = e.Bounds;
                g.DrawRectangle(pen, r);

                if (!hot) continue;
                using var fill = new SolidBrush(Color.FromArgb(46, frame));
                g.FillRectangle(fill, r);
            }

            // 编号徽标（放在框左上角外侧，尽量不压住内容）
            if (opt.DrawLabels)
            {
                foreach (var e in shown)
                {
                    if (e.Kind == ElementKind.Background) continue;
                    DrawBadge(g, e, fBold, w, h);
                }
            }
        }

        // 2) 关系连线：容器中心 → 内容中心；容器是背景时只在元素旁标 ΔL，避免长线穿过画面
        if (opt.DrawRelations)
        {
            foreach (var rel in result.Relations.Take(12))
            {
                var a = result.Find(rel.AId);
                var b = result.Find(rel.BId);
                if (a == null || b == null) continue;
                if (a.Suppressed || b.Suppressed) continue;
                Color line = rel.Kind == RelationKind.TextOnFill ? Color.FromArgb(48, 214, 200)
                           : rel.Kind == RelationKind.Contained ? Color.FromArgb(140, 170, 220)
                           : Color.FromArgb(200, 200, 120);
                string tag = "ΔL " + rel.DeltaL.ToString("0");

                if (a.Kind == ElementKind.Background)
                {
                    DrawDeltaTag(g, b.Bounds, tag, line, fSmall, w, h);
                    continue;
                }

                using var pen = new Pen(Color.FromArgb(180, line), Math.Max(1f, fs / 9f)) { DashStyle = DashStyle.Dash };
                Point pa = Center(a.Bounds), pb = Center(b.Bounds);
                g.DrawLine(pen, pa, pb);

                var sz = g.MeasureString(tag, fSmall);
                using var tb = new SolidBrush(Color.FromArgb(235, 20, 22, 26));
                using var tf = new SolidBrush(Color.FromArgb(240, line));
                int mx = (pa.X + pb.X) / 2, my = (pa.Y + pb.Y) / 2;
                g.FillRectangle(tb, mx - sz.Width / 2 - 3, my - sz.Height / 2 - 1, sz.Width + 6, sz.Height + 2);
                g.DrawString(tag, fSmall, tf, mx - sz.Width / 2, my - sz.Height / 2);
            }
        }

        // 3) 手动探针
        DrawProbe(g, original, opt, fBold, fSmall, w, h);

        // 4) 底部图例
        DrawLegend(g, result, shown, rels, fBold, fNormal, fSmall, w, h, legendH, fs);

        return canvas;
    }

    private static void DrawDeltaTag(Graphics g, Rectangle box, string text, Color color, Font font, int w, int h)
    {
        var sz = g.MeasureString(text, font);
        int bw = (int)sz.Width + 6;
        int bh = (int)sz.Height + 2;
        int x = Math.Min(w - bw - 2, box.Right + 3);
        int y = Math.Max(2, box.Top - bh);
        using var bg = new SolidBrush(Color.FromArgb(222, 18, 20, 24));
        using var pen = new Pen(Color.FromArgb(230, color), 1f);
        g.FillRectangle(bg, x, y, bw, bh);
        g.DrawRectangle(pen, x, y, bw, bh);
        using var tb = new SolidBrush(Color.FromArgb(240, color));
        g.DrawString(text, font, tb, x + 3, y);
    }

    private static void DrawBadge(Graphics g, VisualElement e, Font font, int w, int h)
    {
        string text = e.Id.ToString();
        var sz = g.MeasureString(text, font);
        int bw = (int)sz.Width + (int)(font.Size * 0.8f);
        int bh = (int)sz.Height + (int)(font.Size * 0.2f);

        int x = e.Bounds.X;
        int y = e.Bounds.Y - bh;
        if (y < 0) y = e.Bounds.Y;
        if (y + bh > h) y = Math.Max(0, h - bh);
        if (x + bw > w) x = Math.Max(0, w - bw);

        Color frame = e.FrameColor;
        using var bg = new SolidBrush(Color.FromArgb(240, frame));
        using var border = new Pen(Color.FromArgb(250, 15, 16, 20), 1f);
        g.FillRectangle(bg, x, y, bw, bh);
        g.DrawRectangle(border, x, y, bw, bh);
        using var tb = new SolidBrush(Color.FromArgb(250, 255, 255, 255));
        g.DrawString(text, font, tb, x + bw * 0.5f - sz.Width * 0.5f, y);
    }

    private static void DrawProbe(Graphics g, Bitmap original, RenderOptions opt,
        Font fBold, Font fSmall, int w, int h)
    {
        var probe = opt.Probe;
        if (probe == null) return;

        if (probe.BoxA.HasValue && probe.BoxA.Value.Width > 0)
            DrawProbeBox(g, probe.BoxA.Value, "A", Color.FromArgb(96, 200, 255), fBold, w, h);
        if (probe.BoxB.HasValue && probe.BoxB.Value.Width > 0)
            DrawProbeBox(g, probe.BoxB.Value, "B", Color.FromArgb(255, 168, 96), fBold, w, h);

        if (probe.ProfileStart.HasValue && probe.ProfileEnd.HasValue)
        {
            using var pen = new Pen(Color.FromArgb(230, 255, 220, 120), 2f);
            g.DrawLine(pen, probe.ProfileStart.Value, probe.ProfileEnd.Value);
            using var cap = new SolidBrush(Color.FromArgb(255, 255, 220, 120));
            foreach (var p in new[] { probe.ProfileStart.Value, probe.ProfileEnd.Value })
                g.FillEllipse(cap, p.X - 3, p.Y - 3, 6, 6);

            if (opt.ProfileLine != null && opt.ProfileLine.Count > 1)
                DrawProfileChart(g, opt.ProfileLine, w, h, fSmall);
        }

        if (probe.PickAt.HasValue)
        {
            var p = probe.PickAt.Value;
            using var pen = new Pen(Color.FromArgb(255, 255, 255, 255), 1.5f);
            g.DrawLine(pen, p.X - 7, p.Y, p.X + 7, p.Y);
            g.DrawLine(pen, p.X, p.Y - 7, p.X, p.Y + 7);
            using var ring = new Pen(Color.FromArgb(255, 96, 200, 255), 2f);
            g.DrawEllipse(ring, p.X - 5, p.Y - 5, 10, 10);
        }
    }

    private static void DrawProbeBox(Graphics g, Rectangle r, string tag, Color color, Font font, int w, int h)
    {
        using var pen = new Pen(Color.FromArgb(240, color), 2f) { DashStyle = DashStyle.Dash };
        using var fill = new SolidBrush(Color.FromArgb(36, color));
        g.FillRectangle(fill, r);
        g.DrawRectangle(pen, r);

        var sz = g.MeasureString(tag, font);
        int bw = (int)sz.Width + (int)(font.Size * 0.9f);
        int bh = (int)sz.Height + (int)(font.Size * 0.2f);
        int x = Math.Max(0, Math.Min(w - bw, r.X));
        int y = r.Y - bh - 2;
        if (y < 0) y = Math.Min(h - bh, r.Y + 2);
        using var bg = new SolidBrush(Color.FromArgb(240, color));
        g.FillRectangle(bg, x, y, bw, bh);
        using var tb = new SolidBrush(Color.FromArgb(250, 20, 22, 26));
        g.DrawString(tag, font, tb, x + bw * 0.5f - sz.Width * 0.5f, y);
    }

    /// <summary>在图片右上角画一条明度折线（0-100 满量程）。</summary>
    private static void DrawProfileChart(Graphics g, List<ProbePoint> line, int w, int h, Font font)
    {
        int cw = Math.Min(w / 3, 320);
        int ch = Math.Max(60, (int)(cw * 0.32f));
        int x0 = w - cw - 14, y0 = 14;

        using var bg = new SolidBrush(Color.FromArgb(232, 16, 18, 22));
        g.FillRectangle(bg, x0, y0, cw, ch);
        using var border = new Pen(Color.FromArgb(120, 140, 160), 1f);
        g.DrawRectangle(border, x0, y0, cw, ch);

        using var grid = new Pen(Color.FromArgb(60, 120, 140, 160), 1f);
        for (int i = 1; i < 4; i++)
            g.DrawLine(grid, x0, y0 + ch * i / 4f, x0 + cw, y0 + ch * i / 4f);

        var pts = new PointF[line.Count];
        for (int i = 0; i < line.Count; i++)
        {
            double t = line.Count == 1 ? 0 : i / (double)(line.Count - 1);
            pts[i] = new PointF(x0 + (float)(t * (cw - 1)), y0 + ch - (float)(line[i].L / 100.0 * (ch - 1)));
        }
        using var curve = new Pen(Color.FromArgb(255, 255, 220, 120), 1.6f);
        if (pts.Length >= 2) g.DrawLines(curve, pts);

        string cap = $"明度剖面 0-100　{line[0].L:0} → {line[^1].L:0}";
        using var tb = new SolidBrush(Color.FromArgb(220, 220, 226, 232));
        g.DrawString(cap, font, tb, x0 + 6, y0 + 2);
    }

    private static void DrawLegend(Graphics g, ElementAnalysisResult result, List<VisualElement> shown,
        List<ElementRelation> rels, Font fBold, Font fNormal, Font fSmall, int w, int h, int legendH, float fs)
    {
        using (var bg = new SolidBrush(Color.FromArgb(246, 18, 20, 24)))
            g.FillRectangle(bg, 0, h, w, legendH);
        using (var sep = new Pen(Color.FromArgb(70, 80, 90)))
            g.DrawLine(sep, 0, h, w, h);

        float pad = fs * 1.2f;
        string title = $"元素明度关系　{result.Width}×{result.Height}　判型 {result.Profile}　" +
                       $"元素 {shown.Count} 个　关系 {result.Relations.Count} 条　耗时 {result.ElapsedMs} ms";
        using (var titleBrush = new SolidBrush(Color.FromArgb(200, 210, 220)))
            g.DrawString(title, fNormal, titleBrush, pad, h + pad * 0.6f);

        float top = h + pad * 0.6f + fs * 2.4f;
        float rowH = fs * 2.2f;
        float colW = (w - pad * 3) / 2f;

        // 左栏：元素清单
        using (var head = new SolidBrush(Color.FromArgb(170, 180, 192)))
            g.DrawString("元素　　　　　　　　明度 P50（P5~P95）　　色值　　占比", fSmall, head, pad, top);
        float y = top + rowH;
        int rows = 0;
        foreach (var e in shown)
        {
            if (rows >= 16) break;
            using var swatch = new SolidBrush(e.FrameColor);
            g.FillRectangle(swatch, pad, y + fs * 0.3f, fs * 1.0f, fs * 1.0f);
            using var line = new SolidBrush(Color.FromArgb(230, 234, 238));
            using var dim = new SolidBrush(Color.FromArgb(160, 170, 182));
            string name = $"{e.Id}. {e.KindName}";
            string val = $"{e.P50:0}（{e.P5:0}~{e.P95:0}）";
            string rest = $"{e.Hex}  {e.SharePercent:0.#}%" + (e.HasGradient ? $"  渐变 Δ{e.GradientDeltaL:0}" : "");
            g.DrawString(name, fNormal, line, pad + fs * 1.6f, y);
            g.DrawString(val, fSmall, line, pad + fs * 9.0f, y + fs * 0.35f);
            g.DrawString(rest, fSmall, dim, pad + fs * 17.0f, y + fs * 0.35f);
            y += rowH;
            rows++;
        }
        if (shown.Count > 16)
            using (var more = new SolidBrush(Color.FromArgb(140, 150, 162)))
                g.DrawString($"…另有 {shown.Count - 16} 个元素未列出", fSmall, more, pad + fs * 1.6f, y);

        // 右栏：关系
        float rx = pad * 2 + colW;
        using (var head = new SolidBrush(Color.FromArgb(170, 180, 192)))
            g.DrawString("关系　（文字/包含/相邻）　ΔL　比值　WCAG", fSmall, head, rx, top);
        float ry = top + rowH;
        int rrows = 0;
        foreach (var rel in rels)
        {
            if (rrows >= 16) break;
            var a = result.Find(rel.AId);
            var b = result.Find(rel.BId);
            if (a == null || b == null) continue;
            Color line = rel.Kind == RelationKind.TextOnFill ? Color.FromArgb(48, 214, 200)
                       : rel.Kind == RelationKind.Contained ? Color.FromArgb(140, 170, 220)
                       : Color.FromArgb(200, 200, 120);
            using var swatch = new SolidBrush(Color.FromArgb(230, line));
            g.FillRectangle(swatch, rx, ry + fs * 0.3f, fs * 1.0f, fs * 1.0f);
            using var brush = new SolidBrush(Color.FromArgb(230, 234, 238));
            using var dim = new SolidBrush(Color.FromArgb(160, 170, 182));
            string head2 = $"{b.Id}.{b.KindName} 在 {a.Id}.{a.KindName}";
            string tail = $"  ΔL {rel.DeltaL:0}　{Math.Max(1, Math.Min(a.P50, b.P50)):0} : {Math.Max(a.P50, b.P50):0}　" +
                          $"WCAG {rel.Wcag:0.0}（{ColorMath.ContrastLevel(rel.Wcag)}）";
            g.DrawString(head2, fSmall, brush, rx + fs * 1.6f, ry);
            g.DrawString(tail, fSmall, dim, rx + fs * 1.6f + g.MeasureString(head2, fSmall).Width, ry);
            ry += rowH;
            rrows++;
        }
        if (rels.Count > 16)
            using (var more = new SolidBrush(Color.FromArgb(140, 150, 162)))
                g.DrawString($"…另有 {rels.Count - 16} 条关系未列出", fSmall, more, rx + fs * 1.6f, ry);
    }

    private static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
}
