using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace HueContrast.Core;

/// <summary>把分析结果绘制到截图上：同色系区域全部框出，最暗/最亮单独加粗框，底部追加图例。</summary>
public static class Annotator
{
    public static Bitmap Render(Bitmap original, AnalysisResult result, bool drawRegions)
    {
        int w = original.Width, h = original.Height;
        float fs = Math.Max(9f, Math.Min(20f, w / 200f));
        float rowH = fs * 2.5f;
        int rows = result.Groups.Count;
        int legendH = (int)(rowH * (rows + 1) + 18);

        var canvas = new Bitmap(w, h + legendH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(original, 0, 0, w, h);

            using var fBold = new Font("Microsoft YaHei UI", fs, FontStyle.Bold);
            using var fNormal = new Font("Microsoft YaHei UI", fs, FontStyle.Regular);

            // 1) 同色系连通区域
            if (drawRegions)
            {
                foreach (var gr in result.Groups)
                {
                    Color frame = gr.FrameColor;
                    using var pen = new Pen(Color.FromArgb(235, frame), Math.Max(1f, fs / 7f))
                    { DashStyle = DashStyle.Dash };
                    using var fill = new SolidBrush(Color.FromArgb(28, frame));
                    foreach (var box in gr.Regions)
                    {
                        if (box.Width < 6 || box.Height < 6) continue;
                        g.FillRectangle(fill, box);
                        g.DrawRectangle(pen, box);
                    }
                }
            }

            // 2) 最暗 / 最亮取样框
            foreach (var gr in result.Groups)
            {
                bool same = gr.DarkBox.Width > 0 && gr.BrightBox.Width > 0 &&
                            gr.DarkBox == gr.BrightBox;
                if (same)
                {
                    DrawSampleBox(g, gr.DarkBox, gr.FrameColor,
                        $"{gr.Name}·最暗/最亮 {gr.DarkValue}", fBold, true, w, h);
                    continue;
                }
                if (gr.DarkBox.Width > 0)
                    DrawSampleBox(g, gr.DarkBox, gr.FrameColor, gr.Name + "·最暗 " + gr.DarkValue, fBold, true, w, h);
                if (gr.BrightBox.Width > 0)
                    DrawSampleBox(g, gr.BrightBox, gr.FrameColor, gr.Name + "·最亮 " + gr.BrightValue, fBold, false, w, h);
            }

            // 3) 图例
            using (var bg = new SolidBrush(Color.FromArgb(246, 18, 20, 24)))
                g.FillRectangle(bg, 0, h, w, legendH);
            using (var sep = new Pen(Color.FromArgb(70, 80, 90)))
                g.DrawLine(sep, 0, h, w, h);

            float pad = fs * 1.2f;
            string title = $"色相明度比值分析    {result.Width}×{result.Height}    共 {rows} 组    耗时 {result.ElapsedMs} ms";
            using (var titleBrush = new SolidBrush(Color.FromArgb(200, 210, 220)))
                g.DrawString(title, fNormal, titleBrush, pad, h + pad * 0.6f);

            float y = h + pad * 0.6f + fs * 2.6f;
            foreach (var gr in result.Groups)
            {
                Color frame = gr.FrameColor;
                using (var swatch = new SolidBrush(frame))
                    g.FillRectangle(swatch, pad, y + fs * 0.35f, fs * 1.1f, fs * 1.1f);
                using (var line = new SolidBrush(Color.FromArgb(228, 232, 236)))
                using (var dim = new SolidBrush(Color.FromArgb(150, 160, 170)))
                {
                    g.DrawString(gr.Name, fBold, line, pad + fs * 1.8f, y);
                    string rest = $"最暗 {gr.DarkValue} : 最亮 {gr.BrightValue}    差 {gr.Diff}    占比 {gr.SharePercent:0.#}%    区域 {gr.Regions.Count} 块";
                    g.DrawString(rest, fNormal, dim, pad + fs * 7.0f, y);
                }
                // 等级标签
                string levelText = gr.Level;
                Color levelBg = LevelColor(gr.Level);
                var sz = g.MeasureString(levelText, fBold);
                int chipW = (int)sz.Width + (int)(fs * 0.9f);
                int chipH = (int)sz.Height + (int)(fs * 0.35f);
                int chipX = (int)(w - pad - chipW);
                int chipY = (int)(y - fs * 0.1f);
                using (var cb = new SolidBrush(Color.FromArgb(235, levelBg)))
                    g.FillRectangle(cb, chipX, chipY, chipW, chipH);
                using (var tb = new SolidBrush(Color.FromArgb(250, 250, 250)))
                    g.DrawString(levelText, fBold, tb, chipX + fs * 0.45f, chipY + fs * 0.1f);

                y += rowH;
            }
        }
        return canvas;
    }

    private static void DrawSampleBox(Graphics g, Rectangle box, Color groupColor, string text, Font font, bool dark, int w, int h)
    {
        using var outer = new Pen(Color.FromArgb(240, 10, 12, 14), 4f);
        using var inner = new Pen(dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(255, 240, 120), 2f);
        Rectangle r = new Rectangle(box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4);
        g.DrawRectangle(outer, r);
        g.DrawRectangle(inner, r);

        // 标签
        var sz = g.MeasureString(text, font);
        int padX = (int)(font.Size * 0.6f);
        int padY = (int)(font.Size * 0.25f);
        int cw = (int)sz.Width + padX * 2;
        int ch = (int)sz.Height + padY * 2;
        int x = box.X;
        int y = box.Y - ch - 4;
        if (y < 2) y = box.Y + box.Height + 4;
        if (y + ch > h - 2) y = Math.Max(2, box.Y + 2);
        if (x + cw > w - 2) x = Math.Max(2, w - cw - 2);
        if (x < 2) x = 2;

        using (var bg = new SolidBrush(Color.FromArgb(235, dark ? Color.FromArgb(20, 22, 26) : groupColor)))
            g.FillRectangle(bg, x, y, cw, ch);
        using (var border = new Pen(Color.FromArgb(250, groupColor), 1f))
            g.DrawRectangle(border, x, y, cw, ch);
        using (var tb = new SolidBrush(Color.FromArgb(250, 250, 250)))
            g.DrawString(text, font, tb, x + padX, y + padY);
    }

    public static Color LevelColor(string level)
    {
        switch (level)
        {
            case "弱对比": return Color.FromArgb(90, 110, 130);
            case "中对比": return Color.FromArgb(214, 170, 60);
            case "强对比": return Color.FromArgb(214, 108, 50);
            default: return Color.FromArgb(214, 58, 84);
        }
    }
}
