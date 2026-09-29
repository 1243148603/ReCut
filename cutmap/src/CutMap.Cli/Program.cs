using CutMap.Core.Geo;

namespace CutMap.Cli;

/// <summary>
/// 无界面入口。存在的理由有两个：
///   1. M0 的两道闸门（warp 一致性、ctb-tile 子进程）在 GUI 建好之前就要能跑；
///   2. 以后做回归时，不依赖窗口也能验证产物。
/// 正式的用户入口始终是 CutMap.App。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();

        switch (args[0])
        {
            case "version":
                Console.WriteLine($"GDAL {GdalRuntime.ReleaseName()}");
                Console.WriteLine($"PROJ_DATA {GdalRuntime.ProjDataPath() ?? "(unset)"}");
                return 0;

            case "warp-gate":
            {
                var repo = args.Length > 1 ? args[1] : @"D:\Map";
                var outPath = args.Length > 2
                    ? args[2]
                    : Path.Combine(repo, "cutmap", "_gate", "chongqing_dem.tif");
                return Gates.WarpGate(repo, outPath);
            }

            case "ctb-gate":
                return Gates.CtbGate(args.Length > 1 ? args[1] : @"D:\Map");

            case "grid-gate":
                return Gates.TileGridGate(args.Length > 1 ? args[1] : @"D:\Map");

            case "verify":
            {
                var tiles = args.Length > 1
                    ? args[1]
                    : @"D:\Map\cutmap\_gate\slice_full";
                var dem = args.Length > 2 ? args[2] : @"D:\Map\cutmap\_gate\warp\chongqing_dem.tif";
                return VerifyCommand.Run(tiles, dem);
            }

            case "warp-vector":
                return VectorWarpCommand.Run(
                    @"D:\Map",
                    args.Length > 1 ? args[1] : "156500000",
                    args.Length > 2 ? args[2] : null);

            case "source-test":
                if (args.Length < 2) return Usage();
                return DownloadCommand.CustomSourceAsync(args[1]).GetAwaiter().GetResult();

            case "boundary":
                return BoundaryCommand.Run(@"D:\Map");

            case "download":
                return DownloadCommand.RunAsync(@"D:\Map", args.Contains("--network")).GetAwaiter().GetResult();

            case "publish":
            {
                if (args.Length < 2) return Usage();
                return PackageCommand.Publish(args[1], args.Length > 2 ? args[2] : null);
            }

            case "package":
                return PackageCommand.Run(
                    @"D:\Map",
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null,
                    args.Length > 3 ? args[3] : null);

            case "warp":
                return MosaicCommand.Run(
                    @"D:\Map",
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null,
                    args.Length > 3 ? args[3] : null);

            case "slice":
            {
                if (args.Length < 2) return Usage();
                var repo = @"D:\Map";
                int? maxZoom = args.Length > 3 ? int.Parse(args[3]) : null;
                return SliceCommand.Run(repo, args[1], args.Length > 2 ? args[2] : null, maxZoom);
            }

            default:
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            CutMap 命令行工具 —— 无界面，用于自动化与排障

            用法：CutMap.Cli <命令> [参数]

            日常使用
              slice <栅格> [输出目录] [最深层级]
                                     切成 Cesium 地形瓦片
              warp <输入目录> [输出文件] [西 南 东 北]
                                     多幅 DEM 拼接裁剪 + 算法元数据
              warp-vector <行政区代码> [输出文件]
                                     按真实行政边界裁剪（默认 156500000 重庆市）
              publish <瓦片目录> [裁剪后的 tif]
                                     写 layer.json / 部署说明 / 算法元数据（不打 zip）
              package [瓦片目录] [tif] [输出目录]
                                     打包与双产物（三种开关组合的验收）
              download [--network]   分幅枚举与 URL 模板展开
              verify [瓦片目录] [源 DEM]
                                     校验瓦片集能否被 Cesium 加载

            回归闸门（改了瓦片网格或 GDAL 版本之后跑）
              grid-gate              瓦片网格算术对基线（15 层 223,495）
              warp-gate              30 幅拼接，对基线校验和（期望 58775）
              ctb-gate               ctb-tile 子进程、实时进度、取消、退出码陷阱
              boundary               边界加载 / 过滤 / 矢量裁剪 / dissolve

              version                打印 GDAL 版本与 PROJ 数据目录

            退出码：0 成功，1 失败，2 用法错误。所有输出为 UTF-8。
            """);
        return 2;
    }
}
