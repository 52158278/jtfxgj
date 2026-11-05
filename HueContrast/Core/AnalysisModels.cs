using System.Drawing;
using System.Text.Json.Serialization;

namespace HueContrast.Core;

/// <summary>分析参数，均可从设置界面调整。</summary>
public sealed class AnalyzerOptions
{
    /// <summary>参与统计的缩小图最大宽度，越大越准、越慢。</summary>
    public int MaxAnalysisWidth { get; set; } = 2560;

    /// <summary>饱和度低于该值(0-100)归入“无彩色”组。</summary>
    public int AchromaticSatThreshold { get; set; } = 10;

    /// <summary>像素占比低于该百分比(%)的色相组不参与标注。</summary>
    public double MinSharePercent { get; set; } = 1.0;

    /// <summary>每个色相组最多绘制多少个区域框。</summary>
    public int MaxRegionsPerGroup { get; set; } = 12;

    /// <summary>区域连通块面积下限（占全图像素百分比）。</summary>
    public double MinRegionAreaPercent { get; set; } = 0.25;

    /// <summary>最暗/最亮取样框的网格尺寸（分析图像素）。</summary>
    public int SampleBlockSize { get; set; } = 12;
}

/// <summary>单个色相组的分析结果。</summary>
public sealed class GroupResult
{
    /// <summary>0-11 为 30° 色相桶，12 为无彩色。</summary>
    public int Slot { get; set; }
    public string Name { get; set; }
    public string ColorHex { get; set; }
    public double SharePercent { get; set; }
    public int PixelCount { get; set; }

    /// <summary>最暗分位值 0-100。</summary>
    public int DarkValue { get; set; }
    /// <summary>最亮分位值 0-100。</summary>
    public int BrightValue { get; set; }
    public int AbsMin { get; set; }
    public int AbsMax { get; set; }

    public Rectangle DarkBox { get; set; }
    public Rectangle BrightBox { get; set; }
    public List<Rectangle> Regions { get; set; } = new();

    public string Level { get; set; }
    public double Ratio { get; set; }

    [JsonIgnore] public int Diff => BrightValue - DarkValue;
    [JsonIgnore] public string RatioText => DarkValue + " : " + BrightValue;
    [JsonIgnore] public Color FrameColor => ColorTranslator.FromHtml(ColorHex);
    [JsonIgnore] public string SummaryLine =>
        $"{Name}  {RatioText}  差{Diff}  {Level}  占比{SharePercent:0.#}%  区域{Regions.Count}";
}

/// <summary>整张截图的分析结果。</summary>
public sealed class AnalysisResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int AnalysisWidth { get; set; }
    public int AnalysisHeight { get; set; }
    public long ElapsedMs { get; set; }
    public List<GroupResult> Groups { get; set; } = new();
    public AnalyzerOptions Options { get; set; } = new();

    public GroupResult Find(int slot) => Groups.FirstOrDefault(g => g.Slot == slot);
}
