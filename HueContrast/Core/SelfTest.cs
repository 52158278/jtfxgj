using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace HueContrast.Core;

/// <summary>无界面自检：合成图 + 样例图跑一遍两条管线，输出数值与标注样图，供开发期验证。</summary>
public static class SelfTest
{
    public static int Run()
    {
        string outDir = Path.Combine(AppContext.BaseDirectory, "selftest");
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        bool ok = true;

        // ---------- A. 色相管线 ----------
        var hueOpt = new AnalyzerOptions();
        using (var input = BuildHueImage())
        {
            input.Save(Path.Combine(outDir, "input.png"), ImageFormat.Png);
            var result = HueAnalyzer.Analyze(input, hueOpt);
            using var annotated = Annotator.Render(input, result, true);
            annotated.Save(Path.Combine(outDir, "hue-annotated.png"), ImageFormat.Png);

            sb.AppendLine($"[色相] image={result.Width}x{result.Height} analysis={result.AnalysisWidth}x{result.AnalysisHeight} elapsed={result.ElapsedMs}ms groups={result.Groups.Count}");
            foreach (var g in result.Groups)
            {
                sb.AppendLine($"  [{g.Slot}] {g.Name} share={g.SharePercent:0.#}% dark={g.DarkValue} bright={g.BrightValue} " +
                              $"abs={g.AbsMin}..{g.AbsMax} ratio={g.Ratio:0.00} level={g.Level} regions={g.Regions.Count}");
            }

            var ach = result.Find(HueAnalyzer.AchromaticSlot);
            if (ach == null)
            {
                sb.AppendLine("  FAIL: 未识别到无彩色组");
                ok = false;
            }
            else if (!(ach.DarkValue <= 3 && ach.BrightValue >= 97))
            {
                sb.AppendLine($"  FAIL: 无彩色明度范围异常 dark={ach.DarkValue} bright={ach.BrightValue}");
                ok = false;
            }
            else sb.AppendLine("  PASS 无彩色明度范围");
        }

        // ---------- B. 元素管线（合成 UI） ----------
        var elOpt = new ElementOptions();
        using (var mock = BuildUiMock())
        {
            mock.Save(Path.Combine(outDir, "ui-input.png"), ImageFormat.Png);
            var er = ElementDetector.Analyze(mock, elOpt);
            using var render = ElementsRenderer.Render(mock, er, new RenderOptions());
            render.Save(Path.Combine(outDir, "ui-elements.png"), ImageFormat.Png);
            DumpElements(sb, "合成UI", er);

            bool hasText = er.Elements.Any(e => e.Kind == ElementKind.Text);
            bool hasButton = er.Elements.Any(e => e.Kind == ElementKind.Button);
            var textRel = er.Relations.FirstOrDefault(r => r.Kind == RelationKind.TextOnFill);
            sb.AppendLine($"  text={hasText} button={hasButton} textRelation={(textRel != null ? "ΔL " + textRel.DeltaL.ToString("0") : "none")}");
            if (!hasText || textRel == null || textRel.DeltaL < 20)
            {
                sb.AppendLine("  FAIL: 文字/按钮关系未识别");
                ok = false;
            }
            else sb.AppendLine("  PASS 文字在按钮/背景上的明度关系");
        }

        // ---------- C. 真实样例回归（可选） ----------
        foreach (string name in new[] { "sample-ui.png", "sample-illustration.png" })
        {
            string sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "samples", name);
            if (!File.Exists(sample)) sample = Path.Combine(AppContext.BaseDirectory, "samples", name);
            if (!File.Exists(sample))
            {
                sb.AppendLine($"  SKIP 未找到 samples/{name}");
                continue;
            }

            try
            {
                using var src = new Bitmap(sample);
                var er = ElementDetector.Analyze(src, elOpt);
                using var render = ElementsRenderer.Render(src, er, new RenderOptions());
                render.Save(Path.Combine(outDir, "elements-" + Path.GetFileNameWithoutExtension(name) + ".png"), ImageFormat.Png);
                DumpElements(sb, name, er);

                if (er.Elements.Count < 3 || er.Relations.Count == 0)
                {
                    sb.AppendLine($"  FAIL: {name} 元素/关系过少");
                    ok = false;
                }
                else sb.AppendLine($"  PASS {name} 元素与关系");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  FAIL: {name} 分析异常 " + ex.Message);
                ok = false;
            }
        }

