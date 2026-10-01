using System.IO.Compression;
using System.Threading.Channels;
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
    LayerJson? Layer = null,
    /// <summary>调用方刚扫过的瓦片统计。给了就不重复遍历目录（22 万瓦片一次扫描约数秒）。</summary>
    TileCoverage? Coverage = null,
    /// <summary>
    /// 随前端产物分发的边界矢量（FR-15，矢量裁剪产物旁的 *.boundary.geojson）。
    /// 给了就拷进瓦片目录——它随后自然进 zip。**显式传参，不做路径猜测**：
    /// 打包是独立步骤，指着任意瓦片目录也能跑，猜出来的路径十有八九是错的。
    /// </summary>
    string? BoundaryGeoJsonPath = null);

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

            var coverage = request.Coverage ?? LayerJson.InspectTiles(tilesDir)
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

            // FR-15：边界矢量随产物分发——拷进瓦片目录，它才会进 zip、
            // 才会跟着瓦片一起交到前端手里。
            if (request.BoundaryGeoJsonPath is { } boundaryPath)
            {
                if (File.Exists(boundaryPath))
                {
                    // **统一落成 boundary.geojson**：前端才能确定性地从
                    // <瓦片目录>/boundary.geojson 取到它，不必去猜裁剪文件名叫什么。
                    var boundaryTarget = Path.Combine(tilesDir, "boundary.geojson");
                    File.Copy(boundaryPath, boundaryTarget, overwrite: true);
                    onLog?.Invoke($"已随产物分发边界矢量：boundary.geojson" +
                                  $"（源：{Path.GetFileName(boundaryPath)}）");
                }
                else
                {
                    onLog?.Invoke($"边界矢量不存在，跳过：{boundaryPath}");
                }
            }
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
            ZipFiles(files, algoZip, onLog, cancellation,
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

    /// <summary>读文件的并发度。单个小文件的 open+读把墙钟时间花在系统调用与
    /// 杀软实时扫描上，并发读能把这些等待叠起来；写仍是单线程（ZipArchive 非线程安全）。</summary>
    private static int ReadConcurrency => Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>不超过这个大小的文件整个读进内存再写；更大的（如整幅 tif）流式拷贝，不进内存。</summary>
    private const long InlineThreshold = 4 * 1024 * 1024;

    private readonly record struct ZipItem(string Name, string Path, byte[]? Bytes);

    private static void ZipDirectory(
        string directory, string zipPath, Action<string>? onLog,
        CancellationToken cancellation, Action<double>? progress)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        var root = directory.TrimEnd(Path.DirectorySeparatorChar);
        ZipFiles(files, zipPath, onLog, cancellation, progress,
                 entryName: full => Path.GetRelativePath(root, full));
    }

    /// <param name="entryName">给出则在 zip 里用这个相对名（保留目录结构）；不给则只存文件名。</param>
    private static void ZipFiles(
        IReadOnlyList<string> files, string zipPath,
        Action<string>? onLog, CancellationToken cancellation, Action<double>? progress,
        Func<string, string>? entryName = null)
    {
        // **先写 .tmp、成功了再改名。** 上次一个 22 万瓦片的打包跑到 7 分半被取消，
        // 目标路径上留了半个 zip——从文件名看不出它是半成品，这种"看起来能交付"
        // 的残留不能留（.part 的同一思路，下载那边已经这么做了）。
        var tmpPath = zipPath + ".tmp";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        if (File.Exists(tmpPath)) File.Delete(tmpPath);

        onLog?.Invoke($"打包 {files.Count:N0} 个文件 → {Path.GetFileName(zipPath)}");
        try
        {
            using (var zip = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                WriteEntries(zip, files, entryName, cancellation, progress);
            }
            File.Move(tmpPath, zipPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }

        onLog?.Invoke($"完成：{zipPath}（{new FileInfo(zipPath).Length / (1024.0 * 1024):F0} MB）");
    }

    /// <summary>
    /// 读并发、写单线程的生产者/消费者：读者把文件读进有界队列，写者按顺序塞进 zip。
    ///
    /// 队列满时读者自然减速（<see cref="BoundedChannelFullMode.Wait"/>），内存不会随
    /// 目录规模涨；读侧失败通过 channel 交给写侧抛出，不会被吞在后台任务里。
    /// </summary>
    private static void WriteEntries(
        ZipArchive zip, IReadOnlyList<string> files, Func<string, string>? entryName,
        CancellationToken cancellation, Action<double>? progress)
    {
        var concurrency = ReadConcurrency;
        var channel = Channel.CreateBounded<ZipItem>(new BoundedChannelOptions(concurrency * 4)
        {
            SingleReader = true,   // 只有本线程写 zip
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producing = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, files.Count),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = concurrency,
                        CancellationToken = cancellation,
                    },
                    async (i, ct) =>
                    {
                        var path = files[i];
                        // 用 '\' 会让解压出来的层级取决于解压工具；zip 规范里分隔符就是 '/'。
                        var name = (entryName?.Invoke(path) ?? Path.GetFileName(path)).Replace('\\', '/');
                        byte[]? bytes = null;
                        if (new FileInfo(path).Length <= InlineThreshold)
                            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                        await channel.Writer.WriteAsync(new ZipItem(name, path, bytes), ct)
                                     .ConfigureAwait(false);
                    }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });

        try
        {
            var written = 0;
            foreach (var item in channel.Reader.ReadAllAsync(cancellation).ToBlockingEnumerable())
            {
                cancellation.ThrowIfCancellationRequested();

                // **不压缩**：.terrain 本身就是 gzip 流、GeoTIFF 是 LZW，再 deflate 一遍
                // 只能省千分之几，却要多花几分钟 CPU（22 万个文件）。存储即可。
                var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var target = entry.Open();
                if (item.Bytes is not null)
                    target.Write(item.Bytes);
                else
                    CopyStream(item.Path, target, cancellation);

                if (++written % 5000 == 0) progress?.Invoke((double)written / files.Count);
            }
            progress?.Invoke(1.0);
        }
        finally
        {
            channel.Writer.TryComplete();   // 写侧提前退出（取消/写失败）时别让读者卡死在满队列上
            producing.GetAwaiter().GetResult();
        }
    }

    /// <summary>大文件的流式拷贝；块间检查取消，别让一个几 GB 的文件吃掉整个取消响应。</summary>
    private static void CopyStream(string path, Stream target, CancellationToken cancellation)
    {
        using var source = File.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            target.Write(buffer, 0, read);
        }
    }
}
