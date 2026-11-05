using System.Text.Encodings.Web;
using System.Text.Json;

namespace HueContrast.Core;

public sealed class AppSettings
{
    public string HotkeyText { get; set; } = "Ctrl+Alt+Q";
    public int AchromaticSat { get; set; } = 10;
    public double MinSharePercent { get; set; } = 1.0;
    public int MaxAnalysisWidth { get; set; } = 2560;
    public int MaxRegionsPerGroup { get; set; } = 12;
    public double MinRegionAreaPercent { get; set; } = 0.25;

    /// <summary>元素分析预设：auto / ui / illustration。</summary>
    public string ElementProfile { get; set; } = "auto";

    /// <summary>元素分析缩小图最大宽度。</summary>
    public int ElementMaxWidth { get; set; } = 1600;

    /// <summary>元素量化的主色数量。</summary>
    public int ElementSeedColors { get; set; } = 16;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HueContrast");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpt));
    }

    public HotkeySpec GetHotkey() => HotkeySpec.Parse(HotkeyText);

    public AnalyzerOptions ToOptions() => new()
    {
        MaxAnalysisWidth = MaxAnalysisWidth,
        AchromaticSatThreshold = AchromaticSat,
        MinSharePercent = MinSharePercent,
        MaxRegionsPerGroup = MaxRegionsPerGroup,
        MinRegionAreaPercent = MinRegionAreaPercent
    };

    public ElementOptions ToElementOptions() => new()
    {
        Profile = ElementProfile,
        MaxAnalysisWidth = ElementMaxWidth,
        SeedColors = ElementSeedColors
    };
}