        // ---------- D. 界面构造冒烟 ----------
        try
        {
            using var mock = BuildUiMock();
            var er = ElementDetector.Analyze(mock, elOpt);
            var hue = new AnalysisResult { Width = mock.Width, Height = mock.Height };
            using (var f1 = new UI.ResultForm(new Bitmap(mock), er, hue, null)) { }
            using (var f2 = new UI.SettingsForm(new AppSettings())) { }
            sb.AppendLine("forms-ok");
        }
        catch (Exception ex)
        {
            ok = false;
            sb.AppendLine("FAIL: 界面构造异常 " + ex);
        }

        File.WriteAllText(Path.Combine(outDir, "result.txt"), sb.ToString(), Encoding.UTF8);
        return ok ? 0 : 1;
    }

    private static void DumpElements(StringBuilder sb, string tag, ElementAnalysisResult er)
    {
        sb.AppendLine($"[元素/{tag}] {er.Width}x{er.Height} analysis={er.AnalysisWidth}x{er.AnalysisHeight} " +
                      $"profile={er.Profile} elapsed={er.ElapsedMs}ms elements={er.Elements.Count} relations={er.Relations.Count}");
        foreach (var e in er.Elements.Take(14))
        {
            sb.AppendLine($"  #{e.Id} {e.KindName} box={Fmt(e.Bounds)} L50={e.P50:0} P5~95={e.P5:0}~{e.P95:0} " +
                          $"hex={e.Hex} share={e.SharePercent:0.#}% parent={e.ParentId}" +
                          (e.HasGradient ? $" grad=Δ{e.GradientDeltaL:0}@{e.GradientAngleDeg:0}°" : ""));
        }
        foreach (var r in er.Relations.Take(10))
        {
            var a = er.Find(r.AId);
            var b = er.Find(r.BId);
            sb.AppendLine($"  R {r.Kind}: {b?.KindName}#{b?.Id} in {a?.KindName}#{a?.Id} ΔL={r.DeltaL:0} " +
                          $"wcag={r.Wcag:0.00} ratio={Math.Max(1, Math.Min(a?.P50 ?? 1, b?.P50 ?? 1)):0}:{Math.Max(a?.P50 ?? 0, b?.P50 ?? 0):0}");
        }
    }

    private static string Fmt(Rectangle r) => r.Width <= 0 ? "(empty)" : $"({r.X},{r.Y},{r.Width},{r.Height})";

    private static Bitmap BuildHueImage()
    {
        var bmp = new Bitmap(900, 600, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(250, 250, 250));

            // 左侧纯黑块：无彩色最暗
            g.FillRectangle(Brushes.Black, 0, 0, 260, 600);

            // 中部红色渐变：暗红 -> 亮红
            using (var pen = new Pen(Color.Black))
            {
                for (int y = 0; y < 600; y++)
                {
                    double t = y / 599.0;
                    pen.Color = Color.FromArgb((int)(45 + (250 - 45) * t), (int)(5 + (70 - 5) * t), (int)(5 + (70 - 5) * t));
                    g.DrawLine(pen, 260, y, 560, y);
                }
            }

            g.FillRectangle(new SolidBrush(Color.FromArgb(30, 150, 70)), 600, 60, 220, 180);
            g.FillRectangle(new SolidBrush(Color.FromArgb(25, 45, 165)), 600, 340, 220, 180);
            g.FillRectangle(Brushes.White, 830, 0, 70, 600);
        }
        return bmp;
    }

    /// <summary>合成一张最小界面：深色背景 + 白色标题 + 近背景色的按钮（含白字）。</summary>
    private static Bitmap BuildUiMock()
    {
        var bmp = new Bitmap(640, 360, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(42, 46, 51));

            using var titleFont = new Font("Microsoft YaHei UI", 26f, FontStyle.Bold);
            using var btnFont = new Font("Microsoft YaHei UI", 16f, FontStyle.Regular);
            g.DrawString("色彩与明度", titleFont, Brushes.White, 40, 36);

            g.FillRectangle(new SolidBrush(Color.FromArgb(46, 50, 56)), 40, 170, 230, 66);
            g.DrawRectangle(new Pen(Color.FromArgb(86, 91, 99), 1f), 40, 170, 229, 65);
            g.DrawString("开始分析", btnFont, new SolidBrush(Color.FromArgb(242, 244, 246)), 62, 188);

            // 右侧渐变卡片
            for (int x = 0; x < 180; x++)
            {
                int v = (int)(50 + 40 * (x / 179.0));
                using var pen = new Pen(Color.FromArgb(v, v + 6, v + 14));
                g.DrawLine(pen, 360 + x, 60, 360 + x, 260);
            }
        }
        return bmp;
    }
}
