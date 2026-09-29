using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CutMap.Core.Download;
using CutMap.Core.Geo;

namespace CutMap.Cli;

/// <summary>
/// M4 验收：分幅枚举、URL 模板展开、断点续传/跳过（FR-06 / FR-16）。
/// </summary>
internal static class DownloadCommand
{
    /// <summary>教程 §3.1 那条 URL 的原文，用来核对模板展开是不是同一个地址。</summary>
    private const string TutorialUrl =
        "https://copernicus-dem-30m.s3.amazonaws.com/" +
        "Copernicus_DSM_COG_10_N29_00_E106_00_DEM/Copernicus_DSM_COG_10_N29_00_E106_00_DEM.tif";

    /// <param name="limit">联网段批量下载的幅数（&gt;1 时加测一轮吞吐，测速用）。</param>
    public static async Task<int> RunAsync(string repoRoot, bool network, int limit = 1)
    {
        var ok = true;
        var chongqing = new BBox(105.3, 28.1, 110.2, 32.2);

        // ---------- 一、分幅枚举 ----------
        Console.WriteLine("一、区域 → 分幅枚举（1°×1° 网格，按真实相交）");
        var tiles = DemTileGrid.Enumerate(chongqing);
        Console.WriteLine($"  {chongqing} → {tiles.Count} 幅");
        Console.WriteLine($"  首：{tiles[0].Name}  末：{tiles[^1].Name}");
        ok &= Check("重庆全域应为 30 幅", tiles.Count.ToString(), "30");

        // 教程 §3.1 的清单漏了 N29_E107 / N29_E108，导致中间留 2°×1° 空洞且不报错。
        foreach (var missing in new[] { "N29_E107", "N29_E108" })
            ok &= Check($"{missing} 在枚举里", tiles.Any(t => t.Name == missing));

        // 整度边界不该多带一列/一行。
        var exact = DemTileGrid.Enumerate(new BBox(105.0, 28.0, 106.0, 29.0));
        ok &= Check("整度范围只出 1 幅", exact.Count.ToString(), "1");
        ok &= Check("且是 N28_E105", exact[0].Name, "N28_E105");

        // ---------- 二、URL 模板展开 ----------
        Console.WriteLine();
        Console.WriteLine("二、URL 模板展开（FR-16 的占位符口径）");
        var official = DownloadSource.CopernicusOfficial;
        Console.WriteLine($"  模板：{official.UrlTemplate}");

        var probe = new DemTile(29, 106, "N29", "E106", new BBox(106, 29, 107, 30));
        var expanded = official.Expand(probe);
        Console.WriteLine($"  展开：{expanded}");
        ok &= Check("与教程 §3.1 的 URL 逐字符相同", expanded == TutorialUrl ? "相同" : "不同", "相同");

        // 负向：教程那种 "N{lat}" 写法粘进模板会拼出 N N29。
        var naive = official with
        {
            UrlTemplate = "https://example.invalid/N{lat}_00_E{lon}_00.tif",
        };
        var naiveUrl = naive.Expand(probe);
        Console.WriteLine($"  负向：把教程的 N{{lat}} 写法粘进模板 → {naiveUrl}");
        ok &= Check("能看出拼错了（含 NN29）", naiveUrl.Contains("NN29", StringComparison.Ordinal));

        // 不带任何占位符的模板必须被拒——否则所有分幅会指向同一个地址。
        var noPlaceholder = official with { UrlTemplate = "https://example.invalid/fixed.tif" };
        ok &= Check("无占位符的模板被拒", !noPlaceholder.IsUsable(out _));

        // ---------- 三、并发（离线：本地慢速源） ----------
        Console.WriteLine();
        Console.WriteLine("三、整批并发（离线：每个请求延迟 600 ms 的本地源）");
        ok &= await ConcurrencyTestAsync(tiles);

        if (!network)
        {
            Console.WriteLine();
            Console.WriteLine("（未加 --network，跳过实际下载）");
            Console.WriteLine(ok ? "PASS  M4（离线部分）" : "FAIL  M4（离线部分）");
            return ok ? 0 : 1;
        }

        // ---------- 四、实际下载：跳过与续传 ----------
        Console.WriteLine();
        Console.WriteLine("四、实际下载（只取 1 幅，约 45 MB）");
        var saveDir = Path.Combine(repoRoot, "cutmap", "_gate", "download");
        if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);

