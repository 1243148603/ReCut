using System.Diagnostics;
using System.Text.Json;
using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

internal static class EndToEndGate
{
    public static int Benchmark(string repo, string input)
    {
        var root = Path.Combine(repo, "cutmap", "_gate", "benchmark", Guid.NewGuid().ToString("N"));
        var home = CtbHome.Default(repo);
        var observations = new List<object>();
        var allPassed = true;
        for (var round = 0; round < 3; round++)
        {
            var nativeOutput = Path.Combine(root, $"native-{round}");
            var guiOutput = Path.Combine(root, $"gui-{round}");
            Directory.CreateDirectory(nativeOutput);
            var start = new ProcessStartInfo(home.ExePath) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-m", "67108864", "-s", "14", "-e", "0", "-o", nativeOutput, input }) start.ArgumentList.Add(arg);
            start.Environment["PROJ_DATA"] = home.ProjData;
            start.Environment["GDAL_DATA"] = home.GdalData;
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            var nativeSeconds = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var job = new SliceJob { InputPath = input, OutputDirectory = guiOutput, MaxZoom = 14 };
            var result = job.Execute(home, _ => { }, _ => { }, CancellationToken.None);
            var guiSeconds = watch.Elapsed.TotalSeconds;
            var sameCounts = Slicer.CountByLevel(nativeOutput, 14).SequenceEqual(Slicer.CountByLevel(guiOutput, 14));
            var passed = result.Succeeded && process.ExitCode == 0 && !stderr.Result.Contains("Error:") && sameCounts;
            allPassed &= passed;
            observations.Add(new { round, nativeSeconds, guiSeconds, ratio = guiSeconds / nativeSeconds, sameCounts, passed });
        }
        File.WriteAllText(Path.Combine(repo, "outputs", "stage5-wrapper-benchmark.json"),
            JsonSerializer.Serialize(new { input, observations, passed = allPassed,
                note = "Small ROI, three fresh directories; comparison covers task execution, not manual UI interaction or full-region speed." }, new JsonSerializerOptions { WriteIndented = true }));
        return allPassed ? 0 : 1;
    }

    public static int Run(string repo)
    {
        var output = Path.Combine(repo, "cutmap", "_gate", "approved-e2e", "gui");
        var sources = Directory.GetFiles(Path.Combine(repo, "data", "inputs", "dem"), "*.tif").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var plan = new PipelinePlan(sources, output, true, true, true, true, true, new BBox(105.3, 28.1, 110.2, 32.2), MaxZoom: 14);
        var reportPath = Path.Combine(repo, "outputs", "stage5-gui-e2e.txt");
        using var log = new Core.Store.TaskLog(reportPath);
        var timings = new Dictionary<string, double>();
        foreach (var job in plan.CreateJobs())
        {
            var watch = Stopwatch.StartNew();
            var result = job.Execute(CtbHome.Default(repo), _ => { }, log.Write, CancellationToken.None);
            watch.Stop();
            timings[job.TypeName] = watch.Elapsed.TotalSeconds;
            log.Write($"{job.TypeName}：{watch.Elapsed.TotalSeconds:F3} s；{result.Summary}");
            if (!result.Succeeded) { log.Write("FAIL " + result.FailureReason); return 1; }
        }
        var native = Path.Combine(repo, "cutmap", "_gate", "approved-e2e", "cli-tiles");
        var nativeCounts = Slicer.CountByLevel(native, 14);
        var guiCounts = Slicer.CountByLevel(plan.Tiles, 14);
        var countsMatch = nativeCounts.SequenceEqual(guiCounts) && guiCounts.Sum() == 223495;
        log.Write($"{(countsMatch ? "PASS" : "FAIL")} 15 层瓦片数量对拍：{guiCounts.Sum()}");
        var sampled = 0; var mismatches = 0;
        foreach (var file in Directory.EnumerateFiles(plan.Tiles, "*.terrain", SearchOption.AllDirectories).Where((_, i) => i % 997 == 0))
        {
            sampled++;
            var reference = Path.Combine(native, Path.GetRelativePath(plan.Tiles, file));
            if (!File.Exists(reference) || AlgoMetadata.Fingerprint(file) != AlgoMetadata.Fingerprint(reference)) mismatches++;
        }
        log.Write($"{(mismatches == 0 ? "PASS" : "FAIL")} 抽样内容对拍：{sampled}，不同 {mismatches}");
        var timingPath = Path.Combine(repo, "outputs", "stage5-gui-timing.json");
        File.WriteAllText(timingPath, JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
        return countsMatch && mismatches == 0 ? 0 : 1;
    }
}
