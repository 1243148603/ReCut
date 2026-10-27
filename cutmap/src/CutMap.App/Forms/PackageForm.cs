using CutMap.App.Ui;
using CutMap.Core.Store;

namespace CutMap.App.Forms;

/// <summary>
/// 打包 / 导出参数对话框（layer.json 编辑、产物管理、双产物）。
///
/// 两个"要不要这套产物"的勾与两个"要不要 zip"的勾**互相独立**：
/// 只勾算法产物时切片可以根本不跑（"我只要给算法的数据"），
/// 只勾前端产物时不写 json（"我不想产物里混进算法侧的文件"）。
///
/// 布局用 <see cref="FormLayout"/> 的工厂方法，容器一律 AutoSize——
/// 之前手填 Height 又加 Dock=Fill 的写法会让内容被压扁、裁掉、重叠。
/// </summary>
public sealed class PackageForm : ThemedForm
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
    private readonly TextBox _layerBounds = new();
    private readonly TextBox _layerZoom = new();
    private readonly TextBox _boundaryPath = new();
    public string? BoundaryGeoJsonPath => string.IsNullOrWhiteSpace(_boundaryPath.Text) ? null : _boundaryPath.Text.Trim();
    public CutMap.Core.Geo.BBox? LayerBounds => CutMap.Core.Geo.BBox.TryParse(_layerBounds.Text, out var b) ? b : null;
    public int? LayerMaxZoom => int.TryParse(_layerZoom.Text, out var z) ? z : null;
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
    /// <param name="preset">任务预设。带出 layer.json 字段与 zip 开关。</param>
    public PackageForm(string? initialTiles = null, string? initialTif = null,
                       string? initialOutput = null, TaskPreset? preset = null)
    {
        Text = "打包 / 导出";
        StartPosition = FormStartPosition.CenterParent;

        var root = BuildLayout();

        // **高度按内容兜底。** 写死 460 时最后那行按钮会探出窗体底边
        // （自检实测 666×466 > 680×460），按钮下缘被切掉一截。
        // 用根面板的首选高度定，内容以后再增减也不用回来改这个数字。
        FormLayout.FitToContent(this, new Size(680, Math.Max(460, root.PreferredSize.Height + 8)));

        // **校验要跟着输入走。** 原来只在勾选框变化时算一次，于是两种最常见的操作
        // 都会卡死：先勾选（路径还空着）再填路径、或者开着窗口等后台切片任务把
        // 瓦片目录写出来——两者之后都没有事件触发重算，提示停在"瓦片目录不存在"，
        // 开始打包永远点不亮（用户实际卡在这：目录已经在了，按钮还是灰的）。
        _tilesDir.TextChanged += (_, _) => UpdateEnabled();
        _tifPath.TextChanged += (_, _) => UpdateEnabled();
        _output.TextChanged += (_, _) => UpdateEnabled();
        foreach (var box in new[] { _layerBounds, _layerZoom, _boundaryPath }) box.TextChanged += (_, _) => UpdateEnabled();
        // 窗口开着的期间"世界"也会变：切片任务跑完，瓦片目录才出现。
        Activated += (_, _) => UpdateEnabled();

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

    private TableLayoutPanel BuildLayout()
    {
        var root = FormLayout.Root();

        // 勾选框自己的文字。四个框是两个独立开关 + 两个独立的"要不要打成 zip"，
        // 文案各自标清楚——**曾经这里一个 Text 都没设**，界面上只有四个空方框，
        // 用户根本不知道在勾什么（截图反馈："要哪些产物"下面全是空白）。
        _wantTiles.Text = "前端瓦片（Cesium 地形切片）";
        _zipTiles.Text = "打成 zip";
        _wantAlgo.Text = "算法数据（裁剪 GeoTIFF）";
        _zipAlgo.Text = "打成 zip";

        // ---- 要哪些产物 ----
        // 每行一个独立小网格、**不用跨列**。跨列（SetColumnSpan）跟"按顺序占列"
        // 混用时极易把两个控件放进同一格（实际踩过：勾选框压在旁边那个上面）。
        // 一行一个网格，列数各自定义，谁也不会撞到谁。
        var choose = FormLayout.Grid(FormLayout.Flexible());

        choose.AddRow(ToggleRow(_wantTiles, _zipTiles,
            () => { _wantTiles.Checked = true; UpdateEnabled(); }));
        choose.AddRow(PathRow("瓦片目录：", _tilesDir, () => BrowseFolder(_tilesDir, "选择已有的瓦片目录")));
        choose.AddRow(ToggleRow(_wantAlgo, _zipAlgo, UpdateEnabled));
        choose.AddRow(PathRow("裁剪 GeoTIFF：", _tifPath, () => BrowseFile(_tifPath)));

        root.Controls.Add(FormLayout.Section("要哪些产物", choose), 0, 0);

        // ---- layer.json ----
        var layer = FormLayout.Grid(FormLayout.Flexible());
        layer.AddRow(NameVersionRow());
        layer.AddRow(PathRow("description：", _layerDescription, null));
        layer.AddRow(PathRow("bounds：", _layerBounds, null));
        layer.AddRow(PathRow("maxzoom：", _layerZoom, null));
        layer.AddRow(PathRow("边界文件：", _boundaryPath, () =>
        {
            using var picker = new OpenFileDialog { Filter = "GeoJSON|*.geojson;*.json" };
            if (picker.ShowDialog(this) == DialogResult.OK) _boundaryPath.Text = picker.FileName;
        }));

        var note = Theme.Hint("format=heightmap-1.0、scheme=tms、扩展名小写 watermask 是格式要求，不开放编辑。");
        note.Margin = new Padding(0, 8, 0, 0);
        layer.AddRow(note);
        root.Controls.Add(FormLayout.Section("layer.json", layer), 0, 1);

        // ---- 输出 ----
        var output = FormLayout.Grid(FormLayout.Flexible());
        output.AddRow(PathRow("zip 输出到：", _output, () => BrowseFolder(_output, "选择 zip 输出目录")));
        root.Controls.Add(FormLayout.Section("输出", output), 0, 2);

        // ---- 提示与按钮 ----
        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(680, 0);
        _hint.Margin = new Padding(0, 2, 0, 0);
        root.Controls.Add(_hint, 0, 3);

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        _ok.Text = "开始打包";
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        root.Controls.Add(FormLayout.ButtonRow(_ok, cancel), 0, 4);

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;
        return root;
    }

    /// <summary>两个勾选框一行：左占满、右固定宽。</summary>
    private static Control ToggleRow(CheckBox main, CheckBox trailing, Action onToggle)
    {
        main.CheckedChanged += (_, _) => onToggle();
        trailing.CheckedChanged += (_, _) => onToggle();
        main.AutoSize = true;
        trailing.AutoSize = true;
        trailing.Margin = new Padding(8, 3, 0, 3);

        var row = FormLayout.Grid(FormLayout.Flexible(), FormLayout.Fixed(120));
        row.AddRow(main, trailing);
        return row;
    }

    /// <summary>标签 + 输入框 [+ 浏览] 一行。按钮传 null 就不放。</summary>
    private static Control PathRow(string label, TextBox input, Action? onBrowse)
    {
        input.Dock = DockStyle.Fill;
        if (onBrowse is null)
        {
            var two = FormLayout.Grid(FormLayout.Fixed(104), FormLayout.Flexible());
            two.AddRow(FormLayout.FieldLabel(label, 98), input);
            return two;
        }

        var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => onBrowse();
        var row = FormLayout.Grid(FormLayout.Fixed(104), FormLayout.Flexible(), FormLayout.Fixed(84));
        row.AddRow(FormLayout.FieldLabel(label, 98), input, browse);
        return row;
    }

    /// <summary>name 与 version 挤一行，各自带标签。</summary>
    private Control NameVersionRow()
    {
        var row = FormLayout.Grid(FormLayout.Fixed(104), FormLayout.Flexible(), FormLayout.Fixed(76), FormLayout.Fixed(120));
        row.AddRow(FormLayout.FieldLabel("name：", 98), _layerName,
                   FormLayout.FieldLabel("version：", 70), _layerVersion);
        return row;
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
        if (_wantTiles.Checked && !string.IsNullOrWhiteSpace(_layerBounds.Text) && LayerBounds is null) problems.Add("bounds 须为有效的西 南 东 北，或留空反推");
        if (_wantTiles.Checked && !string.IsNullOrWhiteSpace(_layerZoom.Text) && (LayerMaxZoom is null or < 0 or > 30)) problems.Add("maxzoom 须为 0–30 或留空反推");
        if (_wantTiles.Checked && BoundaryGeoJsonPath is { } boundary && !File.Exists(boundary)) problems.Add("边界文件不存在");
        if (_wantTiles.Checked && !tilesReady) problems.Add("瓦片目录不存在");
        if (_wantAlgo.Checked && !algoReady) problems.Add("裁剪 GeoTIFF 不存在");
        if (!_wantTiles.Checked && !_wantAlgo.Checked) problems.Add("至少选一套产物");
        // 不打 zip 时不需要输出目录——只写 layer.json / 部署说明的话，落点就是瓦片目录本身。
        if ((_zipTiles.Checked || _zipAlgo.Checked) && !outputReady) problems.Add("zip 要指定输出目录");

        _ok.Enabled = problems.Count == 0;
        Theme.SetStatus(_hint,
            problems.Count == 0
                ? (_wantTiles.Checked
                    ? "会在瓦片目录里写入 layer.json 与 README.txt（部署说明）。"
                    : "只产出算法数据，不写 layer.json。")
                : string.Join("；", problems) + "。",
            problems.Count == 0 ? Theme.StatusKind.Normal : Theme.StatusKind.Error);
    }

    public void ApplyHistory(System.Text.Json.JsonElement data)
    {
        string TextOf(string key) => data.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Flag(string key) => data.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        _layerName.Text = TextOf("layerName"); _layerDescription.Text = TextOf("layerDescription"); _layerVersion.Text = TextOf("layerVersion");
        _zipTiles.Checked = Flag("zipTiles"); _zipAlgo.Checked = Flag("zipAlgo"); _boundaryPath.Text = TextOf("boundary");
        if (data.TryGetProperty("layerMaxZoom", out var zoom) && zoom.ValueKind == System.Text.Json.JsonValueKind.Number) _layerZoom.Text = zoom.GetInt32().ToString();
        if (data.TryGetProperty("layerBounds", out var box) && box.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var values = new[] { "West", "South", "East", "North" }.Select(k => box.GetProperty(k).GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
            _layerBounds.Text = string.Join(" ", values);
        }
        UpdateEnabled();
    }
}