        var one = new[] { exact[0] };
        var sw = Stopwatch.StartNew();
        var first = await Downloader.RunAsync(
            official, one, saveDir, pause: null, progress: null,
            onLog: line => Console.WriteLine("  " + line), cancellation: CancellationToken.None);
        Console.WriteLine($"  第一次：{first.Summary}（{sw.Elapsed.TotalSeconds:F0} s，" +
                          $"{first.BytesDownloaded / (1024.0 * 1024):F1} MB）");
        ok &= Check("第一次是 Downloaded",
                    first.Results[0].Outcome.ToString(), nameof(TileOutcome.Downloaded));

        var file = Path.Combine(saveDir, "N28_E105.tif");
        var fullLength = new FileInfo(file).Length;

        // 再跑一次：应当跳过，不重下。
        sw.Restart();
        var second = await Downloader.RunAsync(
            official, one, saveDir, pause: null, progress: null,
            onLog: null, cancellation: CancellationToken.None);
        Console.WriteLine($"  第二次：{second.Summary}（{sw.Elapsed.TotalSeconds:F1} s）");
        ok &= Check("第二次是 Skipped", second.Results[0].Outcome.ToString(), nameof(TileOutcome.Skipped));
        ok &= Check("未产生流量", second.BytesDownloaded.ToString(), "0");
        ok &= Check("文件大小不变", new FileInfo(file).Length.ToString(), fullLength.ToString());

        // 模拟断线：把文件砍成 1/3，放成 .part，再跑一次应当续传而非重下。
        Console.WriteLine("  模拟断线：截断到 1/3，验证续传");
        var partPath = file + ".part";
        File.Delete(file);
        using (var src = File.OpenRead(Path.Combine(repoRoot, "cutmap", "_gate", "warp", "chongqing_dem.tif")))
        using (var dst = File.Create(partPath))
        {
            var buf = new byte[fullLength / 3];
            var read = src.Read(buf, 0, buf.Length);
            dst.Write(buf, 0, read);
        }
        var truncated = new FileInfo(partPath).Length;

        sw.Restart();
        var third = await Downloader.RunAsync(
            official, one, saveDir, pause: null, progress: null,
            onLog: null, cancellation: CancellationToken.None);
        var resumedBytes = third.BytesDownloaded;
        Console.WriteLine($"  第三次：{third.Summary}，本次只取了 {resumedBytes / (1024.0 * 1024):F1} MB" +
                          $"（{sw.Elapsed.TotalSeconds:F0} s）");

        ok &= Check("续传后文件完整", new FileInfo(file).Length.ToString(), fullLength.ToString());
        // 续传的意义就在这里：本次流量应当约等于剩余部分，而不是整幅。
        var expectedRemaining = fullLength - truncated;
        var resumeOk = resumedBytes < fullLength * 0.95 &&
                       Math.Abs(resumedBytes - expectedRemaining) < fullLength * 0.05;
        ok &= Check($"只取了剩余部分（应约 {(expectedRemaining / (1024.0 * 1024)):F1} MB，" +
                    $"整幅 {(fullLength / (1024.0 * 1024)):F1} MB）", resumeOk);
        ok &= Check("没有残留 .part", !File.Exists(partPath));

