using System.IO.Compression;
using System.Text;

namespace CutMap.Setup;

internal static class SetupGate
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cutmap-setup-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failed = 0;
        void Check(string name, bool pass)
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}");
            if (!pass) failed++;
        }
        void Reject(string name, Action action)
        {
            try { action(); Check(name, false); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Check(name, true); }
        }
        void Fixture(string path, bool complete)
        {
            foreach (var name in new[] { "CutMap.App.exe", "CutMap.Cli.exe", "coreclr.dll", "ctb/bin/ctb-tile.exe",
                "THIRD-PARTY-NOTICES.txt", "resources/中国_省.geojson", "resources/中国_市.geojson", "resources/中国_县.geojson" })
            {
                if (!complete && name == "coreclr.dll") continue;
                var file = Path.Combine(path, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "fixture");
            }
        }
        try
        {
            var setup = Path.Combine(root, "installer.exe");
            File.WriteAllText(setup, "fixture");
            var target = Path.Combine(root, "installed");
            Check("首次部署不产生旧版备份", InstallOperation.Deploy(target, p => Fixture(p, true), setup) is null);
            Check("首次部署写入归属标记", File.Exists(Path.Combine(target, InstallOperation.OwnershipFile)));
            File.WriteAllText(Path.Combine(target, "operator-dem.tif"), "user data");
            var backup = InstallOperation.Deploy(target, p => Fixture(p, true), setup);
            Check("升级保留旧版及用户数据", backup is not null && File.ReadAllText(Path.Combine(backup, "operator-dem.tif")) == "user data");
            Check("升级不把旧文件混入新版", !File.Exists(Path.Combine(target, "operator-dem.tif")));
            File.WriteAllText(Path.Combine(target, "current.txt"), "current");
            Reject("损坏载荷升级被拒绝", () => InstallOperation.Deploy(target, p => Fixture(p, false), setup));
            Check("升级失败仍保留当前版本", File.ReadAllText(Path.Combine(target, "current.txt")) == "current");
            Check("升级失败清理临时目录", !Directory.EnumerateDirectories(root, "*.stage-*").Any());
            var foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "important.txt"), "keep");
            Reject("拒绝覆盖非程序目录", () => InstallOperation.Deploy(foreign, p => Fixture(p, true), setup));
            Check("非程序目录保持原样", File.ReadAllText(Path.Combine(foreign, "important.txt")) == "keep");
            Reject("拒绝从安装目录内自覆盖", () => InstallOperation.Deploy(target, p => Fixture(p, true), Path.Combine(target, "CutMap-Setup.exe")));
            Reject("拒绝磁盘根目录", () => InstallOperation.ValidateTarget(Path.GetPathRoot(root)!));
            Check("无尾部载荷可识别", !Payload.IsPresent(out _, setup));
            var archive = Path.Combine(root, "payload.exe");
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open());
                    writer.Write("bad");
                }
                using var file = File.Create(archive);
                file.Write(Encoding.ASCII.GetBytes("fixture-header"));
                bytes.Position = 0;
                bytes.CopyTo(file);
                file.Write(BitConverter.GetBytes(bytes.Length));
                file.Write(Encoding.ASCII.GetBytes("CUTMAPPA"));
            }
            Check("真实尾部载荷可识别", Payload.IsPresent(out _, archive));
            Reject("拒绝 ZIP 路径穿越", () => Payload.ExtractTo(Path.Combine(root, "extract"), null, archive));
            Check("ZIP 未越界写文件", !File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally
        {
            // 仅删除本次生成的独立 fixture 根目录，不触碰真实安装、快捷方式或注册表。
            Directory.Delete(root, recursive: true);
        }
        return failed == 0 ? 0 : 1;
    }
}
