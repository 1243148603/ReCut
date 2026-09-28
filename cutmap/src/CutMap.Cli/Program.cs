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

            case "download":
                return DownloadCommand.RunAsync(@"D:\Map", args.Contains("--network")).GetAwaiter().GetResult();

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
            CutMap.Cli - headless diagnostics

              version              print GDAL release and PROJ data directory
              warp-gate [repo] [out]
                                   M0 gate 1: mosaic 30 DEMs and compare the
                                   checksum against the baseline (expect 58775)
              ctb-gate [repo]      M0 gate 2: ctb-tile subprocess, live progress,
                                   and the exit-code-0-on-failure trap
              grid-gate [repo]     M1 gate: tile-grid arithmetic vs the baseline
              warp [in] [out] [W S E N]
                                   M2: mosaic + clip + algo metadata json
              slice <tif> [out] [maxzoom]
                                   M1: slice a GeoTIFF into terrain tiles
            """);
        return 2;
    }
}
