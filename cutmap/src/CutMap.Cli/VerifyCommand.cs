using System.IO.Compression;
using System.Text.Json;
using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using OSGeo.GDAL;

namespace CutMap.Cli;

/// <summary>
/// 校验一份瓦片集能不能真的被前端加载（PRD 验收 #2 / 204 教程 §8）。
///
/// **为什么不能只数文件。** 数出 223,495 个文件、逐层计数全对，前端仍可能一片空白——
/// README §6.5 记录的正是这个故障：瓦片全是 200、layer.json 也能解析，屏幕上什么都没有。
/// 原因只是服务器没发 `Content-Encoding: gzip`，浏览器把压缩字节原样交给 Cesium，
/// HeightmapTerrainData 构造时抛 RangeError。
///
/// 所以这里解到数据层：gzip 魔数、解压后长度、以及**按 heightmap-1.0 解出的高程
/// 与源 DEM 对得上**。最后一条是关键——它同时验证了瓦片内容、范围、TMS 编号方向。
/// </summary>
internal static class VerifyCommand
{
    /// <summary>一对经纬度，用来抽查高程。</summary>
    private static readonly (string Name, double Lon, double Lat)[] SamplePoints =
    [
        ("中部偏西", 107.75, 30.15),
        ("西北部", 106.25, 31.65),
        ("东北部", 109.45, 31.25),
        ("西南部", 105.55, 28.35),
    ];

