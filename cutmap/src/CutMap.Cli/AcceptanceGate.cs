using System.Net;
using System.Net.Sockets;
using System.Text;
using CutMap.Core.Download;
using CutMap.Core.Geo;
using CutMap.Core.Packaging;
using CutMap.Core.Store;
using OSGeo.GDAL;

namespace CutMap.Cli;

internal static class AcceptanceGate
{
    public static int Run(string repo)
    {
        var work = Path.Combine(repo, "cutmap", "_gate", "acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var failed = 0;
        void Check(string name, bool pass)
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}");
            if (!pass) failed++;
        }
        RasterInfo Raster(double west, double south, double east, double north) => new(
            "fixture.tif", 1000, 1000,
            [west, (east - west) / 1000, 0, north, 0, -(north - south) / 1000],
            "", DataType.GDT_Float32, null, 0);

        var target = new BBox(105, 28, 108, 31);
        var full = (from x in Enumerable.Range(105, 3)
                    from y in Enumerable.Range(28, 3) select Raster(x, y, x + 1, y + 1)).ToArray();
        Check("完整九幅允许提交", Coverage.Check(full, target).Covered);
        Check("中间缺一幅拒绝提交", !Coverage.Check(full.Where((_, i) => i != 4).ToArray(), target).Covered);
        Check("仅两角不能冒充全域覆盖", !Coverage.Check([full[0], full[^1]], target).Covered);
        Check("外沿缺幅拒绝提交", !Coverage.Check([full[0]], target).Covered);
        Check("相邻分幅接缝允许提交", Coverage.Check([full[0], full[3]], new BBox(105, 28, 107, 29)).Covered);

        var config = Path.Combine(work, "sources.json");
        var store = new SourceStore(config);
        var official = DownloadSource.CopernicusOfficial;
        store.Save(official with { Enabled = false, UrlTemplate = "https://old.invalid/{name}" });
        store.RecordOutcome(official.Id, false, "测试失败原因");
        var custom = official with { Id = "test-custom", IsBuiltIn = false, Name = "测试镜像" };
        store.Save(custom);
        var reload = new SourceStore(config);
        Check("内置源禁用与失败原因重启保留", reload.ById(official.Id) is { Enabled: false, LastError: "测试失败原因" });
        Check("内置模板升级以代码为准", reload.ById(official.Id)?.UrlTemplate == official.UrlTemplate);
        Check("内置源不能删除", !reload.Delete(official.Id));
        Check("自定义源重启保留", reload.ById(custom.Id)?.Name == custom.Name);

        // 只监听 loopback，以可控 HTTP 响应验续传，不使用公网或真实 DEM。
        using var server = new FixtureServer();
        var tile = new DemTile(29, 106, "N29", "E106", new BBox(106, 29, 107, 30));
        DownloadOutcome Fetch(string mode, string directory) => Downloader.RunAsync(
            custom with { UrlTemplate = server.Url + mode + "/{name}" }, [tile], directory,
            null, null, null, CancellationToken.None, 1).GetAwaiter().GetResult();
        var complete = Path.Combine(work, "complete");
        Directory.CreateDirectory(complete);
        File.WriteAllBytes(Path.Combine(complete, tile.Name + ".tif.part"), FixtureServer.Payload);
        Check("已完整 part 直接转正", Fetch("normal", complete).Succeeded &&
            File.ReadAllBytes(Path.Combine(complete, tile.Name + ".tif")).SequenceEqual(FixtureServer.Payload));
        var resumed = Path.Combine(work, "resume");
        Directory.CreateDirectory(resumed);
        File.WriteAllBytes(Path.Combine(resumed, tile.Name + ".tif.part"), FixtureServer.Payload[..3]);
        var result = Fetch("normal", resumed);
        Check("Range 续传仅下载剩余字节", result.Succeeded && result.BytesDownloaded == FixtureServer.Payload.Length - 3 &&
            File.ReadAllBytes(Path.Combine(resumed, tile.Name + ".tif")).SequenceEqual(FixtureServer.Payload));
        Check("HEAD 无长度时用 Range 探测", Fetch("probe", Path.Combine(work, "probe")).Succeeded);
        Check("无总长度如实失败，不能当 404", !Fetch("unknown", Path.Combine(work, "unknown")).Succeeded);
        Check("只有真实 404 才报远端缺失", Fetch("missing", Path.Combine(work, "missing")).Results[0].Outcome == TileOutcome.NotFound);
        var badRange = Path.Combine(work, "bad-range");
        Directory.CreateDirectory(badRange);
        File.WriteAllBytes(Path.Combine(badRange, tile.Name + ".tif.part"), FixtureServer.Payload[..3]);
        Check("错误 Range 起点拒绝写入", !Fetch("bad-range", badRange).Succeeded &&
            new FileInfo(Path.Combine(badRange, tile.Name + ".tif.part")).Length == 3);

