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
        _installDirectory = InstallOperation.DefaultDirectory;

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
            _detail.Text = $"检测到另一处安装：{existing}\n本次会装到 {_installDirectory}，两者互不影响。";
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

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Continuous;
        var progressHost = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(20, 8, 20, 0) };
        progressHost.Controls.Add(_progress);

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
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                if (p.Total > 0)
                {
                    _progress.Maximum = p.Total;
                    _progress.Value = Math.Min(p.Done, p.Total);
                    _detail.Text = $"正在展开 {p.Done}/{p.Total}：{p.Name}";
                }
                else
                {
                    _detail.Text = p.Name + "…";
                }
                Application.DoEvents();
            });

            InstallOperation.Run(_installDirectory, progress, _launch.Checked);

            _progress.Value = _progress.Maximum;
            _detail.ForeColor = Color.DarkGreen;
            _detail.Text = "安装完成。";

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
}
