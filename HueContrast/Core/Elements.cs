using System.Drawing;

namespace HueContrast.Core;

public enum ElementKind { Background, Panel, Button, Text, Icon, Shape }

public static class ElementKindNames
{
    public static string Of(ElementKind k) => k switch
    {
        ElementKind.Background => "背景",
        ElementKind.Panel => "面板",
        ElementKind.Button => "按钮",
        ElementKind.Text => "文字",
        ElementKind.Icon => "图形",
        _ => "色块"
    };

    public static Color Frame(ElementKind k) => k switch
    {
        ElementKind.Background => Color.FromArgb(120, 132, 148),
        ElementKind.Panel => Color.FromArgb(74, 144, 217),
        ElementKind.Button => Color.FromArgb(224, 161, 60),
        ElementKind.Text => Color.FromArgb(48, 214, 200),
        ElementKind.Icon => Color.FromArgb(126, 216, 86),
        _ => Color.FromArgb(198, 124, 186)
    };
}

public sealed class VisualElement
{
    public int Id { get; set; }
    public ElementKind Kind { get; set; }
    public Rectangle Bounds { get; set; }
    public int AreaPixels { get; set; }
    public double FillRatio { get; set; } = 1;
    public double SharePercent { get; set; }

    public double MeanL { get; set; }
    public double P5 { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }

    public double MeanHue { get; set; }
    public double MeanSat { get; set; }
    public string Hex { get; set; }
    public Color MeanColor { get; set; }

    public int ParentId { get; set; } = -1;
    public bool Suppressed { get; set; }

    public bool HasGradient { get; set; }
    public double GradientDeltaL { get; set; }
    public double GradientAngleDeg { get; set; }

    public string CustomLabel { get; set; }

    public string KindName => ElementKindNames.Of(Kind);
    public Color FrameColor => ElementKindNames.Frame(Kind);
    public string DisplayName => string.IsNullOrEmpty(CustomLabel) ? KindName + " " + Id : CustomLabel;
}

public enum RelationKind { TextOnFill, Contained, Adjacent }

public sealed class ElementRelation
{
    public int AId { get; set; }
    public int BId { get; set; }
    public RelationKind Kind { get; set; }
    public double DeltaL { get; set; }
    public double Ratio { get; set; }
    public double Wcag { get; set; }
    public double HueDiff { get; set; }
    public string Note { get; set; }
}

public sealed class ElementAnalysisResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int AnalysisWidth { get; set; }
    public int AnalysisHeight { get; set; }
    public string Profile { get; set; } = "UI";
    public long ElapsedMs { get; set; }
    public List<VisualElement> Elements { get; set; } = new();
    public List<ElementRelation> Relations { get; set; } = new();

    public VisualElement Find(int id) => Elements.FirstOrDefault(e => e.Id == id);
}

public sealed class ElementOptions
{
    public string Profile { get; set; } = "auto";
    public int MaxAnalysisWidth { get; set; } = 1600;
    public int SeedColors { get; set; } = 16;
}

/// <summary>明度与对比度基础计算。</summary>
public static class ColorMath
{
    /// <summary>灰度亮度 0-100（黑=0，白=100）。</summary>
    public static double GrayL(int r, int g, int b) => (0.299 * r + 0.587 * g + 0.114 * b) * 100.0 / 255.0;

    public static double GrayL(Color c) => GrayL(c.R, c.G, c.B);

    /// <summary>WCAG 相对亮度（线性化 sRGB）。</summary>
    public static double RelativeLuminance(int r, int g, int b)
    {
        static double Lin(int v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    public static double Wcag(Color a, Color b)
    {
        double la = RelativeLuminance(a.R, a.G, a.B);
        double lb = RelativeLuminance(b.R, b.G, b.B);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    public static double HueOf(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b));
        int mn = Math.Min(r, Math.Min(g, b));
        int d = mx - mn;
        if (d == 0) return 0;
        double h;
        if (mx == r) h = 60.0 * (((g - b) / (double)d) % 6);
        else if (mx == g) h = 60.0 * ((b - r) / (double)d + 2);
        else h = 60.0 * ((r - g) / (double)d + 4);
        if (h < 0) h += 360;
        return h;
    }

    public static double SatOf(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b));
        if (mx == 0) return 0;
        int mn = Math.Min(r, Math.Min(g, b));
        return (mx - mn) * 100.0 / mx;
    }

    public static double HueDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static int Percentile(int[] hist, double q)
    {
        long total = 0;
        for (int i = 0; i < hist.Length; i++) total += hist[i];
        if (total <= 0) return 0;
        long target = (long)Math.Ceiling(total * q);
        long cum = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            cum += hist[i];
            if (cum >= target) return i;
        }
        return hist.Length - 1;
    }

    /// <summary>对比度等级：文字级 4.5:1 为强。</summary>
    public static string ContrastLevel(double wcag)
    {
        if (wcag >= 7) return "极强";
        if (wcag >= 4.5) return "强";
        if (wcag >= 3) return "中";
        return "弱";
    }
}
