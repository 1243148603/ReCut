using CutMap.Core.Download;
using CutMap.Core.Geo;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

/// <summary>DEM 分幅下载任务（FR-06）。唯一支持运行中暂停的类型。</summary>
public sealed class DownloadJob : Job
{
    public required DownloadSource Source { get; init; }
    public required IReadOnlyList<DemTile> Tiles { get; init; }
    public required string SaveDirectory { get; init; }

    /// <summary>区域与所用下载源一并落进任务参数（FR-06），历史回填时要能原样还原。</summary>
    public required BBox Area { get; init; }

    /// <summary>这批分幅下载完的目录，供「继续处理这批 DEM」预填（FR-06）。</summary>
    public string? DownloadedDirectory => SaveDirectory;

    public override string TypeName => "下载";
    public override string Display =>
        $"{Tiles.Count} 幅（{Source.Name}）→ {SaveDirectory}";

    public override bool SupportsPause => true;

    public override string ParametersJson => System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "download",
        sourceId = Source.Id,
        sourceName = Source.Name,
        urlTemplate = Source.UrlTemplate,
        saveDirectory = SaveDirectory,
        west = Area.West, south = Area.South, east = Area.East, north = Area.North,
        // 只存区域与源：分幅可以从区域确定性地重算（DemTileGrid.Enumerate），
        // 存几千个名字只会把历史文件撑大。
        tileCount = Tiles.Count,
    });

    public override JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation)
    {
        _ = ctbHome; // 下载不碰 CTB。

        var progress = new Progress<(int Done, int Total, string Current, long Bytes)>(p =>
        {
            var fraction = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            onProgress(Math.Clamp(fraction, 0, 1));
            if (p.Current.Length > 0)
                onLog($"[{p.Done + 1}/{p.Total}] {p.Current}（累计 {p.Bytes / (1024.0 * 1024):F1} MB）");
        });

        var outcome = Downloader.RunAsync(
            Source, Tiles, SaveDirectory, Pause, progress, onLog, cancellation)
            .GetAwaiter().GetResult();

        var summary = outcome.Summary;
        if (!outcome.Succeeded)
            return new JobResult(false, null, summary, SaveDirectory);

        return new JobResult(true, summary, null, SaveDirectory);
    }
}
