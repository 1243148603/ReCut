using System.Text.Json;
using CutMap.Core.Packaging;
using CutMap.Core.Terrain;

namespace CutMap.Cli;

/// <summary>
/// M3 验收：打包与双产物（FR-03/05/13/18）。
///
/// 要验的不只是"能 zip"，而是三条互相独立的行为：
///   1. 前端产物：从瓦片目录反推 layer.json + 部署说明
///   2. 算法产物：tif + json，**不产出瓦片**
///   3. 只勾前端产物时**不产出 json**
/// </summary>
internal static class PackageCommand
{
    /// <summary>
    /// 真实体量的发布：写 layer.json + 部署说明 + 算法元数据，**不打 zip**。
    ///
    /// 与 <see cref="Run"/> 的区别：那个是小数据上的三组合验收（要打 zip 验证
    /// 开关组合），22 万瓦片打两次 zip 太慢；这个只跑一遍真实产物，用于端到端。
    /// </summary>
    public static int Publish(string tilesDir, string? tifPath)
    {
        if (!Directory.Exists(tilesDir))
        {
            Console.Error.WriteLine($"FAIL  瓦片目录不存在：{tilesDir}");
            return 1;
        }

        var ok = true;
        Console.WriteLine("打包（写 layer.json / 部署说明 / 算法元数据）");
        var result = Packager.Run(
            new PackageRequest(
                Path.Combine(tilesDir, ".."),
                TilesDirectory: tilesDir,
                AlgoTifPath: File.Exists(tifPath) ? tifPath : null,
                ZipTiles: false, ZipAlgo: false),
            onLog: line => Console.WriteLine("  " + line));

        Console.WriteLine($"  产物：{result.Summary}");
        ok &= result.LayerJsonPath is not null;
        ok &= result.DeployReadmePath is not null;

        if (result.LayerJsonPath is { } lp)
        {
            var layer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lp)).RootElement;
            Console.WriteLine($"  layer.json：maxzoom={layer.GetProperty("maxzoom").GetInt32()}，" +
                              $"bounds=[{string.Join(", ", layer.GetProperty("bounds").EnumerateArray().Select(e => e.GetDouble().ToString("F4")))}]");
        }
        ok &= File.Exists(Path.Combine(tilesDir, "README.txt"));

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  发布产物齐全" : "FAIL  发布产物不全");
        return ok ? 0 : 1;
    }

    public static int Run(string repoRoot, string? tilesDir, string? tifPath, string? outDir)
    {
        tilesDir ??= Path.Combine(repoRoot, "cutmap", "_gate", "slice_smoke");
        tifPath ??= Path.Combine(repoRoot, "cutmap", "_gate", "warp", "chongqing_dem.tif");
        outDir ??= Path.Combine(repoRoot, "cutmap", "_gate", "package");

        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        var ok = true;

        // ---------- 一、前端产物：从瓦片目录反推 ----------
        Console.WriteLine("一、只勾前端产物（独立打包，不重跑切片）");
        if (!Directory.Exists(tilesDir))
        {
            Console.Error.WriteLine($"FAIL  瓦片目录不存在：{tilesDir}（先跑 slice 命令）");
            return 1;
        }

        var frontOnly = Path.Combine(outDir, "front_only");
        var r1 = Packager.Run(
            new PackageRequest(frontOnly, TilesDirectory: tilesDir, ZipTiles: true),
            onLog: line => Console.WriteLine("  " + line));

        Console.WriteLine($"  产物：{r1.Summary}");
        ok &= Check("layer.json 已产出", r1.LayerJsonPath is not null);
        ok &= Check("部署说明已产出", r1.DeployReadmePath is not null);
        ok &= Check("瓦片 zip 已产出", r1.TilesZipPath is not null);
        ok &= Check("未产出算法 json", r1.AlgoZipPath is null);

        if (r1.LayerJsonPath is { } lp)
        {
            var layer = JsonDocument.Parse(File.ReadAllText(lp)).RootElement;
            ok &= Check("format 是 heightmap-1.0",
                        layer.GetProperty("format").GetString() ?? "", "heightmap-1.0");
            ok &= Check("scheme 是 tms", layer.GetProperty("scheme").GetString() ?? "", "tms");
            ok &= Check("扩展名小写 watermask",
                        layer.GetProperty("extensions")[0].GetString() ?? "", "watermask");
            var bounds = layer.GetProperty("bounds").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var maxzoom = layer.GetProperty("maxzoom").GetInt32();
            Console.WriteLine($"  layer.json：maxzoom={maxzoom}，bounds=[{string.Join(", ", bounds.Select(v => v.ToString("F4")))}]");

            // bounds 必须来自瓦片目录反推，而不是凭空写的默认值。
            var inferred = LayerJson.InspectTiles(tilesDir);
            ok &= Check("maxzoom 与目录一致",
                        maxzoom.ToString(), inferred?.DeepestZoom.ToString() ?? "?");
            ok &= Check("bounds 与瓦片包络一致",
                        bounds.Length == 4 &&
                        Math.Abs(bounds[0] - inferred!.Bounds.West) < 1e-9 &&
                        Math.Abs(bounds[3] - inferred.Bounds.North) < 1e-9);
        }

        // ---------- 二、只勾算法产物 ----------
        Console.WriteLine();
        Console.WriteLine("二、只勾算法产物（不产出瓦片、不碰切片）");
        if (!File.Exists(tifPath))
        {
            Console.Error.WriteLine($"  跳过：找不到 {tifPath}（先跑 warp 命令）");
        }
        else
        {
            var jsonPath = Path.ChangeExtension(tifPath, ".json");
            if (File.Exists(jsonPath)) File.Delete(jsonPath); // 验证它会自己产出

            var algoOnly = Path.Combine(outDir, "algo_only");
            var r2 = Packager.Run(
                new PackageRequest(algoOnly, AlgoTifPath: tifPath, ZipAlgo: true),
                onLog: line => Console.WriteLine("  " + line));

            Console.WriteLine($"  产物：{r2.Summary}");
            ok &= Check("未产出 layer.json", r2.LayerJsonPath is null);
            ok &= Check("未产出瓦片 zip", r2.TilesZipPath is null);
            ok &= Check("算法 zip 已产出", r2.AlgoZipPath is not null);
            ok &= Check("元数据 json 已自动补出", File.Exists(jsonPath));
        }

        // ---------- 三、两套同时 ----------
        Console.WriteLine();
        Console.WriteLine("三、两套产物同时，各自独立 zip");
        var both = Path.Combine(outDir, "both");
        var r3 = Packager.Run(
            new PackageRequest(both, TilesDirectory: tilesDir,
                               AlgoTifPath: File.Exists(tifPath) ? tifPath : null,
                               ZipTiles: true, ZipAlgo: true),
            onLog: line => Console.WriteLine("  " + line));
        Console.WriteLine($"  产物：{r3.Summary}");
        ok &= Check("瓦片 zip", r3.TilesZipPath is not null);
        ok &= Check("算法 zip", r3.AlgoZipPath is not null);
        if (r3.TilesZipPath is { } tz) ok &= Check("瓦片 zip 非空", new FileInfo(tz).Length > 0);
        if (r3.AlgoZipPath is { } az) ok &= Check("算法 zip 非空", new FileInfo(az).Length > 0);

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M3：打包与双产物" : "FAIL  M3");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass, string? expected = null)
    {
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}" + (expected is null ? "" : $"（期望 {expected}）"));
        return pass;
    }

    private static bool Check(string label, string actual, string expected) =>
        Check($"{label}: {actual}", actual == expected, expected);
}