        // 让旧 zip 先存在，在打包开始后取消，旧交付物必须仍然可用。
        var tiles = Path.Combine(work, "tiles");
        Directory.CreateDirectory(Path.Combine(tiles, "0", "0"));
        File.WriteAllBytes(Path.Combine(tiles, "0", "0", "0.terrain"), [1]);
        var zipPath = Path.Combine(work, "tiles_tiles.zip");
        byte[] oldZip = [80, 75, 3, 4, 99];
        File.WriteAllBytes(zipPath, oldZip);
        using var cancel = new CancellationTokenSource();
        try
        {
            Packager.Run(new PackageRequest(work, tiles, ZipTiles: true),
                onLog: line => { if (line.StartsWith("打包 ", StringComparison.Ordinal)) cancel.Cancel(); },
                cancellation: cancel.Token);
            Check("取消打包抛取消异常", false);
        }
        catch (OperationCanceledException) { Check("取消打包及时返回", true); }
        Check("取消打包保留旧 zip", File.ReadAllBytes(zipPath).SequenceEqual(oldZip));
        Check("取消打包清理临时文件", !File.Exists(zipPath + ".tmp"));

        var taskPath = Path.Combine(work, "task.log");
        using (var taskLog = new TaskLog(taskPath))
            Parallel.For(0, 20000, i => taskLog.Write($"line-{i}"));
        Check("并发日志先写磁盘，20000 条完整保留", File.ReadAllLines(taskPath).Length == 20000);
        var historyPath = Path.Combine(work, "history.json");
        new HistoryStore(historyPath).Add(new HistoryEntry { Id = "log-test", Type = "测试", StartedAt = DateTimeOffset.Now,
            Status = "成功", LogPath = taskPath });
        Check("历史重载后仍可定位独立任务日志", new HistoryStore(historyPath).All[0].LogPath == taskPath);
        var history = new HistoryStore(historyPath);
        history.Add(new HistoryEntry { Id = "delete-test", Type = "测试", StartedAt = DateTimeOffset.Now,
            Status = "失败", ParametersJson = "{\"max_zoom\":14}" });
        Check("历史单条删除重启后生效", history.Delete("delete-test") &&
            new HistoryStore(historyPath).All.All(e => e.Id != "delete-test"));
        history.Clear();
        Check("历史清空重启后生效", new HistoryStore(historyPath).All.Count == 0);

