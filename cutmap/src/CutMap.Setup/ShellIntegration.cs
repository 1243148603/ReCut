using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace CutMap.Setup;

/// <summary>
/// 快捷方式、卸载登记、自删除。
///
/// 全部走 **HKCU**（当前用户），不碰 HKLM：这样安装与卸载都不需要管理员权限。
/// 代价是"所有用户"看不到这个程序，但换来的是双击就能装——对这类交付工具更合适。
/// </summary>
internal static class ShellIntegration
{
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CutMap";

    public static string ProductName => "CutMap";
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>开始菜单里的快捷方式路径。</summary>
    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     ProductName + ".lnk");

    public static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     ProductName + ".lnk");

    /// <summary>
    /// 建快捷方式。用 WScript.Shell 的 COM 接口——.NET 没有内建的 .lnk 写入 API，
    /// 引一个 COM 组件比引一个第三方库轻。
    /// </summary>
    public static void CreateShortcut(string shortcutPath, string targetExe, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统里没有 WScript.Shell，无法创建快捷方式。");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = targetExe + ",0";
            shortcut.Description = "CutMap — DEM 地形切片";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    public static void DeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 快捷方式删不掉不该让卸载失败——残一个 .lnk 指向不存在的目录，
            // 系统自己会标记为失效，比卸载中断好。
        }
    }

    /// <summary>在"应用和功能"里登记一条，让用户能从这里卸载。</summary>
    public static void RegisterUninstall(string installDirectory, string setupExe, long estimatedBytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("写不进注册表（HKCU 的卸载项）。");

        key.SetValue("DisplayName", ProductName + " — DEM 地形切片");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "CutMap");
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("DisplayIcon", Path.Combine(installDirectory, "CutMap.App.exe") + ",0");
        // 卸载时不复制自身：直接调安装目录里那份安装器的 --uninstall。
        key.SetValue("UninstallString", $"\"{setupExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{setupExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (estimatedBytes > 0)
            key.SetValue("EstimatedSize", (int)(estimatedBytes / 1024), RegistryValueKind.DWord);
    }

    public static void UnregisterUninstall()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 同上：注册表项残留比卸载中断轻。
        }
    }

    /// <summary>已安装的目录；没装过返回 null。</summary>
    public static string? InstalledDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>
    /// 删掉整个安装目录，包括**正在运行的这个 exe**。
    ///
    /// Windows 不允许删除正在运行的可执行文件，所以让 cmd 等一小会儿再删：
    /// 进程退出后 cmd 才动手，那时文件已经解锁。用 `ping -n` 而不是 `timeout`
    /// 来延时——`timeout` 在没有控制台时会直接报错退出。
    /// </summary>
    public static void ScheduleSelfDelete(string directory)
    {
        if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(InstallOperation.DefaultDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理固定安装目录之外的路径。");
        InstallOperation.ValidateTarget(directory);
        var literal = directory.Replace("'", "''", StringComparison.Ordinal);
        var script = $"Start-Sleep -Seconds 3; Remove-Item -LiteralPath '{literal}' -Recurse -Force";
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
