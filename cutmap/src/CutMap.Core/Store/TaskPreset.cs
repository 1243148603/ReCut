using System.Text.Json.Serialization;
using CutMap.Core.Geo;

namespace CutMap.Core.Store;

/// <summary>
/// 一次任务的完整参数（FR-08）。
///
/// **为什么是"整套参数"而不是按字段分类的多个预设。** PRD 把预设写成分项的
/// （下载区 / 范围 / 切片参数 / layer.json 字段），但用户实际要复用的一定是
/// "上次那一整套"——不会有人想把 A 任务的范围配上 B 任务的层级。做成一套，
/// 新建任务时一键带出，才是真的省事。
///
/// **不含下载参数**：PRD 说预设要涵盖"下载区"，但下载是工具条上的独立入口，
/// 与新建任务向导没有上下文关系；下载对话框自己有一个范围预设下拉（复用裁剪范围预设），
/// 那才是"下载区"该待的地方。放在这里只会变成两个地方管同一件事，
/// 且其中一份永远读不到。
/// </summary>
public sealed record TaskPreset
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    // ---- 拼接裁剪 ----
    /// <summary>裁剪范围，西 南 东 北。null 表示"按行政区划边界裁"，不预设矩形。</summary>
    [JsonPropertyName("bounds")] public double[]? Bounds { get; init; }
    [JsonPropertyName("resampling")] public string Resampling { get; init; } = "bilinear";
    [JsonPropertyName("write_metadata")] public bool WriteMetadata { get; init; } = true;

    /// <summary>要按行政区划裁剪时，预设里带的区域编码（可多选）。</summary>
    [JsonPropertyName("boundary_codes")] public string[]? BoundaryCodes { get; init; }

    // ---- 切片 ----
    /// <summary>最深层级。null = 按栅格分辨率自动。</summary>
    [JsonPropertyName("max_zoom")] public int? MaxZoom { get; init; }

    // ---- layer.json ----
    [JsonPropertyName("layer_name")] public string LayerName { get; init; } = "terrain";
    [JsonPropertyName("layer_description")] public string LayerDescription { get; init; } = "Cesium terrain tiles";
    [JsonPropertyName("layer_version")] public string LayerVersion { get; init; } = "1.0.0";

    // ---- 导出选项（FR-18）----
    [JsonPropertyName("zip_tiles")] public bool ZipTiles { get; init; }

    [JsonPropertyName("zip_algo")] public bool ZipAlgo { get; init; }

    [JsonIgnore] public bool IsBuiltIn { get; init; }

    [JsonIgnore]
    public BBox? ClipBox => Bounds is { Length: 4 }
        ? new BBox(Bounds[0], Bounds[1], Bounds[2], Bounds[3]) : null;

    public override string ToString() => Name;
}