        var presetPath = Path.Combine(work, "presets.json");
        var presets = new PresetStore(presetPath);
        var taskPreset = PresetStore.ChongqingTask with
        {
            Id = "acceptance-task", Name = "验收任务", Description = "首次保存", IsBuiltIn = false,
            MaxZoom = 12, ZipTiles = true,
        };
        presets.SaveTask(taskPreset);
        presets.SaveTask(taskPreset with { Description = "修改后", MaxZoom = 14 });
        var clipPreset = new ClipPreset
        {
            Id = "acceptance-clip", Name = "验收范围", Description = "持久化范围",
            Bounds = [106, 29, 107, 30], IsBuiltIn = false,
        };
        presets.SaveClip(clipPreset);
        var reloadedPresets = new PresetStore(presetPath);
        Check("自定义任务预设增改后重启保留", reloadedPresets.TaskById(taskPreset.Id) is
            { Description: "修改后", MaxZoom: 14, ZipTiles: true });
        Check("自定义范围预设重启保留", reloadedPresets.ClipPresets.Any(p =>
            p.Id == clipPreset.Id && p.Bounds.SequenceEqual(clipPreset.Bounds)));
        Check("内置预设不能删除", !reloadedPresets.DeleteTask(PresetStore.ChongqingTask.Id) &&
            !reloadedPresets.DeleteClip(PresetStore.Chongqing.Id));
        Check("自定义预设删除后重启消失", reloadedPresets.DeleteTask(taskPreset.Id) &&
            reloadedPresets.DeleteClip(clipPreset.Id) &&
            new PresetStore(presetPath).TaskById(taskPreset.Id) is null &&
            new PresetStore(presetPath).ClipPresets.All(p => p.Id != clipPreset.Id));
        var blocked = Path.Combine(work, "blocked.json");
        Directory.CreateDirectory(blocked);
        var failedStore = new SourceStore(blocked);
        try { failedStore.Save(custom); Check("配置保存失败可见", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Check("配置保存失败可见且回滚内存", failedStore.ById(custom.Id) is null); }
        using (var pause = new PauseToken())
        using (var cancelPause = new CancellationTokenSource(100))
        {
            pause.Pause();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { Task.Run(() => pause.WaitIfPaused(cancelPause.Token)).GetAwaiter().GetResult(); Check("暂停取消响应", false); }
            catch (OperationCanceledException) { Check("暂停取消立即退出", watch.Elapsed.TotalSeconds < 2); }
        }
        using (var stalled = new FixtureServer())
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timeoutResult = Downloader.RunAsync(custom with { UrlTemplate = stalled.Url + "stall/{name}" }, [tile],
                Path.Combine(work, "timeout"), null, null, null, CancellationToken.None, 1, TimeSpan.FromMilliseconds(100))
                .GetAwaiter().GetResult();
            Check("服务端不回响应时有限重试后明确超时", !timeoutResult.Succeeded && watch.Elapsed.TotalSeconds < 10 &&
                timeoutResult.Results[0].Error!.Contains("超时"));
        }
        Console.WriteLine($"证据目录：{work}");
        return failed == 0 ? 0 : 1;
    }

    private sealed class FixtureServer : IDisposable
    {
        public static readonly byte[] Payload = Encoding.ASCII.GetBytes("terrain-fixture");
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        public string Url { get; }

        public FixtureServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _serving = Serve();
        }

        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var first = await reader.ReadLineAsync() ?? "";
                    var head = first.StartsWith("HEAD", StringComparison.Ordinal);
                    var mode = first.Split(' ')[1].Split('/')[1];
                    if (mode == "stall") { await Task.Delay(Timeout.Infinite, _stop.Token); continue; }
                    string? range = null;
                    while (await reader.ReadLineAsync() is { Length: > 0 } line)
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line;
                    var offset = range is null ? 0 : int.Parse(range.Split('=')[1].Split('-')[0]);
                    var missing = mode == "missing";
                    var unknown = mode == "unknown" || (head && mode == "probe");
                    var partial = range is not null && !unknown && !missing;
                    var body = missing ? [] : partial ? Payload[offset..] : Payload;
                    var headers = $"HTTP/1.1 {(missing ? "404 Not Found" : partial ? "206 Partial Content" : "200 OK")}\r\nConnection: close\r\n";
                    if (!unknown) headers += $"Content-Length: {body.Length}\r\n";
                    if (partial) headers += $"Content-Range: bytes {(mode == "bad-range" ? 0 : offset)}-{Payload.Length - 1}/{Payload.Length}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"));
                    if (!head) await stream.WriteAsync(body);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _serving.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
