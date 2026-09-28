using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;
using CutMap.Core.Vector;

namespace CutMap.App.Jobs;

/// <summary>
/// 拼接裁剪任务（FR-07 两种模式 + FR-17 算法产物 + FR-15 边界分发）。
///
/// 覆盖检查在提交前已经做过了（<see cref="Coverage"/>），但这里不做"反正是界面查过的"
/// 的假设——任务可以来自队列里的任意位置，入口只保证自己的那次检查。
/// </summary>
public sealed class MosaicJob : Job
{
    public required IReadOnlyList<string> InputPaths { get; init; }
    public required string OutputPath { get; init; }
    public required BBox Target { get; init; }
    public string Resampling { get; init; } = Mosaic.DefaultResampling;

    /// <summary>是否同时写出算法元数据 json（FR-18 的算法产物开关）。</summary>
    public bool WriteMetadata { get; init; } = true;

    /// <summary>
    /// 非空即走**矢量模式**（FR-07 第二种）：按行政区划边界精确裁剪。
    /// 空则走矩形模式。
    /// </summary>
    public IReadOnlyList<BoundaryFeature>? Boundaries { get; init; }

    /// <summary>裙边高程。null = 自动取输入最低高程。</summary>
    public double? SkirtElevation { get; init; }

    public override string TypeName => Boundaries is null ? "拼接裁剪" : "矢量裁剪";
    public override string Display =>
        Boundaries is null
            ? $"{InputPaths.Count} 幅 → {Path.GetFileName(OutputPath)}"
            : $"{string.Join("+", Boundaries.Select(b => b.Name))} → {Path.GetFileName(OutputPath)}";

    public override string ParametersJson => System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = Boundaries is null ? "mosaic" : "mosaic-vector",
        inputs = InputPaths,
        output = OutputPath,
        west = Target.West, south = Target.South, east = Target.East, north = Target.North,
        resampling = Resampling,
        writeMetadata = WriteMetadata,
        // 只存编码不存几何：几何动辄几十万顶点，而 PRD FR-09 要的是"能回填"。
        // 有 code 就能从边界库里重新取到几何，存进去只是把历史文件撑爆。
        boundaries = Boundaries?.Select(b => new { b.Name, b.Code }),
        skirt = SkirtElevation,
    });

    public override JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation)
    {
        _ = ctbHome; // 拼接只用到 GDAL，不碰 CTB。

        string? boundaryPath = null;
        if (Boundaries is { Count: > 0 } boundaries)
        {
            // cutline 落在输出旁边，跟产物一起走——出了问题是哪条边界裁的，一眼能看到。
            boundaryPath = Path.ChangeExtension(OutputPath, ".cutline.geojson");
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? ".");
            BoundaryExport.WriteCutline(boundaries, boundaryPath);
            onLog("裁剪边界：" + string.Join("、", boundaries.Select(b => b.Display)));
            onLog(Mosaic.DescribeVectorCommandLine(InputPaths, OutputPath, boundaryPath, Resampling));

            // gdalwarp 的进度回调粒度是"每个输入块"，这里把它映射到 0–90%，
            // 留最后 10% 给元数据与落盘——否则进度条会在写 json 时停在 100% 不动。
            Mosaic.RunVector(
                InputPaths, OutputPath, boundaryPath, SkirtElevation, Resampling,
                onProgress: p => onProgress(Math.Clamp(p * 0.9, 0.0, 0.9)),
                cancellation: cancellation);
        }
        else
        {
            onLog(Mosaic.DescribeCommandLine(InputPaths, OutputPath, Target, Resampling));
            Mosaic.Run(InputPaths, OutputPath, Target, Resampling,
                       onProgress: p => onProgress(Math.Clamp(p * 0.9, 0.0, 0.9)),
                       cancellation: cancellation);
        }

        cancellation.ThrowIfCancellationRequested();

        string? metadataPath = null;
        if (WriteMetadata)
        {
            var metadata = AlgoMetadata.Measure(OutputPath, InputPaths);
            metadataPath = metadata.WriteBeside(OutputPath);
            onLog($"算法元数据：{metadataPath}");
        }

        // FR-15：边界矢量**只跟随"裁剪是否按行政区划边界"**。按行政区划下载、
        // 但按矩形裁剪时不得附带下载区域的边界——那不是裁剪范围，前端按它裁剪会错位。
        if (Boundaries is { Count: > 0 } exported)
        {
            var productBoundary = Path.ChangeExtension(OutputPath, ".boundary.geojson");
            BoundaryExport.WriteProductBoundary(
                exported, store: null, productBoundary,
                Path.GetFileNameWithoutExtension(OutputPath),
                sourceNotes: ["行政区划数据来源：天地图（国家地理信息公共服务平台）"]);
            onLog($"边界矢量：{productBoundary}");
        }

        onProgress(1.0);

        var info = RasterInfo.Read(OutputPath);
        var summary = $"{info.Width}×{info.Height}，{info.Summary()}" +
                      (metadataPath is null ? "" : "，已写元数据 json") +
                      (Boundaries is null ? "" : "，已附边界 geojson");
        return new JobResult(true, summary, null, OutputPath);
    }
}
