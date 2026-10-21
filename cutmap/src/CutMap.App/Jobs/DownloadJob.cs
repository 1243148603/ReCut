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
        tileNames = Tiles.Select(t => t.Name),
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
            // 并发下载下进度是按"完成"上报的（Done 是已完成幅数），日志口径跟着改；
            // 完成顺序不等于输入顺序，所以这里只报"哪幅完成了"，不暗示序号含义。
            if (p.Current.Length > 0)
                onLog($"[{p.Done}/{p.Total}] {p.Current} 完成（累计 {p.Bytes / (1024.0 * 1024):F1} MB）");
        });

        var outcome = Downloader.RunAsync(
            Source, Tiles, SaveDirectory, Pause, progress, onLog, cancellation)
            .GetAwaiter().GetResult();

        // FR-16：逐源记一次成败，便于判断内网镜像还活着没有——
        // 源管理界面会把失败原因挂在那一行上。
        try
        {
            new SourceStore().RecordOutcome(Source.Id, outcome.Succeeded, outcome.Succeeded ? null : outcome.Summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { onLog("下载结果已保留，但下载源状态保存失败：" + ex.Message); }

        var summary = outcome.Summary;
        if (!outcome.Succeeded)
            return new JobResult(false, null, summary, SaveDirectory);

        return new JobResult(true, summary, null, SaveDirectory);
    }
}
