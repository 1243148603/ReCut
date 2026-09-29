using CutMap.Core.Terrain;

namespace CutMap.App.Forms;

/// <summary>
/// 打包 / 导出参数对话框（FR-03 layer.json 编辑 / FR-05 产物管理 / FR-18 双产物）。
///
/// 两个"要不要这套产物"的勾与两个"要不要 zip"的勾**互相独立**：
/// 只勾算法产物时切片可以根本不跑（"我只要给算法的数据"），
/// 只勾前端产物时不写 json（"我不想产物里混进算法侧的文件"）。
/// </summary>
public sealed class PackageForm : Form
{
    private readonly CheckBox _wantTiles = new();
    private readonly CheckBox _wantAlgo = new();
    private readonly TextBox _tilesDir = new();
    private readonly TextBox _tifPath = new();
    private readonly CheckBox _zipTiles = new();
    private readonly CheckBox _zipAlgo = new();
    private readonly TextBox _layerName = new();
    private readonly TextBox _layerDescription = new();
    private readonly TextBox _layerVersion = new();
    private readonly TextBox _output = new();
    private readonly Label _hint = new();
    private readonly Button _ok = new();

    public string? TilesDirectory => _wantTiles.Checked ? NullIfBlank(_tilesDir.Text) : null;
    public string? AlgoTifPath => _wantAlgo.Checked ? NullIfBlank(_tifPath.Text) : null;
    public bool ZipTiles => _wantTiles.Checked && _zipTiles.Checked;
    public bool ZipAlgo => _wantAlgo.Checked && _zipAlgo.Checked;
    public string LayerName => Fallback(_layerName.Text, "terrain");
    public string LayerDescription => Fallback(_layerDescription.Text, "Cesium terrain tiles");
    public string LayerVersion => Fallback(_layerVersion.Text, "1.0.0");
    public string OutputDirectory => _output.Text.Trim();

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Fallback(string s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

    /// <param name="initialTiles">从历史重跑时预填的瓦片目录。</param>
    /// <param name="initialTif">预填的裁剪 GeoTIFF。</param>
    /// <param name="initialOutput">预填的 zip 输出目录。</param>
    /// <param name="preset">任务预设（FR-08）。带出 layer.json 字段与 zip 开关。</param>
    public PackageForm(string? initialTiles = null, string? initialTif = null,
                       string? initialOutput = null,
                       CutMap.Core.Store.TaskPreset? preset = null)
    {
        Text = "打包 / 导出";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(640, 500);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        if (initialTiles is not null) { _tilesDir.Text = initialTiles; _wantTiles.Checked = true; }
        if (initialTif is not null) { _tifPath.Text = initialTif; _wantAlgo.Checked = true; }
        if (initialOutput is not null) _output.Text = initialOutput;

        if (preset is not null)
        {
            _layerName.Text = preset.LayerName;
            _layerDescription.Text = preset.LayerDescription;
            _layerVersion.Text = preset.LayerVersion;
            _zipTiles.Checked = preset.ZipTiles;
            _zipAlgo.Checked = preset.ZipAlgo;
        }
        UpdateEnabled();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 要哪几套产物 ----
        var choose = new GroupBox { Text = "要哪些产物（FR-18）", Dock = DockStyle.Fill, Height = 132 };
        var chooseLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(8),
        };
        chooseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        chooseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chooseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        _wantTiles.Text = "前端产物：瓦片目录 + layer.json";
        _wantTiles.AutoSize = true;
        _wantTiles.Checked = true;
        _wantTiles.CheckedChanged += (_, _) => UpdateEnabled();
        _zipTiles.Text = "同时打 zip";
        _zipTiles.AutoSize = true;
        _zipTiles.CheckedChanged += (_, _) => UpdateEnabled();
        chooseLayout.Controls.Add(_wantTiles, 0, 0);
        chooseLayout.SetColumnSpan(_wantTiles, 2);
        chooseLayout.Controls.Add(_zipTiles, 2, 0);

        chooseLayout.Controls.Add(new Label { Text = "瓦片目录：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _tilesDir.Dock = DockStyle.Fill;
        chooseLayout.Controls.Add(_tilesDir, 1, 1);
        var browseTiles = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browseTiles.Click += (_, _) => BrowseFolder(_tilesDir, "选择已有的瓦片目录");
        chooseLayout.Controls.Add(browseTiles, 2, 1);

        _wantAlgo.Text = "算法产物：裁剪 GeoTIFF + 元数据 json";
        _wantAlgo.AutoSize = true;
        _wantAlgo.CheckedChanged += (_, _) => UpdateEnabled();
        _zipAlgo.Text = "同时打 zip";
        _zipAlgo.AutoSize = true;
        _zipAlgo.CheckedChanged += (_, _) => UpdateEnabled();
        chooseLayout.Controls.Add(_wantAlgo, 0, 2);
        chooseLayout.SetColumnSpan(_wantAlgo, 2);
        chooseLayout.Controls.Add(_zipAlgo, 2, 2);

        chooseLayout.Controls.Add(new Label { Text = "裁剪 GeoTIFF：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _tifPath.Dock = DockStyle.Fill;
        chooseLayout.Controls.Add(_tifPath, 1, 3);
        var browseTif = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browseTif.Click += (_, _) => BrowseFile(_tifPath);
        chooseLayout.Controls.Add(browseTif, 2, 3);

        choose.Controls.Add(chooseLayout);
        root.Controls.Add(choose, 0, 0);

        // ---- layer.json 字段 ----
        var layerBox = new GroupBox { Text = "layer.json（FR-03）", Dock = DockStyle.Fill, Height = 96 };
        var layerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Padding = new Padding(8) };
        layerLayout.Controls.Add(new Label { Text = "name：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _layerName.Text = "terrain";
        layerLayout.Controls.Add(_layerName, 1, 0);
        layerLayout.Controls.Add(new Label { Text = "version：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        _layerVersion.Text = "1.0.0";
        _layerVersion.Width = 80;
        layerLayout.Controls.Add(_layerVersion, 3, 0);
        layerLayout.Controls.Add(new Label { Text = "description：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _layerDescription.Text = "Cesium terrain tiles";
        layerLayout.Controls.Add(_layerDescription, 1, 1);
        layerLayout.SetColumnSpan(_layerDescription, 3);

        var formatNote = new Label
        {
            Text = "format=heightmap-1.0、scheme=tms、扩展名小写 watermask 是格式要求，不开放编辑。",
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = SystemColors.GrayText,
        };
        layerBox.Controls.Add(layerLayout);
        layerBox.Controls.Add(formatNote);

        root.Controls.Add(layerBox, 0, 1);

        // ---- 输出 ----
        var outRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        outRow.Controls.Add(new Label { Text = "zip 输出到：", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        _output.Width = 440;
        outRow.Controls.Add(_output);
        var browseOut = new Button { Text = "浏览…", Width = 76 };
        browseOut.Click += (_, _) => BrowseFolder(_output, "选择 zip 输出目录");
        outRow.Controls.Add(browseOut);
        root.Controls.Add(outRow, 2, 0);

        _hint.Dock = DockStyle.Fill;
        _hint.AutoEllipsis = true;
        root.Controls.Add(_hint, 0, 3);

        // ---- 按钮 ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84 };
        _ok.Text = "开始打包";
        _ok.Width = 96;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;

        _tilesDir.TextChanged += (_, _) => UpdateEnabled();
        _tifPath.TextChanged += (_, _) => UpdateEnabled();
        _output.TextChanged += (_, _) => UpdateEnabled();
    }

    private static void BrowseFolder(TextBox target, string title)
    {
        using var dialog = new FolderBrowserDialog { Description = title };
        if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private static void BrowseFile(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择裁剪后的 GeoTIFF",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.FileName;
    }

    private void UpdateEnabled()
    {
        _tilesDir.Enabled = _wantTiles.Checked;
        _zipTiles.Enabled = _wantTiles.Checked;
        _tifPath.Enabled = _wantAlgo.Checked;
        _zipAlgo.Enabled = _wantAlgo.Checked;

        var tilesReady = !_wantTiles.Checked || Directory.Exists(_tilesDir.Text.Trim());
        var algoReady = !_wantAlgo.Checked || File.Exists(_tifPath.Text.Trim());
        var outputReady = !string.IsNullOrWhiteSpace(_output.Text);

        var problems = new List<string>();
        if (_wantTiles.Checked && !tilesReady) problems.Add("瓦片目录不存在");
        if (_wantAlgo.Checked && !algoReady) problems.Add("裁剪 GeoTIFF 不存在");
        if (!_wantTiles.Checked && !_wantAlgo.Checked) problems.Add("至少选一套产物");
        if ((_zipTiles.Checked || _zipAlgo.Checked) && !outputReady) problems.Add("zip 要指定输出目录");

        // 不打 zip 时不需要输出目录——只写 layer.json / 部署说明的话，落点就是瓦片目录本身。
        _ok.Enabled = problems.Count == 0;
        _hint.Text = problems.Count == 0
            ? (_wantTiles.Checked ? "会在瓦片目录里写入 layer.json 与 README.txt（部署说明）。" : "只产出算法数据，不写 layer.json。")
            : string.Join("；", problems) + "。";
        _hint.ForeColor = problems.Count == 0 ? SystemColors.GrayText : Color.Firebrick;
    }
}