        // ---------- 五、批量吞吐（可选，--limit N） ----------
        if (limit > 1)
        {
            var batch = tiles.Take(limit).ToArray();
            Console.WriteLine();
            Console.WriteLine($"五、批量下载 {batch.Length} 幅（测吞吐）");
            var batchDir = Path.Combine(repoRoot, "cutmap", "_gate", "download_batch");
            if (Directory.Exists(batchDir)) Directory.Delete(batchDir, true);

            sw.Restart();
            var batchOutcome = await Downloader.RunAsync(
                official, batch, batchDir, pause: null, progress: null,
                onLog: line => Console.WriteLine("  " + line), cancellation: CancellationToken.None);
            sw.Stop();

            var mb = batchOutcome.BytesDownloaded / (1024.0 * 1024);
            var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Console.WriteLine($"  {batchOutcome.Summary}；{mb:F1} MB / {sw.Elapsed.TotalSeconds:F1} s " +
                              $"= {mb / seconds:F1} MB/s");
            ok &= Check("批量下载无失败", batchOutcome.Succeeded);
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M4：分幅枚举、模板展开、跳过与续传" : "FAIL  M4");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// FR-16 / PRD 验收 #8：新增一条 URL 模板与官方不同的源，用它下载要能成功。
    ///
    /// 用本地 HTTP 服务造一个"命名规则完全不同"的源（`{code}.tif` 而不是官方那种
    /// 又长又绕的路径），这样验的是**模板机制本身**，不依赖任何外部服务。
    /// 再顺手验"源不可达时如实报错、不静默换源"。
    /// </summary>
    public static async Task<int> CustomSourceAsync(string baseUrl)
    {
        var ok = true;
        var saveDir = Path.Combine(Path.GetTempPath(), "cutmap-src-test");
        if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);

        Console.WriteLine("自定义下载源（命名规则与官方完全不同）");
        var custom = new DownloadSource
        {
            Id = "local-test",
            Name = "本地测试源",
            // 官方那种是 Copernicus_DSM_COG_10_N29_00_E106_00_DEM/...，这里是 {code}.tif
            UrlTemplate = baseUrl.TrimEnd('/') + "/{code}.tif",
            Description = "命名规则与官方完全不同，用于验证模板机制",
        };

        // {code} 不是内置占位符——Expand 只认 {name}/{lat}/{lon}，
        // 所以这里改用 {name}，服务端也就按 N29_E106.tif 这个文件名放。
        custom = custom with { UrlTemplate = baseUrl.TrimEnd('/') + "/{name}.tif" };

        var tile = new DemTile(29, 106, "N29", "E106", new BBox(106, 29, 107, 30));
        Console.WriteLine($"  模板：{custom.UrlTemplate}");
        Console.WriteLine($"  展开：{custom.Expand(tile)}");

        var outcome = await Downloader.RunAsync(
            custom, [tile], saveDir, pause: null, progress: null,
            onLog: line => Console.WriteLine("  " + line), cancellation: CancellationToken.None);

        var file = Path.Combine(saveDir, "N29_E106.tif");
        ok &= Check("自定义源下载成功", outcome.Results[0].Outcome.ToString(), "Downloaded");
        ok &= Check("文件确实落地", File.Exists(file).ToString(), "True");
        if (File.Exists(file))
            Console.WriteLine($"  落盘 {new FileInfo(file).Length} 字节（服务端文件是 1024 字节）");

        // ---- 源不可达：如实报错，不静默换源 ----
        Console.WriteLine();
        Console.WriteLine("源不可达时应如实报错（PRD：不静默换源）");
        var dead = custom with { UrlTemplate = "http://127.0.0.1:1/{name}.tif" };
        var deadOutcome = await Downloader.RunAsync(
            dead, [tile], Path.Combine(saveDir, "dead"), pause: null, progress: null,
            onLog: null, cancellation: CancellationToken.None);
        Console.WriteLine($"  结果：{deadOutcome.Results[0].Outcome}，说明：{deadOutcome.Results[0].Error}");
        ok &= Check("不可达被判定为失败（不是静默成功）",
                    deadOutcome.Results[0].Outcome.ToString(), "Failed");
        ok &= Check("没有产出文件",
                    File.Exists(Path.Combine(saveDir, "dead", "N29_E106.tif")).ToString(), "False");

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  自定义下载源" : "FAIL  自定义下载源");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 验证"整批并发"真的在并发：本地起一个每个请求延迟 600 ms 的迷你 HTTP 源，
    /// 看源端观测到的并发连接峰值。峰值 ≥ 3 即证明 4 幅确实同时在跑——比
    /// "耗时比串行短"这种时间断言更抗机器负载（这台机器同时在干别的也成立）。
    /// </summary>
    private static async Task<bool> ConcurrencyTestAsync(IReadOnlyList<DemTile> tiles)
    {
        var ok = true;
        using var slow = new SlowHttpSource(delayMs: 600);
        var local = new DownloadSource
        {
            Id = "slow-local",
            Name = "本地慢速源",
            UrlTemplate = $"http://127.0.0.1:{slow.Port}/{{name}}.tif",
            Description = "每个请求延迟 600 ms，用于验证整批并发",
        };

        var batch = tiles.Take(4).ToArray();
        var dir = Path.Combine(Path.GetTempPath(), "cutmap-concurrency");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var sw = Stopwatch.StartNew();
        var outcome = await Downloader.RunAsync(
            local, batch, dir, pause: null, progress: null,
            onLog: null, cancellation: CancellationToken.None);
        sw.Stop();

        // 每幅要 HEAD+GET 两个请求，串行的下界 = 4 幅 × 2 × 0.6 s = 4.8 s。
        Console.WriteLine($"  {batch.Length} 幅用时 {sw.Elapsed.TotalSeconds:F1} s" +
                          $"（串行下界约 {batch.Length * 2 * 0.6:F1} s），" +
                          $"源端并发峰值 {slow.MaxConcurrent}");
        ok &= Check("全部下载成功", outcome.Succeeded);
        ok &= Check($"并发峰值 ≥ 3（实测 {slow.MaxConcurrent}）", slow.MaxConcurrent >= 3);
        return ok;
    }

    private static bool Check(string label, string actual, string expected)
    {
        var pass = actual == expected;
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: {actual}" + (pass ? "" : $" / 期望 {expected}"));
        return pass;
    }

    private static bool Check(string label, bool pass) =>
        Check(label, pass ? "是" : "否", "是");

    /// <summary>
    /// 每个请求延迟若干毫秒的迷你 HTTP 源。用裸 <see cref="TcpListener"/> 而不是
    /// HttpListener：后者走 HTTP.sys，非管理员绑定前缀会失败；裸 socket 没有这层。
    /// 记录同时打开的连接数峰值，供并发断言使用。
    /// </summary>
    private sealed class SlowHttpSource : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly int _delayMs;
        private int _active;
        private int _maxActive;

        public SlowHttpSource(int delayMs)
        {
            _delayMs = delayMs;
            _listener.Start();
            _ = AcceptLoopAsync();   // 后台接受连接；Dispose 时由 _cts 收尾
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int MaxConcurrent => Volatile.Read(ref _maxActive);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;   // Dispose 停掉监听；这里没有别的退出路径
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var now = Interlocked.Increment(ref _active);
                int prev;
                do { prev = Volatile.Read(ref _maxActive); }
                while (now > prev && Interlocked.CompareExchange(ref _maxActive, now, prev) != prev);
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync();
                    if (requestLine is null) return;
                    var method = requestLine.Split(' ')[0];
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

                    await Task.Delay(_delayMs);   // 模拟慢源：延迟发生在"能开始传数据"之前

                    var body = new byte[1024];
                    Random.Shared.NextBytes(body);
                    var head =
                        $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n" +
                        "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    if (method == "GET") await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    // 客户端中途断开（例如取消）不该把服务器打挂
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
