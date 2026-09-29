using System.Diagnostics;

namespace CutMap.Setup;

/// <summary>安装界面。装到 <c>%LOCALAPPDATA%\Programs\CutMap</c>，不需要管理员权限。</summary>
internal sealed class InstallerForm : Form
{
    private readonly Label _headline = new();
    private readonly Label _detail = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _action = new();
    private readonly Button _close = new();
    private readonly CheckBox _launch = new();

    private readonly string _installDirectory;
    private bool _installed;

    public InstallerForm()
    {
        _installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", ShellIntegration.ProductName);

        Text = $"安装 {ShellIntegration.ProductName} {ShellIntegration.Version}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 300);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        var existing = ShellIntegration.InstalledDirectory();
        if (existing is not null && !string.Equals(existing, _installDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _detail.Text = $"检测到另一处安装：{existing}\n本次会装到 {_installDirectory}，两者互不影响。";
        }
    }

    private void BuildLayout()
    {
        _headline.Text = $"{ShellIntegration.ProductName} — DEM 地形切片";
        _headline.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        _headline.Dock = DockStyle.Top;
        _headline.Height = 44;
        _headline.Padding = new Padding(20, 18, 20, 0);

        var body = new Label
        {
            Dock = DockStyle.Top,
            Height = 96,
            Padding = new Padding(20, 4, 20, 0),
            Text = "把 Copernicus DEM 变成 Cesium 地形瓦片，并同时产出给算法侧的\n" +
                   "裁剪 GeoTIFF + 元数据 json。\n\n" +
                   $"安装位置：{_installDirectory}\n" +
                   "按用户安装，不需要管理员权限。",
        };

        _detail.Dock = DockStyle.Top;
        _detail.Height = 40;
        _detail.Padding = new Padding(20, 4, 20, 0);
        _detail.ForeColor = SystemColors.GrayText;

        _progress.Dock = DockStyle.Top;
        _progress.Height = 18;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Margin = new Padding(20);

        var progressHost = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(20, 8, 20, 0) };
        progressHost.Controls.Add(_progress);
        _progress.Dock = DockStyle.Fill;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 56,
            Padding = new Padding(20, 10, 20, 10),
        };
        _action.Text = "安装";
        _action.Width = 100;
        _action.Height = 30;
        _action.Click += (_, _) =>
        {
            // 装完之后同一个按钮变成"完成"。
            if (_installed) Close();
            else Install();
        };
        _close.Text = "取消";
        _close.Width = 100;
        _close.Height = 30;
        _close.Click += (_, _) => Close();
        buttons.Controls.Add(_action);
        buttons.Controls.Add(_close);

        _launch.Text = "安装完成后立即运行 CutMap";
        _launch.Checked = true;
        _launch.AutoSize = true;
        _launch.Dock = DockStyle.Bottom;
        _launch.Padding = new Padding(22, 0, 0, 8);

        Controls.Add(progressHost);
        Controls.Add(_detail);
        Controls.Add(body);
        Controls.Add(_headline);
        Controls.Add(_launch);
        Controls.Add(buttons);
        AcceptButton = _action;
        CancelButton = _close;
    }

    private void Install()
    {
        _action.Enabled = false;
        _close.Enabled = false;
        _launch.Enabled = false;
        _detail.ForeColor = SystemColors.GrayText;

        try
        {
            if (Directory.Exists(_installDirectory))
            {
                // 覆盖安装前先清干净：旧版本可能留下已经不再分发的文件，
                // 留着它们会让"装完还是老行为"这种问题特别难查。
                _detail.Text = "清除上一版残留…";
                Application.DoEvents();
                Directory.Delete(_installDirectory, recursive: true);
            }

            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                _progress.Maximum = p.Total;
                _progress.Value = Math.Min(p.Done, p.Total);
                _detail.Text = $"正在展开 {p.Done}/{p.Total}：{p.Name}";
            });

            var count = Payload.ExtractTo(_installDirectory, progress);
            _detail.Text = $"展开 {count} 个文件，正在创建快捷方式…";
            Application.DoEvents();

            var appExe = Path.Combine(_installDirectory, "CutMap.App.exe");
            if (!File.Exists(appExe))
                throw new FileNotFoundException("安装包里没有 CutMap.App.exe", appExe);

            // 把安装器自己复制进安装目录：卸载项要指向一个稳定的路径。
            // 指向"用户当初双击的那个文件"是不行的——很多人装完就把安装包删了。
            var setupInInstallDir = Path.Combine(_installDirectory, "CutMap-Setup.exe");
            File.Copy(Environment.ProcessPath!, setupInInstallDir, overwrite: true);

            ShellIntegration.CreateShortcut(ShellIntegration.StartMenuShortcut, appExe, _installDirectory);
            ShellIntegration.CreateShortcut(ShellIntegration.DesktopShortcut, appExe, _installDirectory);
            ShellIntegration.RegisterUninstall(_installDirectory, setupInInstallDir, TotalBytes(_installDirectory));

            _progress.Value = _progress.Maximum;
            _detail.ForeColor = Color.DarkGreen;
            _detail.Text = "安装完成。";

            if (_launch.Checked)
            {
                Process.Start(new ProcessStartInfo(appExe) { WorkingDirectory = _installDirectory });
            }

            _installed = true;
            _action.Text = "完成";
            _action.Enabled = true;
            _close.Enabled = true;
        }
        catch (Exception ex)
        {
            _detail.ForeColor = Color.Firebrick;
            _detail.Text = "安装失败：" + ex.Message;
            MessageBox.Show(this,
                $"安装失败：\n\n{ex.Message}\n\n" +
                "若是权限或磁盘空间问题，清理后重试即可；已写入的文件可以手动删除。",
                "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _action.Enabled = true;
            _close.Enabled = true;
            _launch.Enabled = true;
        }
    }

    private static long TotalBytes(string directory)
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
