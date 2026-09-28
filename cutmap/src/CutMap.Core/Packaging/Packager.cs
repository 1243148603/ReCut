using System.IO.Compression;
using CutMap.Core.Terrain;

namespace CutMap.Core.Packaging;

/// <summary>
/// 一次运行要哪几套产物、各自要不要 zip（PRD FR-05 / FR-18）。
/// 两个开关互相独立：只跑算法产物时切片可以根本不跑；只跑前端产物时不写 json。
/// </summary>
public sealed record PackageRequest(
    string OutputDirectory,
    /// <summary>前端产物：瓦片目录。null 表示本次不要前端产物。</summary>
    string? TilesDirectory = null,
    /// <summary>算法产物：裁剪后的 GeoTIFF。null 表示本次不要算法产物。</summary>
    string? AlgoTifPath = null,
    bool ZipTiles = false,
    bool ZipAlgo = false,
    /// <summary>前端产物的 layer.json。null 时从瓦片目录反推（FR-03 的独立打包）。</summary>
    LayerJson? Layer = null);

public sealed record PackageResult(
    string? LayerJsonPath,
    string? DeployReadmePath,
    string? TilesZipPath,
    string? AlgoZipPath,
    long TileCount,
    string Summary);

/// <summary>
/// 打包（FR-05）。"打包"是**独立步骤**：用户指着一个已有的瓦片目录也能单独跑，
/// 不必重跑切片——所以这里的输入是路径，不是上游任务的返回值。
/// </summary>
public static class Packager
{
    public static PackageResult Run(
        PackageRequest request,
        Action<double>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken cancellation = default)
    {
        if (request.TilesDirectory is null && request.AlgoTifPath is null)
            throw new InvalidOperationException("至少要有一套产物：前端瓦片或算法数据。");

        Directory.CreateDirectory(request.OutputDirectory);

        string? layerPath = null;
        string? readmePath = null;
        long tileCount = 0;

        // ---------- 前端产物 ----------
        if (request.TilesDirectory is { } tilesDir)
        {
            if (!Directory.Exists(tilesDir))
                throw new DirectoryNotFoundException($"瓦片目录不存在：{tilesDir}");

            var coverage = LayerJson.InspectTiles(tilesDir)
                ?? throw new InvalidOperationException($"目录里没有 .terrain 瓦片：{tilesDir}");
            tileCount = coverage.TileCount;
            onLog?.Invoke($"瓦片：{coverage.MinZoom}–{coverage.DeepestZoom} 级，共 {tileCount:N0} 个");

            // layer.json 由"打包"任务产出，不是切片的副产物（NFR-06）。
            var layer = request.Layer ?? LayerJson.FromExistingTiles(
                tilesDir, Path.GetFileName(tilesDir.TrimEnd(Path.DirectorySeparatorChar)),
                "Cesium terrain tiles", "1.0.0");
            layerPath = layer.WriteTo(tilesDir);
            onLog?.Invoke($"已写 {layerPath}");

            readmePath = Path.Combine(tilesDir, "README.txt");
            File.WriteAllText(readmePath, DeployReadme.Build(layer.Name, layer.MaxZoom, tileCount),
                              new System.Text.UTF8Encoding(false));
            onLog?.Invoke($"已写 {readmePath}");
        }

        cancellation.ThrowIfCancellationRequested();
        onProgress?.Invoke(0.1);

        // ---------- 算法产物 ----------
        string? algoJson = null;
        if (request.AlgoTifPath is { } tif)
        {
            if (!File.Exists(tif))
                throw new FileNotFoundException("裁剪 GeoTIFF 不存在", tif);

            // 元数据 json 与 tif 同名同目录（FR-17）。已经有就不重写——
            // 重写会把"这份数据什么时候、用哪些源产出的"覆盖成"打包的时间"。
            algoJson = Path.ChangeExtension(tif, ".json");
            if (!File.Exists(algoJson))
            {
                AlgoMetadata.Measure(tif, []).WriteBeside(tif);
                onLog?.Invoke($"已写 {algoJson}");
            }
            else
            {
                onLog?.Invoke($"算法元数据已存在，保留原样：{algoJson}");
            }
        }

        onProgress?.Invoke(0.2);

        // ---------- zip ----------
        string? tilesZip = null;
        if (request.ZipTiles && request.TilesDirectory is { } tz)
        {
            tilesZip = Path.Combine(request.OutputDirectory, ZipName(tz, "tiles"));
            ZipDirectory(tz, tilesZip, onLog, cancellation,
                         progress: p => onProgress?.Invoke(0.2 + p * 0.6));
        }

        string? algoZip = null;
        if (request.ZipAlgo && request.AlgoTifPath is { } az)
        {
            algoZip = Path.Combine(request.OutputDirectory, ZipName(az, "algo"));
            var files = new List<string> { az };
            if (algoJson is not null) files.Add(algoJson);
            ZipFiles(Path.GetDirectoryName(az)!, files, algoZip, onLog, cancellation,
                     progress: p => onProgress?.Invoke(0.8 + p * 0.2));
        }

        onProgress?.Invoke(1.0);

        var parts = new List<string>();
        if (request.TilesDirectory is not null) parts.Add($"前端瓦片 {tileCount:N0} 个");
        if (request.AlgoTifPath is not null) parts.Add("算法数据");
        if (tilesZip is not null) parts.Add("瓦片 zip");
        if (algoZip is not null) parts.Add("算法 zip");

        return new PackageResult(layerPath, readmePath, tilesZip, algoZip, tileCount,
                                 string.Join("，", parts));
    }

    private static string ZipName(string source, string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(source.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "cutmap";
        return $"{name}_{suffix}.zip";
    }

    private static void ZipDirectory(
        string directory, string zipPath, Action<string>? onLog,
        CancellationToken cancellation, Action<double>? progress)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        var root = directory.TrimEnd(Path.DirectorySeparatorChar);
        ZipFiles(root, files, zipPath, onLog, cancellation, progress,
                 entryName: full => Path.GetRelativePath(root, full));
    }

    /// <param name="entryName">给出则在 zip 里用这个相对名（保留目录结构）；不给则只存文件名。</param>
    private static void ZipFiles(
        string baseDirectory, IReadOnlyList<string> files, string zipPath,
        Action<string>? onLog, CancellationToken cancellation, Action<double>? progress,
        Func<string, string>? entryName = null)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);

        onLog?.Invoke($"打包 {files.Count:N0} 个文件 → {Path.GetFileName(zipPath)}");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        for (var i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();

            var path = files[i];
            var name = entryName?.Invoke(path) ?? Path.GetFileName(path);
            // 用 '\' 会让解压出来的层级取决于解压工具；zip 规范里目录分隔符就是 '/'。
            name = name.Replace('\\', '/');

            // **不压缩**：.terrain 本身就是 gzip 流、GeoTIFF 是 LZW，再 deflate 一遍
            // 只能省千分之几，却要多花几分钟 CPU（22 万个文件）。存储即可。
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var source = File.OpenRead(path);
            using var target = entry.Open();
            source.CopyTo(target);

            if (i % 5000 == 0) progress?.Invoke((double)i / files.Count);
        }
        progress?.Invoke(1.0);
        onLog?.Invoke($"完成：{zipPath}（{new FileInfo(zipPath).Length / (1024.0 * 1024):F0} MB）");
    }
}
