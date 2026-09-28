using System.Diagnostics;
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

    public static async Task<int> RunAsync(string repoRoot, bool network)
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

        if (!network)
        {
            Console.WriteLine();
            Console.WriteLine("（未加 --network，跳过实际下载）");
            Console.WriteLine(ok ? "PASS  M4（离线部分）" : "FAIL  M4（离线部分）");
            return ok ? 0 : 1;
        }

        // ---------- 三、实际下载：跳过与续传 ----------
        Console.WriteLine();
        Console.WriteLine("三、实际下载（只取 1 幅，约 45 MB）");
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

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M4：分幅枚举、模板展开、跳过与续传" : "FAIL  M4");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, string actual, string expected)
    {
        var pass = actual == expected;
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: {actual}" + (pass ? "" : $" / 期望 {expected}"));
        return pass;
    }

    private static bool Check(string label, bool pass) =>
        Check(label, pass ? "是" : "否", "是");
}
