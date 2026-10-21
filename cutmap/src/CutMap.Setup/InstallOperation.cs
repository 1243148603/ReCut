using System.Diagnostics;

namespace CutMap.Setup;

/// <summary>
/// 安装动作本身。界面（<see cref="InstallerForm"/>）与静默模式（<c>--silent</c>）
/// 共用这一份，免得两条路径的行为慢慢分家。
/// </summary>
internal static class InstallOperation
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ShellIntegration.ProductName);

    public static string AppExeIn(string directory) => Path.Combine(directory, "CutMap.App.exe");

    internal const string OwnershipFile = ".cutmap-install";

    internal static void ValidateTarget(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能把磁盘根目录作为安装目录。");
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("安装目录不能是目录联接或符号链接。");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() &&
            !File.Exists(Path.Combine(full, OwnershipFile)))
        {
            // 兼容旧版仅限固定安装位置，不能把任意用户目录认作旧程序。
            var legacy = string.Equals(full, Path.GetFullPath(DefaultDirectory), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(AppExeIn(full)) && File.Exists(Path.Combine(full, "CutMap.Cli.exe")) &&
                File.Exists(Path.Combine(full, "ctb", "bin", "ctb-tile.exe"));
            if (!legacy) throw new InvalidOperationException("目标目录包含非 CutMap 文件，拒绝覆盖。");
        }
    }

    internal static void ValidateFiles(string directory)
    {
        string[] required = ["CutMap.App.exe", "CutMap.Cli.exe", "coreclr.dll",
            "ctb/bin/ctb-tile.exe", "THIRD-PARTY-NOTICES.txt",
            "resources/中国_省.geojson", "resources/中国_市.geojson", "resources/中国_县.geojson"];
        foreach (var relative in required)
            if (!File.Exists(Path.Combine(directory, relative)))
                throw new InvalidDataException($"安装载荷缺少必需文件：{relative}");
    }

    // 解压和验证都在同盘临时目录完成，旧版直到替换前始终可用。
    // 备份保留供回退，也避免升级删除用户误放到安装目录里的数据。
    internal static string? Deploy(string directory, Action<string> extract, string setupPath)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        ValidateTarget(directory);
        if (Path.GetFullPath(setupPath).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从安装目录外运行新版安装包，不能覆盖正在运行的安装器。");
        var stage = directory + ".stage-" + Guid.NewGuid().ToString("N");
        var backup = directory + ".previous-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var moved = false;
        try
        {
            Directory.CreateDirectory(stage);
            extract(stage);
            ValidateFiles(stage);
            File.Copy(setupPath, Path.Combine(stage, "CutMap-Setup.exe"));
            File.WriteAllText(Path.Combine(stage, OwnershipFile), ShellIntegration.Version);
            if (Directory.Exists(directory))
            {
                Directory.Move(directory, backup);
                moved = true;
            }
            try { Directory.Move(stage, directory); }
            catch
            {
                if (moved && !Directory.Exists(directory)) Directory.Move(backup, directory);
                throw;
            }
            return moved ? backup : null;
        }
        finally
        {
            // stage 是本次生成的固定同级路径，不使用外部输入作为递归清理目标。
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>
    /// 装到 <paramref name="installDirectory"/>。抛异常即失败，异常消息面向用户。
    /// </summary>
    /// <param name="progress">(已展开, 总数, 当前文件)。</param>
    /// <param name="launchAfter">装完是否启动程序。</param>
    public static void Run(
        string installDirectory,
        IProgress<(int Done, int Total, string Name)>? progress,
        bool launchAfter)
    {
        if (!string.Equals(Path.GetFullPath(installDirectory), Path.GetFullPath(DefaultDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装器只允许安装到当前用户的 CutMap 程序目录。");
        var backup = Deploy(installDirectory, target => Payload.ExtractTo(target, progress), Environment.ProcessPath!);
        if (backup is not null) progress?.Report((0, 0, $"旧版及原目录文件已保留：{backup}"));

        var appExe = AppExeIn(installDirectory);
        if (!File.Exists(appExe))
            throw new FileNotFoundException("安装包里没有 CutMap.App.exe", appExe);

        // 把安装器自己复制进安装目录：卸载项要指向一个稳定的路径。
        // 指向"用户当初双击的那个文件"是不行的——很多人装完就把安装包删了。
        var setupInInstallDir = Path.Combine(installDirectory, "CutMap-Setup.exe");

        progress?.Report((0, 0, "创建快捷方式"));
        ShellIntegration.CreateShortcut(ShellIntegration.StartMenuShortcut, appExe, installDirectory);
        ShellIntegration.CreateShortcut(ShellIntegration.DesktopShortcut, appExe, installDirectory);
        ShellIntegration.RegisterUninstall(installDirectory, setupInInstallDir, TotalBytes(installDirectory));

        if (launchAfter)
        {
            progress?.Report((0, 0, "启动 CutMap"));
            Process.Start(new ProcessStartInfo(appExe) { WorkingDirectory = installDirectory });
        }
    }

    public static long TotalBytes(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
