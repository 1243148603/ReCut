using System.Net;
using System.Net.Http.Headers;

namespace CutMap.Core.Download;

public enum TileOutcome
{
    Downloaded,
    /// <summary>本地已有且大小与远端一致，没重下。</summary>
    Skipped,
    /// <summary>远端没有这一幅（HTTP 404）。海面上的分幅本来就缺，不是错误。</summary>
    NotFound,
    Failed,
}

public sealed record TileResult(DemTile Tile, TileOutcome Outcome, long Bytes, string? Error);

public sealed record DownloadOutcome(
    IReadOnlyList<TileResult> Results,
    long BytesDownloaded,
    string Summary)
{
    public bool Succeeded => Results.All(r => r.Outcome is not TileOutcome.Failed);
}

/// <summary>
/// 分幅下载（FR-06）。
///
/// 三条从 204 教程与基线里继承下来的行为：
///
///   1. **先校验后跳过**。直接用 <c>Range</c> 请求一个已经完整的文件，S3 会返回 416，
///      看起来像失败。所以先用 HEAD 取远端长度、与本地比，一致就跳过。
///   2. **写入 .part，完成才改名**。中途断线留下的半个文件如果直接叫 .tif，
///      下一次判断"是否存在"就会把它当成已完成。用 .part 让"这个文件名存在"
///      本身就等于"这份数据完整"。
///   3. **源不可达就如实报错，不换源**。静默回退到别的源会让"这批数据是从哪下的"
///      变得不可追溯（FR-16）。
/// </summary>
public static class Downloader
{
    /// <summary>分块大小。1 MB 是 PRD FR-11 指定的暂停粒度。</summary>
    private const int ChunkBytes = 1024 * 1024;

    private const int MaxAttempts = 3;

    public static async Task<DownloadOutcome> RunAsync(
        DownloadSource source,
        IReadOnlyList<DemTile> tiles,
        string saveDirectory,
        PauseToken? pause,
        IProgress<(int Done, int Total, string Current, long Bytes)>? progress,
        Action<string>? onLog,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(saveDirectory);

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var results = new List<TileResult>(tiles.Count);
        long totalBytes = 0;
        var index = 0;

        foreach (var tile in tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            pause?.WaitIfPaused(cancellation);

            var destination = Path.Combine(saveDirectory, tile.Name + ".tif");
            var url = source.Expand(tile);
            progress?.Report((index, tiles.Count, tile.Name, totalBytes));

            var result = await FetchOneAsync(http, url, destination, tile, pause, cancellation)
                .ConfigureAwait(false);
            results.Add(result);
            totalBytes += result.Bytes;

            if (result.Outcome == TileOutcome.Failed)
                onLog?.Invoke($"[失败] {tile.Name}：{result.Error}");
            else if (result.Outcome == TileOutcome.NotFound)
                onLog?.Invoke($"[缺失] {tile.Name}：远端没有这一幅");

            index++;
        }

        progress?.Report((index, tiles.Count, "", totalBytes));

        var downloaded = results.Count(r => r.Outcome == TileOutcome.Downloaded);
        var skipped = results.Count(r => r.Outcome == TileOutcome.Skipped);
        var missing = results.Count(r => r.Outcome == TileOutcome.NotFound);
        var failed = results.Count(r => r.Outcome == TileOutcome.Failed);

        var summary = $"新下载 {downloaded} / 已存在跳过 {skipped} / 远端缺失 {missing} / 失败 {failed}";
        if (failed > 0)
            summary += "。失败项可用同一任务重跑（会续传，不重下已完成的）。";

        return new DownloadOutcome(results, totalBytes, summary);
    }

    private static async Task<TileResult> FetchOneAsync(
        HttpClient http, string url, string destination, DemTile tile,
        PauseToken? pause, CancellationToken cancellation)
    {
        try
        {
            var remoteLength = await HeadLengthAsync(http, url, cancellation).ConfigureAwait(false);
            if (remoteLength is null)
                return new TileResult(tile, TileOutcome.NotFound, 0, null);

            var partPath = destination + ".part";
            var alreadyHave = File.Exists(destination) ? new FileInfo(destination).Length : -1;
            if (alreadyHave == remoteLength)
                return new TileResult(tile, TileOutcome.Skipped, 0, null);

            // 已经下了一半的话从断点续，别丢掉那边的字节。
            var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (resumeFrom > remoteLength) resumeFrom = 0;   // .part 比远端还大说明它坏了

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var written = await DownloadToPartAsync(
                        http, url, partPath, resumeFrom, pause, cancellation).ConfigureAwait(false);
                    var finalLength = new FileInfo(partPath).Length;
                    if (finalLength != remoteLength)
                        throw new IOException($"大小不符：本地 {finalLength} / 远端 {remoteLength}");

                    File.Move(partPath, destination, overwrite: true);
                    return new TileResult(tile, TileOutcome.Downloaded, written, null);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    // 瞬时错误（TLS 重置、超时）重试有用；下一轮会重新取 .part 的长度续传。
                    resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellation).ConfigureAwait(false);
                    _ = ex;
                }
            }

            return new TileResult(tile, TileOutcome.Failed, 0, "重试若干次后仍失败");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TileResult(tile, TileOutcome.Failed, 0, ex.Message);
        }
    }

    /// <summary>取远端长度。返回 null 表示这一幅不存在（404）。</summary>
    private static async Task<long?> HeadLengthAsync(
        HttpClient http, string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            // 有些对象存储不支持 HEAD。退回用 Range 取 1 字节，从 Content-Range 里读总长。
            using var probe = new HttpRequestMessage(HttpMethod.Get, url);
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeResponse = await http.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (probeResponse.StatusCode == HttpStatusCode.NotFound) return null;
            if (!probeResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"HEAD 返回 {(int)response.StatusCode} {response.ReasonPhrase}");
            return probeResponse.Content.Headers.ContentRange?.Length
                   ?? probeResponse.Content.Headers.ContentLength;
        }

        return response.Content.Headers.ContentLength;
    }

    /// <summary>
    /// 下载到 .part 文件。<paramref name="resumeFrom"/> 之后的字节才需要取。
    /// 返回本次写入的字节数。
    /// </summary>
    private static async Task<long> DownloadToPartAsync(
        HttpClient http, string url, string partPath, long resumeFrom,
        PauseToken? pause, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // 服务器不支持 Range 时会返回 200 并从头开始——那就得丢弃 .part 重写，
        // 否则会把整份数据追加到半个文件后面。
        var appending = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var mode = appending ? FileMode.Append : FileMode.Create;

        await using var file = new FileStream(
            partPath, mode, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);

        var buffer = new byte[ChunkBytes];
        long writtenThisRun = 0;

        while (true)
        {
            // 暂停点就在块与块之间：这是"运行中暂停"能成立的全部原因。
            pause?.WaitIfPaused(cancellation);

            var read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read <= 0) break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            writtenThisRun += read;
        }

        return writtenThisRun;
    }
}
