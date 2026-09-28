using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

/// <summary>
/// 拼接裁剪任务（FR-07 矩形模式 + FR-17 算法产物）。
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

    public override string TypeName => "拼接裁剪";
    public override string Display =>
        $"{InputPaths.Count} 幅 → {Path.GetFileName(OutputPath)}";

    public override JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation)
    {
        _ = ctbHome; // 拼接只用到 GDAL，不碰 CTB。

        onLog(Mosaic.DescribeCommandLine(InputPaths, OutputPath, Target, Resampling));

        // gdalwarp 的进度回调粒度是"每个输入块"，这里把它映射到 0–90%，
        // 留最后 10% 给元数据与落盘——否则进度条会在写 json 时停在 100% 不动。
        Mosaic.Run(InputPaths, OutputPath, Target, Resampling,
            onProgress: p => onProgress(Math.Clamp(p * 0.9, 0.0, 0.9)),
            cancellation: cancellation);

        cancellation.ThrowIfCancellationRequested();

        string? metadataPath = null;
        if (WriteMetadata)
        {
            var metadata = AlgoMetadata.Measure(OutputPath, InputPaths);
            metadataPath = metadata.WriteBeside(OutputPath);
            onLog($"算法元数据：{metadataPath}");
        }
        onProgress(1.0);

        var info = RasterInfo.Read(OutputPath);
        var summary = $"{info.Width}×{info.Height}，{info.Summary()}" +
                      (metadataPath is null ? "" : "，已写元数据 json");
        return new JobResult(true, summary, null, OutputPath);
    }
}
