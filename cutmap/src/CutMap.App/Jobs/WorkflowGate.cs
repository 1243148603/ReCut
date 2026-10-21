using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

internal static class WorkflowGate
{
    public static int Run(string repo)
    {
        var root = Path.Combine(repo, "cutmap", "_gate", "workflow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.tif");
        File.Copy(Path.Combine(repo, "baseline", "maxzoom_calibration", "res_3_3600.tif"), input);
        var info = RasterInfo.Read(input);
        var before = AlgoMetadata.Fingerprint(input);
        var log = new List<string>(); var failures = 0;
        void Check(string name, bool pass) { log.Add($"[{(pass ? "PASS" : "FAIL")}] {name}"); if (!pass) failures++; }
        void Execute(IReadOnlyList<Job> jobs)
        {
            foreach (var job in jobs)
            {
                var result = job.Execute(CtbHome.Default(repo), _ => { }, _ => { }, CancellationToken.None);
                if (!result.Succeeded) throw new InvalidOperationException(result.FailureReason);
            }
        }
        var full = new PipelinePlan([input], Path.Combine(root, "full"), true, true, true, true, true, info.Extent, MaxZoom: 8);
        Check("全流程生成三步", full.CreateJobs().Count == 3);
        Execute(full.CreateJobs());
        Check("全流程双产物", File.Exists(Path.Combine(full.Tiles, "layer.json")) && File.Exists(Path.ChangeExtension(full.Tif, ".json")));
        var algo = full with { Root = Path.Combine(root, "algo"), Slice = false, Package = false, WantTiles = false };
        Check("只算法不启动切片", algo.CreateJobs().All(j => j is not SliceJob));
        Execute(algo.CreateJobs());
        Check("只算法不生成瓦片目录", !Directory.Exists(algo.Tiles) && File.Exists(Path.ChangeExtension(algo.Tif, ".json")));
        var front = full with { Root = Path.Combine(root, "front"), WantAlgo = false };
        Execute(front.CreateJobs());
        Check("只前端不生成算法 JSON", !File.Exists(Path.ChangeExtension(front.Tif, ".json")) && File.Exists(Path.Combine(front.Tiles, "layer.json")));
        var slice = full with { Root = Path.Combine(root, "slice"), Mosaic = false, Package = false, WantAlgo = false };
        Check("只切片跳过其他步骤", slice.CreateJobs() is [SliceJob]);
        Execute(slice.CreateJobs());
        Check("只切片不生成 layer.json", !File.Exists(Path.Combine(slice.Tiles, "layer.json")));
        var package = full with { Root = Path.Combine(root, "package"), Inputs = [], Mosaic = false, Slice = false, WantAlgo = false, ExistingTiles = slice.Tiles };
        Check("只打包无需 DEM", package.CreateJobs() is [PackageJob]);
        Execute(package.CreateJobs());
        Check("只打包生成 layer.json", File.Exists(Path.Combine(slice.Tiles, "layer.json")));
        var existingAlgo = full with { Mosaic = false, Slice = false, Package = false, WantTiles = false };
        Execute(existingAlgo.CreateJobs());
        Check("现成 DEM 只补元数据且不重写 TIFF", AlgoMetadata.Fingerprint(input) == before && File.Exists(Path.ChangeExtension(input, ".json")));
        try { (full with { MaxZoom = 30 }).CreateJobs(); Check("失控层级拦截", false); }
        catch (ArgumentException) { Check("失控层级拦截", true); }
        Check("NaN 范围拒绝", !new BBox(double.NaN, 0, 1, 1).IsValid(out _));
        using (var form = new Forms.PackageForm(slice.Tiles, input, root))
        using (var data = System.Text.Json.JsonDocument.Parse("{\"layerName\":\"history\",\"layerDescription\":\"test\",\"layerVersion\":\"2.0\",\"zipTiles\":true,\"zipAlgo\":true,\"layerMaxZoom\":8}"))
        {
            form.ApplyHistory(data.RootElement);
            Check("打包历史字段与开关回填", form.LayerName == "history" && form.LayerVersion == "2.0" && form.ZipTiles && form.ZipAlgo && form.LayerMaxZoom == 8);
        }
        var report = Path.Combine(repo, "outputs", "stage3-workflow-gate.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllLines(report, log);
        foreach (var line in log) Console.WriteLine(line);
        return failures == 0 ? 0 : 1;
    }
}
