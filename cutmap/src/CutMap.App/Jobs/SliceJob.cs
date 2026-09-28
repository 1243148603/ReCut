using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

/// <summary>切片任务（FR-04）。</summary>
public sealed class SliceJob : Job
{
    public required string InputPath { get; init; }
    public required string OutputDirectory { get; init; }
    public int? MaxZoom { get; init; }

    public override string TypeName => "切片";
    public override string Display => $"{Path.GetFileName(InputPath)} → {OutputDirectory}";

    public override JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation)
    {
        var outcome = Slicer.Run(
            ctbHome,
            new SliceRequest(InputPath, OutputDirectory, MaxZoom),
            onProgress, onLog, cancellation);

        if (!outcome.Succeeded)
            return new JobResult(false, null, outcome.FailureReason, null);

        return new JobResult(
            true,
            $"{outcome.ActualTotal:N0} 个瓦片，最深 {outcome.MaxZoom} 级，用时 {outcome.Elapsed.TotalSeconds:F0} s",
            null,
            OutputDirectory);
    }
}
