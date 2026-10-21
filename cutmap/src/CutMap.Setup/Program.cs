using System.Runtime.InteropServices;

namespace CutMap.Setup;

internal static class Program
{
    /// <summary>静默模式下把控制台接回来，好让调用方拿到输出。</summary>
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (Has(args, "--self-test"))
        {
            AttachConsole(AttachParentProcess);
            return SetupGate.Run();
        }
        if (Has(args, "--verify"))
        {
            AttachConsole(AttachParentProcess);
            if (!Payload.IsPresent(out var verificationError))
            {
                Console.Error.WriteLine(verificationError);
                return 2;
            }
            var temporary = Path.Combine(Path.GetTempPath(), "cutmap-verify-" + Guid.NewGuid().ToString("N"));
            try
            {
                Payload.ExtractTo(temporary, null);
                InstallOperation.ValidateFiles(temporary);
                Console.WriteLine("PASS 安装载荷可解压且包含自包含运行时、CTB 和边界数据。");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }

        var uninstall = Has(args, "--uninstall");
        var silent = Has(args, "--silent") || Has(args, "--quiet");
        var noLaunch = Has(args, "--no-launch");

        if (uninstall) return Uninstall(silent);

        if (!Payload.IsPresent(out var reason))
        {
            if (silent) Console.Error.WriteLine("错误：" + reason);
            else MessageBox.Show(reason, "这不是安装包", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        if (silent) return SilentInstall(noLaunch);

        using var installer = new InstallerForm();
        Application.Run(installer);
        return 0;
    }

    private static bool Has(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 无人值守安装：<c>--silent [--no-launch]</c>。
    /// 既给自动化验证用，也给批量部署用——这也是自研安装器比 Inno Setup 好办的地方。
    /// </summary>
    private static int SilentInstall(bool noLaunch)
    {
        AttachConsole(AttachParentProcess);
        var directory = InstallOperation.DefaultDirectory;
        try
        {
            Console.WriteLine($"正在安装到 {directory} …");
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                if (p.Total > 0 && (p.Done % 500 == 0 || p.Done == p.Total))
                    Console.WriteLine($"  {p.Done}/{p.Total}");
            });
            InstallOperation.Run(directory, progress, launchAfter: !noLaunch);
            Console.WriteLine("安装完成。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("安装失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 卸载。目录里那份安装器会被自己删掉，所以用一个延迟的 cmd 收尾。
    /// </summary>
    private static int Uninstall(bool silent)
    {
        AttachConsole(AttachParentProcess);
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(InstallOperation.DefaultDirectory), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory, InstallOperation.OwnershipFile)))
        {
            if (!silent) MessageBox.Show("只能从已登记的 CutMap 安装目录卸载。", "无法卸载");
            return 2;
        }

        if (!silent &&
            MessageBox.Show(
                $"要从这台电脑上移除 {ShellIntegration.ProductName} 吗？\n\n" +
                $"安装位置：{directory}\n\n" +
                "此目录内的文件将被删除，请先移走放在这里的瓦片或 DEM。用户配置与同级升级备份保留。",
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

        if (silent)
        {
            Console.WriteLine("已卸载。程序目录会在几秒后消失。");
        }
        else
        {
            MessageBox.Show(
                "已卸载。\n\n桌面与开始菜单的快捷方式已移除，程序目录会在几秒后消失。\n" +
                "（进程正在运行，Windows 不允许删除自己。）",
                "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }
}
