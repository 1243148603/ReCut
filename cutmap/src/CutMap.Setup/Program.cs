namespace CutMap.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            return Uninstall(quiet);

        if (!Payload.IsPresent(out var reason))
        {
            MessageBox.Show(reason, "这不是安装包", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        Application.Run(new InstallerForm());
        return 0;
    }

    /// <summary>
    /// 卸载。目录里那份安装器会被自己删掉，所以用一个延迟的 cmd 收尾。
    /// </summary>
    private static int Uninstall(bool quiet)
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        if (!quiet &&
            MessageBox.Show(
                $"要从这台电脑上移除 {ShellIntegration.ProductName} 吗？\n\n" +
                $"安装位置：{directory}\n\n" +
                "你产出的瓦片与 DEM 数据不在这个目录里，不会被删除。",
                "卸载 " + ShellIntegration.ProductName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return 1;
        }

        ShellIntegration.DeleteShortcut(ShellIntegration.StartMenuShortcut);
        ShellIntegration.DeleteShortcut(ShellIntegration.DesktopShortcut);
        ShellIntegration.UnregisterUninstall();

        // 目录本身交给延迟的 cmd 删：里面那个 exe 就是当前正在运行的进程。
        ShellIntegration.ScheduleSelfDelete(directory);

        if (!quiet)
            MessageBox.Show(
                "已卸载。\n\n桌面与开始菜单的快捷方式已移除，程序目录会在几秒后消失。\n" +
                "（进程正在运行，Windows 不允许删除自己。）",
                "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

        return 0;
    }
}
