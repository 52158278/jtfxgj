using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HueContrast.Core;

public sealed class HistoryGroupDto
{
    public int Slot { get; set; }
    public string Name { get; set; }
    public string ColorHex { get; set; }
    public double SharePercent { get; set; }
    public int Dark { get; set; }
    public int Bright { get; set; }
    public int Diff { get; set; }
    public string Level { get; set; }
    public int RegionCount { get; set; }
}

public sealed class HistoryElementDto
{
    public int Id { get; set; }
    public string Kind { get; set; }
    public double P50 { get; set; }
    public double P5 { get; set; }
    public double P95 { get; set; }
    public string Hex { get; set; }
    public double SharePercent { get; set; }
}

public sealed class HistoryRelationDto
{
    public int AId { get; set; }
    public int BId { get; set; }
    public string Kind { get; set; }
    public double DeltaL { get; set; }
    public double Wcag { get; set; }
}

public sealed class HistoryMeta
{
    public int Version { get; set; } = 2;
    public string Id { get; set; }
    public DateTime Created { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Profile { get; set; }
    public string OriginalFile { get; set; } = "original.png";
    public string AnnotatedFile { get; set; } = "elements.png";
    public string HueFile { get; set; } = "hue.png";
    public string Summary { get; set; }
    public List<HistoryElementDto> Elements { get; set; } = new();
    public List<HistoryRelationDto> Relations { get; set; } = new();
    public List<HistoryGroupDto> Groups { get; set; } = new();

    [JsonIgnore] public string Dir { get; set; }
    [JsonIgnore] public string AnnotatedPath => Path.Combine(Dir ?? string.Empty, AnnotatedFile);
    [JsonIgnore] public string OriginalPath => Path.Combine(Dir ?? string.Empty, OriginalFile);
    [JsonIgnore] public string HuePath => Path.Combine(Dir ?? string.Empty, HueFile);
    [JsonIgnore] public string Title => Created.ToString("yyyy-MM-dd HH:mm:ss");
}

public static class HistoryStore
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HueContrast", "history");

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Save(Bitmap original, Bitmap elementsView, Bitmap hueView,
        ElementAnalysisResult er, AnalysisResult hue)
    {
        Directory.CreateDirectory(Root);
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        original.Save(Path.Combine(dir, "original.png"), ImageFormat.Png);
        if (elementsView != null) elementsView.Save(Path.Combine(dir, "elements.png"), ImageFormat.Png);
        if (hueView != null) hueView.Save(Path.Combine(dir, "hue.png"), ImageFormat.Png);

        var meta = new HistoryMeta
        {
            Id = id,
            Created = DateTime.Now,
            Width = original.Width,
            Height = original.Height,
            Profile = er?.Profile
        };

        if (er != null)
        {
            foreach (var e in er.Elements.Take(16))
            {
                meta.Elements.Add(new HistoryElementDto
                {
                    Id = e.Id, Kind = e.KindName, P50 = e.P50, P5 = e.P5, P95 = e.P95,
                    Hex = e.Hex, SharePercent = e.SharePercent
                });
            }
            foreach (var r in er.Relations.Take(10))
            {
                meta.Relations.Add(new HistoryRelationDto
                {
                    AId = r.AId, BId = r.BId,
                    Kind = r.Kind.ToString(), DeltaL = Math.Round(r.DeltaL, 1), Wcag = Math.Round(r.Wcag, 2)
                });
            }
        }

        if (hue != null)
        {
            foreach (var g in hue.Groups)
            {
                meta.Groups.Add(new HistoryGroupDto
                {
                    Slot = g.Slot, Name = g.Name, ColorHex = g.ColorHex,
                    SharePercent = g.SharePercent, Dark = g.DarkValue, Bright = g.BrightValue,
                    Diff = g.Diff, Level = g.Level, RegionCount = g.Regions.Count
                });
            }
        }

        var lines = new List<string>();
        if (meta.Elements.Count > 0)
            lines.Add($"{meta.Profile} · {meta.Elements.Count} 元素 · {meta.Relations.Count} 关系");
        foreach (var g in meta.Groups.Take(2))
            lines.Add($"{g.Name} {g.Dark} : {g.Bright}（{g.Level}）");
        meta.Summary = string.Join("　", lines);

        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, JsonOpt));
        return dir;
    }

    public static List<HistoryMeta> LoadAll()
    {
        var list = new List<HistoryMeta>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (string dir in Directory.GetDirectories(Root))
            {
                string metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var meta = JsonSerializer.Deserialize<HistoryMeta>(File.ReadAllText(metaPath), JsonOpt);
                    if (meta == null) continue;
                    meta.Dir = dir;
                    // 旧版记录没有 elements.png，回退到 annotated.png
                    if (!File.Exists(meta.AnnotatedPath) &&
                        File.Exists(Path.Combine(dir, "annotated.png")))
                        meta.AnnotatedFile = "annotated.png";
                    if (!File.Exists(meta.HuePath)) meta.HueFile = meta.AnnotatedFile;
                    list.Add(meta);
                }
                catch
                {
                    // 跳过损坏条目
                }
            }
        }
        catch
        {
            // 目录不可读时返回已有结果
        }

        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        return list;
    }

    public static void Delete(HistoryMeta meta)
    {
        try
        {
            if (!string.IsNullOrEmpty(meta.Dir) && Directory.Exists(meta.Dir))
                Directory.Delete(meta.Dir, true);
        }
        catch
        {
            // 忽略删除失败
        }
    }
}
