using CutMap.Core.Packaging;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

/// <summary>
/// 打包任务（FR-05）。**独立步骤**：指着一个已有的瓦片目录就能单独跑，
/// 不必重跑切片——所以它的输入是路径，不依赖上游任务。
/// </summary>
public sealed class PackageJob : Job
{
    public required string OutputDirectory { get; init; }
    public string? TilesDirectory { get; init; }
    public string? AlgoTifPath { get; init; }
    public bool ZipTiles { get; init; }
    public bool ZipAlgo { get; init; }

    /// <summary>layer.json 的可编辑字段（FR-03）。</summary>
    public required string LayerName { get; init; }
    public required string LayerDescription { get; init; }
    public required string LayerVersion { get; init; }

    /// <summary>
    /// 边界矢量（FR-15）：矢量裁剪会在 tif 旁写同名 *.boundary.geojson，
    /// 打包时把它一并放进瓦片目录。**只按"与本次裁剪 tif 同名"这一条规则找**，
    /// 有就带、没有就算了——不是所有产物都来自矢量裁剪。
    /// </summary>
    private string? ResolveBoundaryGeoJson()
    {
        if (AlgoTifPath is not { } tif) return null;
        var candidate = Path.ChangeExtension(tif, ".boundary.geojson");
        return File.Exists(candidate) ? candidate : null;
    }

    public override string TypeName => "打包";
    public override string Display =>
        TilesDirectory is not null && AlgoTifPath is not null ? "前端 + 算法"
        : TilesDirectory is not null ? $"前端：{Path.GetFileName(TilesDirectory.TrimEnd(Path.DirectorySeparatorChar))}"
        : $"算法：{Path.GetFileName(AlgoTifPath ?? "")}";

    public override string ParametersJson => System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "package",
        output = OutputDirectory,
        tiles = TilesDirectory,
        tif = AlgoTifPath,
        zipTiles = ZipTiles,
        zipAlgo = ZipAlgo,
        layerName = LayerName,
        layerDescription = LayerDescription,
        layerVersion = LayerVersion,
    });

    public override JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation)
    {
        _ = ctbHome; // 打包只用 GDAL（读 tif 实测元数据）与文件操作。

        // 用户能编辑 name/description/version，但 format/scheme/tiles/extensions
        // 是格式约定，不开放——填错了产物就加载不了（FR-03）。
        // **只扫一遍目录**：layer.json 的 minzoom/bounds 与打包要的瓦片统计
        // 共用这一次扫描的结果（22 万瓦片扫一遍要好几秒，之前这条路上扫了四遍）。
        LayerJson? layer = null;
        TileCoverage? coverage = null;
        if (TilesDirectory is { } tilesDir)
        {
            coverage = LayerJson.InspectTiles(tilesDir);
            if (coverage is not null)
            {
                layer = LayerJson.Create(LayerName, LayerDescription, LayerVersion,
                                         coverage.Bounds, coverage.MinZoom, coverage.DeepestZoom);
                onLog($"layer.json：{layer.MinZoom}–{layer.MaxZoom} 级，" +
                      $"bounds [{string.Join(", ", layer.Bounds.Select(v => v.ToString("F4")))}]");
            }
        }

        var result = Packager.Run(
            new PackageRequest(
                OutputDirectory,
                TilesDirectory,
                AlgoTifPath,
                ZipTiles,
                ZipAlgo,
                layer,
                coverage,
                BoundaryGeoJsonPath: ResolveBoundaryGeoJson()),
            onProgress, onLog, cancellation);

        return new JobResult(true, result.Summary, null,
                             result.TilesZipPath ?? result.AlgoZipPath ?? OutputDirectory);
    }
}
