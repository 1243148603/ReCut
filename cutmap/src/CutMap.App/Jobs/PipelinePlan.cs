using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Vector;

namespace CutMap.App.Jobs;

public sealed record PipelinePlan(
    IReadOnlyList<string> Inputs, string Root, bool Mosaic, bool Slice, bool Package,
    bool WantTiles, bool WantAlgo, BBox Target, string? ExistingTiles = null,
    IReadOnlyList<BoundaryFeature>? Boundaries = null, int? MaxZoom = null,
    string Resampling = "bilinear", bool ZipTiles = false, bool ZipAlgo = false,
    string LayerName = "terrain", string LayerDescription = "Cesium terrain tiles", string LayerVersion = "1.0.0",
    BBox? LayerBounds = null, int? LayerMaxZoom = null, IReadOnlyList<RasterInfo>? RasterSummaries = null)
{
    public string Tif => Mosaic ? Path.Combine(Root, "dem_clipped.tif") : Inputs.Single();
    public string Tiles => Slice ? Path.Combine(Root, "cesium_terrain") : ExistingTiles ?? "";

    public IReadOnlyList<Job> CreateJobs()
    {
        if (!WantTiles && !WantAlgo) throw new ArgumentException("至少选择一套产物。");
        if (string.IsNullOrWhiteSpace(Root)) throw new ArgumentException("请选择输出根目录。");
        if (!Mosaic && !Slice && !Package && !WantAlgo) throw new ArgumentException("至少选择一步。");
        if (Slice && !WantTiles) throw new ArgumentException("不需要前端产物时请取消切片步骤。");
        if ((Mosaic || Slice || WantAlgo) && Inputs.Count == 0) throw new ArgumentException("请选择输入 DEM。");
        if (!Mosaic && (Slice || WantAlgo) && Inputs.Count != 1) throw new ArgumentException("多幅 DEM 必须先勾选拼接裁剪。");
        if (Mosaic)
        {
            var target = Boundaries is { Count: > 0 } ? BoundaryExport.UnionBounds(Boundaries) : Target;
            if (!target.IsValid(out var reason)) throw new ArgumentException(reason);
            var coverage = Coverage.Check(RasterSummaries ?? Inputs.Select(RasterInfo.Read).ToArray(), target);
            if (!coverage.Covered) throw new ArgumentException(coverage.Describe());
            if (Inputs.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(Tif), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("裁剪输出不能覆盖输入文件。");
        }
        if (WantTiles && Package && !Slice && !Directory.Exists(Tiles)) throw new ArgumentException("请选择已有瓦片目录。");
        if (Slice)
        {
            var info = RasterSummaries?.FirstOrDefault() ?? RasterInfo.Read(Inputs[0]);
            var estimate = CtbGrid.Estimate(Mosaic ? (Boundaries is { Count: > 0 } ? BoundaryExport.UnionBounds(Boundaries) : Target) : info.Extent,
                MaxZoom ?? CtbGrid.ReferenceMaxZoom(info.PixelSizeX));
            if (CtbGrid.ValidateCost(estimate) is { } rejection) throw new ArgumentException(rejection);
        }
        var chain = Guid.NewGuid().ToString("N");
        // 完整流程知道瓦片实际来自哪一份 DEM，layer bounds 应沿用该数据范围。
        // 只有“独立打包已有瓦片”缺少 DEM 时，才退回按瓦片包络反推。
        BBox? productBounds = LayerBounds;
        if (productBounds is null && Slice)
        {
            productBounds = Mosaic
                ? (Boundaries is { Count: > 0 } ? BoundaryExport.UnionBounds(Boundaries) : Target)
                : (RasterSummaries?.FirstOrDefault() ?? RasterInfo.Read(Inputs.Single())).Extent;
        }
        var jobs = new List<Job>();
        if (Mosaic) jobs.Add(new MosaicJob { InputPaths = Inputs, OutputPath = Tif, Target = Target,
            Boundaries = Boundaries, Resampling = Resampling, WriteMetadata = WantAlgo, ChainId = chain });
        if (Slice) jobs.Add(new SliceJob { InputPath = Tif, OutputDirectory = Tiles, MaxZoom = MaxZoom, ChainId = chain });
        if (Package || (WantAlgo && !Mosaic)) jobs.Add(new PackageJob { OutputDirectory = Root,
            TilesDirectory = WantTiles && Package ? Tiles : null, AlgoTifPath = WantAlgo ? Tif : null,
            ZipTiles = Package && WantTiles && ZipTiles, ZipAlgo = Package && WantAlgo && ZipAlgo,
            LayerName = LayerName, LayerDescription = LayerDescription, LayerVersion = LayerVersion,
            LayerBounds = productBounds, LayerMaxZoom = LayerMaxZoom, ChainId = chain });
        return jobs;
    }
}
