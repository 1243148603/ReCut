using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CutMap.Core.Geo;

namespace CutMap.Core.Terrain;

/// <summary>
/// Cesium 的 layer.json（FR-03）。
///
/// 格式约定**不开放给用户填**：<c>format</c> 恒为 heightmap-1.0、<c>scheme</c> 恒为
/// tms（CTB 的 geodetic 剖分就是 TMS）、扩展名必须**小写** <c>watermask</c>
/// （Cesium 匹配区分大小写，写成 Watermask 会静默失效——204 教程 §5.5 的修正记的就是这条）。
/// 这些不是"默认值"，是格式要求，填错了产物就加载不了。
/// </summary>
public sealed record LayerJson
{
    [JsonPropertyName("tilejson")]
    public string TileJson { get; init; } = "2.1.0";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "heightmap-1.0";

    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "tms";

    [JsonPropertyName("tiles")]
    public string[] Tiles { get; init; } = ["{z}/{x}/{y}.terrain"];

    [JsonPropertyName("minzoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public int MaxZoom { get; init; }

    /// <summary>西 南 东 北，WGS84 经纬度。与算法元数据 json 的 bounds_wgs84 同口径。</summary>
    [JsonPropertyName("bounds")]
    public required double[] Bounds { get; init; }

    [JsonPropertyName("extensions")]
    public string[] Extensions { get; init; } = ["watermask"];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LayerJson Create(
        string name, string description, string version, BBox bounds, int minZoom, int maxZoom) => new()
        {
            Name = name,
            Description = description,
            Version = version,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = [bounds.West, bounds.South, bounds.East, bounds.North],
        };

    /// <summary>
    /// 从一个已有的瓦片目录反推 layer.json 的内容（FR-03：用户指着已有的瓦片目录
    /// 也能单独跑"打包"）。bounds 与 maxzoom 都从目录结构量出来，不靠用户填。
    /// </summary>
    public static LayerJson FromExistingTiles(
        string tileDirectory, string name, string description, string version)
    {
        var coverage = InspectTiles(tileDirectory);
        if (coverage is null)
            throw new InvalidOperationException($"目录里没有 .terrain 瓦片：{tileDirectory}");

        return new LayerJson
        {
            Name = name,
            Description = description,
            Version = version,
            MinZoom = coverage.MinZoom,
            MaxZoom = coverage.DeepestZoom,
            Bounds =
            [
                coverage.Bounds.West, coverage.Bounds.South,
                coverage.Bounds.East, coverage.Bounds.North,
            ],
        };
    }

    /// <summary>扫瓦片目录，量出层级范围、瓦片数与地理范围。</summary>
    public static TileCoverage? InspectTiles(string tileDirectory)
    {
        if (!Directory.Exists(tileDirectory)) return null;

        var levels = Directory.EnumerateDirectories(tileDirectory)
            .Select(d => Path.GetFileName(d))
            .Select(name => int.TryParse(name, out var z) ? z : (int?)null)
            .OfType<int>()
            .OrderBy(z => z)
            .ToArray();
        if (levels.Length == 0) return null;

        // 最深层的瓦片包络就是最紧的范围：层越浅、单块瓦片覆盖越大，
        // 粗层的包络只会更松。所以只扫最深一层，既准又省。
        var deepest = levels[^1];
        long count = 0, xMin = long.MaxValue, xMax = long.MinValue;
        long yMin = long.MaxValue, yMax = long.MinValue;

        foreach (var xDir in Directory.EnumerateDirectories(Path.Combine(tileDirectory, deepest.ToString())))
        {
            if (!long.TryParse(Path.GetFileName(xDir), out var x)) continue;
            foreach (var file in Directory.EnumerateFiles(xDir, "*.terrain"))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var y)) continue;
                count++;
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
        }
        if (count == 0) return null;

        // 总瓦片数要把浅层也算上，不能只报最深一层。
        long total = 0;
        foreach (var z in levels)
            total += Directory.EnumerateFiles(Path.Combine(tileDirectory, z.ToString()),
                                              "*.terrain", SearchOption.AllDirectories).LongCount();

        return new TileCoverage(levels[0], deepest, total, BoundsOfTileRange(deepest, xMin, xMax, yMin, yMax));
    }

    /// <summary>
    /// 把某一层上的瓦片编号区间换成经纬度范围。
    ///
    /// CTB 的 geodetic 剖分根节点是 **2×1**（不是滑图的 1×1），所以第 z 层横向
    /// 有 2^(z+1) 块、纵向有 2^z 块：
    ///   经度每块 360 / 2^(z+1) 度
    ///   纬度每块 180 / 2^z 度
    /// y 从**南极**计数（TMS），所以南边界由 yMin 定。
    /// </summary>
    private static BBox BoundsOfTileRange(int zoom, long xMin, long xMax, long yMin, long yMax)
    {
        var lonPerTile = 360.0 / Math.Pow(2, zoom + 1);
        var latPerTile = 180.0 / Math.Pow(2, zoom);
        return new BBox(
            -180.0 + xMin * lonPerTile,
            -90.0 + yMin * latPerTile,
            -180.0 + (xMax + 1) * lonPerTile,
            -90.0 + (yMax + 1) * latPerTile);
    }

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>写到瓦片目录下（Cesium 从 <c>&lt;terrain&gt;/layer.json</c> 取）。返回写出的路径。</summary>
    public string WriteTo(string tileDirectory)
    {
        Directory.CreateDirectory(tileDirectory);
        var path = Path.Combine(tileDirectory, "layer.json");
        File.WriteAllText(path, ToJson(), new System.Text.UTF8Encoding(false));
        return path;
    }
}

/// <param name="DeepestZoom">最深（最精细）层级。</param>
/// <param name="TileCount">所有层级的瓦片总数。</param>
/// <param name="Bounds">最深层瓦片包络对应的经纬度范围。</param>
public sealed record TileCoverage(int MinZoom, int DeepestZoom, long TileCount, BBox Bounds);
