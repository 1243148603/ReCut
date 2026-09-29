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
        if (Directory.Exists(installDirectory))
        {
            // 覆盖安装前先清干净：旧版本可能留下已经不再分发的文件，
            // 留着它们会让"装完还是老行为"这种问题特别难查。
            progress?.Report((0, 0, "清除上一版残留"));
            Directory.Delete(installDirectory, recursive: true);
        }

        Payload.ExtractTo(installDirectory, progress);

        var appExe = AppExeIn(installDirectory);
        if (!File.Exists(appExe))
            throw new FileNotFoundException("安装包里没有 CutMap.App.exe", appExe);

        // 把安装器自己复制进安装目录：卸载项要指向一个稳定的路径。
        // 指向"用户当初双击的那个文件"是不行的——很多人装完就把安装包删了。
        var setupInInstallDir = Path.Combine(installDirectory, "CutMap-Setup.exe");
        File.Copy(Environment.ProcessPath!, setupInInstallDir, overwrite: true);

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