    public static int Run(string tilesDirectory, string? sourceDem)
    {
        if (!Directory.Exists(tilesDirectory))
        {
            Console.Error.WriteLine($"FAIL  瓦片目录不存在：{tilesDirectory}");
            return 1;
        }

        var ok = true;

        // ---------- 一、layer.json ----------
        Console.WriteLine("一、layer.json");
        var layerPath = Path.Combine(tilesDirectory, "layer.json");
        if (!File.Exists(layerPath))
        {
            Console.Error.WriteLine($"FAIL  没有 layer.json（Cesium 的入口，缺了就无法加载）");
            return 1;
        }

        var layer = JsonDocument.Parse(File.ReadAllText(layerPath)).RootElement;
        ok &= Check("format", layer.GetProperty("format").GetString() ?? "", "heightmap-1.0");
        ok &= Check("scheme", layer.GetProperty("scheme").GetString() ?? "", "tms");
        ok &= Check("tiles 模板", layer.GetProperty("tiles")[0].GetString() ?? "", "{z}/{x}/{y}.terrain");

        // 扩展名大小写：Cesium 区分大小写，写成 Watermask 会静默失效。
        var extensions = layer.GetProperty("extensions").EnumerateArray()
            .Select(e => e.GetString() ?? "").ToArray();
        ok &= Check("扩展名小写", string.Join(",", extensions), "watermask");

        var declaredMaxZoom = layer.GetProperty("maxzoom").GetInt32();
        var declaredBounds = layer.GetProperty("bounds").EnumerateArray()
            .Select(e => e.GetDouble()).ToArray();
        Console.WriteLine($"  声明：maxzoom={declaredMaxZoom}，bounds=" +
                          $"[{string.Join(", ", declaredBounds.Select(v => v.ToString("F4")))}]");

        // ---------- 二、目录与声明是否一致 ----------
        Console.WriteLine();
        Console.WriteLine("二、目录结构与 layer.json 是否对得上");
        var coverage = LayerJson.InspectTiles(tilesDirectory);
        if (coverage is null)
        {
            Console.Error.WriteLine("FAIL  目录里没有 .terrain 瓦片");
            return 1;
        }
        Console.WriteLine($"  实测：{coverage.MinZoom}–{coverage.DeepestZoom} 级，共 {coverage.TileCount:N0} 个");
        ok &= Check("maxzoom 一致", declaredMaxZoom.ToString(), coverage.DeepestZoom.ToString());
        ok &= Check("目录里有最深层的瓦片",
                    coverage.DeepestZoom == declaredMaxZoom);

        // 声明的 bounds 应当盖住瓦片实际包络（瓦片是按整格切的，只会更大不会更小）。
        var tileBounds = coverage.Bounds;
        var covered = declaredBounds[0] <= tileBounds.West + 1e-9 &&
                      declaredBounds[1] <= tileBounds.South + 1e-9 &&
                      declaredBounds[2] >= tileBounds.East - 1e-9 &&
                      declaredBounds[3] >= tileBounds.North - 1e-9;
        ok &= Check("bounds 盖住瓦片包络", covered,
                    true, $"瓦片包络 [{tileBounds.West:F4}, {tileBounds.South:F4}, " +
                          $"{tileBounds.East:F4}, {tileBounds.North:F4}]");

        // ---------- 三、瓦片能否解出高程 ----------
        Console.WriteLine();
        Console.WriteLine("三、瓦片数据（这层不过，前端就一定白屏）");

        var sample = FindSampleTile(tilesDirectory, declaredMaxZoom, declaredBounds);
        if (sample is null)
        {
            Console.Error.WriteLine("FAIL  在最深层里找不到任何瓦片");
            return 1;
        }

        var (samplePath, sampleX, sampleY) = sample.Value;
        Console.WriteLine($"  抽样：{declaredMaxZoom}/{sampleX}/{sampleY}.terrain");

        var raw = File.ReadAllBytes(samplePath);
        var isGzip = raw.Length >= 2 && raw[0] == 0x1f && raw[1] == 0x8b;
        ok &= Check("是 gzip 流（服务器必须声明 Content-Encoding: gzip）", isGzip);
        Console.WriteLine($"  磁盘上 {raw.Length} 字节" +
                          (isGzip ? "（gzip 压缩态）" : "（未压缩）"));

        byte[] payload;
        if (isGzip)
        {
            using var input = new MemoryStream(raw);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            payload = output.ToArray();
        }
        else
        {
            payload = raw;
        }

        // 65×65 的 uint16 高度网格 + 1 字节 childTileMask + 1 字节 waterMask。
        var expectedLength = 65 * 65 * 2 + 2;
        ok &= Check("解压后长度", payload.Length.ToString(), expectedLength.ToString());

        if (payload.Length < expectedLength)
        {
            Console.Error.WriteLine("FAIL  解压后的数据不足以构成一块瓦片");
            return 1;
        }

        var height = DecodeHeight(payload, 32, 32);
        Console.WriteLine($"  中心像素高程：{height:F1} m");

        // ---------- 四、与源 DEM 对照 ----------
        if (sourceDem is null || !File.Exists(sourceDem))
        {
            Console.WriteLine();
            Console.WriteLine("（未提供源 DEM，跳过高程对照）");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"四、与源 DEM 对照（{Path.GetFileName(sourceDem)}）");
            var info = RasterInfo.Read(sourceDem);
            Console.WriteLine($"  源：{info.Summary()}");

            var worst = 0.0;
            foreach (var (name, lon, lat) in SamplePoints)
            {
                var decoded = SampleTileHeight(tilesDirectory, declaredMaxZoom, lon, lat);
                if (decoded is null)
                {
                    Console.WriteLine($"  [--] {name} ({lon}E {lat}N)：该点没有瓦片");
                    continue;
                }
                var source = SampleDemHeight(info, lon, lat);
                if (source is null)
                {
                    Console.WriteLine($"  [--] {name}：源 DEM 取不到值");
                    continue;
                }
                var delta = decoded.Value - source.Value;
                worst = Math.Max(worst, Math.Abs(delta));
                Console.WriteLine($"  [OK] {name} ({lon}E {lat}N)：瓦片 {decoded:F1} m / " +
                                  $"源 {source:F1} m / 差 {delta:+0.0;-0.0;0.0} m");
            }

            // 阈值 50 m 是怎么来的：
            //
            // 瓦片是 30 m 源网格重采样到 65×65 的产物，而这里比的是**单个格子对单个像元**。
            // 两者中心最多差半个像元，在陡峭处（如三峡一带）十几米水平偏移就足以造成
            // 二三十米垂直差——那是重采样本身的噪声，不是数据错。README §8.2 用同样
            // 思路实测记录的是 −4.6 ~ +12.4 m，换成更陡的点就会更大。
            //
            // 这道检查要抓的是**粗错**：取错瓦片、TMS 方向反了、heightmap 解码错位。
            // 那些错误的量级是几百到几千米——z14 上错一块瓦片就是约 1.2 km。
            // 50 m 既容得下重采样噪声，又远小于任何一类真实故障。
            ok &= Check("最大高差在重采样噪声量级内（< 50 m）", worst < 50.0, true,
                        $"实测最大 {worst:F1} m（z14 错一块瓦片约 1200 m）");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  verify：这份瓦片集可以被 Cesium 加载" : "FAIL  verify");
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- 瓦片

    private static (string Path, int X, int Y)? FindSampleTile(
        string tilesDirectory, int zoom, double[] bounds)
    {
        // 取声明范围中心所在的那块，比取第一块更有代表性（第一块通常是边缘，全是裙边）。
        var centreLon = (bounds[0] + bounds[2]) / 2;
        var centreLat = (bounds[1] + bounds[3]) / 2;
        var (x, y) = CtbGrid.TileIndex(centreLon, centreLat, zoom);

        var path = Path.Combine(tilesDirectory, zoom.ToString(), x.ToString(), $"{y}.terrain");
        if (File.Exists(path)) return (path, x, y);

        // 中心没有就退而求其次，随便找一块。
        var levelDir = Path.Combine(tilesDirectory, zoom.ToString());
        if (!Directory.Exists(levelDir)) return null;
        foreach (var xDir in Directory.EnumerateDirectories(levelDir))
        {
            foreach (var file in Directory.EnumerateFiles(xDir, "*.terrain"))
            {
                return (file, int.Parse(Path.GetFileName(xDir)), int.Parse(Path.GetFileNameWithoutExtension(file)));
            }
        }
        return null;
    }

    /// <summary>点所在的瓦片里，覆盖该点的那一个像素的高程。取不到返回 null。</summary>
    private static double? SampleTileHeight(string tilesDirectory, int zoom, double lon, double lat)
    {
        var (x, y) = CtbGrid.TileIndex(lon, lat, zoom);
        var path = Path.Combine(tilesDirectory, zoom.ToString(), x.ToString(), $"{y}.terrain");
        if (!File.Exists(path)) return null;

        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var payload = output.ToArray();
        if (payload.Length < 65 * 65 * 2 + 2) return null;

        // 瓦片在该层覆盖的经纬度范围（CTB 的 geodetic 剖分：横向 2^(z+1) 块、纵向 2^z 块）。
        var lonSpan = 360.0 / Math.Pow(2, zoom + 1);
        var latSpan = 180.0 / Math.Pow(2, zoom);
        var west = -180.0 + x * lonSpan;
        var south = -90.0 + y * latSpan;

        var col = (int)Math.Floor((lon - west) / lonSpan * 64.0);
        var row = (int)Math.Floor((south + latSpan - lat) / latSpan * 64.0);
        col = Math.Clamp(col, 0, 64);
        row = Math.Clamp(row, 0, 64);

        return DecodeHeight(payload, row, col);
    }

    /// <summary>
    /// heightmap-1.0：小端 uint16 数组，<c>原始值 = (米 + 1000) × 5</c>。
    /// 见 CTB 的 TerrainTiler.cpp:50。
    /// </summary>
    private static double DecodeHeight(byte[] payload, int row, int col)
    {
        var offset = (row * 65 + col) * 2;
        var raw = (ushort)(payload[offset] | (payload[offset + 1] << 8));
        return raw / 5.0 - 1000.0;
    }

    /// <summary>
    /// 从源 DEM 取某点的高程。用绑定的 RasterIO 单像素读，不起子进程。
    ///
    /// **取最近像元而不是 floor。** 这与 README §8.2 记录参考值用的
    /// <c>gdallocationinfo -geoloc</c> 行为一致。用 floor 会引入最多半个像元的
    /// 位置偏差，而源是 30 m 网格——在陡峭处半个像元（约 15 m 水平）足以造成
    /// 二十几米的垂直差，那是我自己的取法带来的，不是数据的问题。
    /// </summary>
    private static double? SampleDemHeight(RasterInfo info, double lon, double lat)
    {
        GdalRuntime.EnsureReady();
        using var dataset = Gdal.OpenEx(info.Path, (uint)Access.GA_ReadOnly, null, null, null);
        using var band = dataset?.GetRasterBand(1);
        if (band is null) return null;

        var gt = info.GeoTransform;
        var col = (int)Math.Round((lon - gt[0]) / gt[1] - 0.5, MidpointRounding.AwayFromZero);
        var row = (int)Math.Round((lat - gt[3]) / gt[5] - 0.5, MidpointRounding.AwayFromZero);
        if (col < 0 || row < 0 || col >= info.Width || row >= info.Height) return null;

        var buffer = new float[1];
        band.ReadRaster(col, row, 1, 1, buffer, 1, 1, 0, 0);
        return buffer[0];
    }

    private static bool Check(string label, string actual, string expected)
    {
        var pass = actual == expected;
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: {actual}" + (pass ? "" : $" / 期望 {expected}"));
        return pass;
    }

    private static bool Check(string label, bool pass) =>
        Check(label, pass ? "是" : "否", "是");

    private static bool Check(string label, bool pass, bool _, string detail)
    {
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}：{detail}");
        return pass;
    }
}
