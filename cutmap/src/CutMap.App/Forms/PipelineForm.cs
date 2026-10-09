using System.Globalization;
using CutMap.Core.Geo;
using CutMap.Core.Vector;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 「一键生产」：把 拼接裁剪 → 切片 → 打包 三段的参数收在一个对话框里，
/// 确定后由主窗口按序入队、串行跑完；链路里任一环失败，剩下的自动跳过。
///
/// **为什么不是三次对话框**：三段的输入/输出是一条直线
/// （dem_clipped.tif → cesium_terrain → layer.json/zip），让用户手填三次既啰嗦
/// 又容易把下游路径写错——而且错要到跑完才暴露。这里只让用户定
/// 「输入什么、裁到哪、产物放哪」，中间产物路径由程序推导并在界面上列出来。
/// </summary>
public sealed class PipelineForm : ThemedForm
{
    private const string TifName = "dem_clipped.tif";
    private const string TilesName = "cesium_terrain";

    private readonly ListView _inputs = new();
    private readonly List<RasterInfo> _infos = [];

    private readonly TextBox[] _bounds = new TextBox[4];
    private readonly Button _fillFromInputs = new();
    private readonly CheckBox _useBoundary = new();
    private readonly Button _pickBoundary = new();
    private readonly Label _boundarySummary = new();
    // **惰性**：构造时解析 25 MB 边界 GeoJSON 会让「打开窗口」卡 10 秒
    //（实测 10.8 s vs 0.8 s）。改成用到才取——配合 MainForm 启动时的后台预热，
    // 真到用时命中的是缓存。
    private BoundaryStore? _boundaryStoreLazy;
    private BoundaryStore BoundaryData => _boundaryStoreLazy ??= BoundaryStore.Locate();
    private IReadOnlyList<BoundaryFeature> _boundaries = [];

    private readonly TextBox _root = new();
    private readonly TextBox _layerName = new();
    private readonly TextBox _layerDescription = new();
    private readonly TextBox _layerVersion = new();
    private readonly CheckBox _zipTiles = new();
    private readonly CheckBox _zipAlgo = new();
    private readonly Label _plan = new();
    private readonly Button _ok = new();

    private static readonly string[] BoundLabels = ["西", "南", "东", "北"];

    public IReadOnlyList<string> InputPaths => _infos.Select(i => i.Path).ToArray();
    public BBox Target => new(Parse(_bounds[0]), Parse(_bounds[1]), Parse(_bounds[2]), Parse(_bounds[3]));
    public IReadOnlyList<BoundaryFeature>? Boundaries =>
        _useBoundary.Checked && _boundaries.Count > 0 ? _boundaries : null;
    public string RootDirectory => _root.Text.Trim();

    /// <summary>中间产物路径由根目录推导——界面上会把这三步列出来，用户改根目录即可。</summary>
    public string AlgoTifPath => Path.Combine(RootDirectory, TifName);
    public string TilesDirectory => Path.Combine(RootDirectory, TilesName);

    public bool ZipTiles => _zipTiles.Checked;
    public bool ZipAlgo => _zipAlgo.Checked;
    public string LayerName => Fallback(_layerName.Text, "terrain");
    public string LayerDescription => Fallback(_layerDescription.Text, "Cesium terrain tiles");
    public string LayerVersion => Fallback(_layerVersion.Text, "1.0.0");

    private static string Fallback(string text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    public PipelineForm()
    {
        Text = "一键生产（拼接裁剪 → 切片 → 打包）";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        // 高度按内容兜底：计划那段是随输入变化的多行文字，写死高度会把它裁掉。
        var root = BuildLayout();
        UpdateBoundarySummary();   // 先给区域摘要一段非空文字，避免出现 0 宽的 AutoSize 标签
        UpdatePlan();
        FormLayout.FitToContent(this, new Size(900, Math.Max(620, root.PreferredSize.Height + 16)));
    }

    private TableLayoutPanel BuildLayout()
    {
        var root = FormLayout.Root();
        // 四行都显式声明成 AutoSize：靠 GrowStyle 自动长出来的行会和 Dock=Fill 的
        // 分组框互相试探，结果行被撑高、分组框里留下大片空白（实测多出 150px）。
        root.RowCount = 4;
        for (var i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 输入 ----
        _inputs.View = View.Details;
        _inputs.FullRowSelect = true;
        _inputs.GridLines = true;
        _inputs.Dock = DockStyle.Fill;
        _inputs.Columns.Add("文件", 190);
        _inputs.Columns.Add("尺寸", 90);
        _inputs.Columns.Add("坐标系", 80);
        _inputs.Columns.Add("范围（GDAL 外边缘）", 360);

        var inputButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        var addFiles = new Button { Text = "添加 DEM…", Width = 92 };
        addFiles.Click += (_, _) => AddFiles();
        var addFolder = new Button { Text = "添加目录…", Width = 96 };
        addFolder.Click += (_, _) => AddFolder();
        var remove = new Button { Text = "移除选中", Width = 84 };
        remove.Click += (_, _) => RemoveSelected();
        var clear = new Button { Text = "清空", Width = 60 };
        clear.Click += (_, _) => { _infos.Clear(); RefreshInputs(); UpdatePlan(); };
        inputButtons.Controls.Add(addFiles);
        inputButtons.Controls.Add(addFolder);
        inputButtons.Controls.Add(remove);
        inputButtons.Controls.Add(clear);

        var inputPanel = new Panel { Dock = DockStyle.Fill, Height = 160 };
        // 顺序不能动：填充的先加、边条的后加（WinForms 从后往前停靠）。
        inputPanel.Controls.Add(_inputs);
        inputPanel.Controls.Add(inputButtons);
        root.Controls.Add(FormLayout.Section("输入 DEM（多幅自动拼接）", inputPanel), 0, 0);

        // ---- 裁剪范围 ----
        for (var i = 0; i < 4; i++)
        {
            var box = new TextBox { Width = 96 };
            box.TextChanged += (_, _) => UpdatePlan();
            _bounds[i] = box;
        }

        _fillFromInputs.Text = "用输入范围";
        _fillFromInputs.Width = 104;
        _fillFromInputs.Click += (_, _) => FillFromInputs();
        _pickBoundary.Text = "选择边界…";
        _pickBoundary.Width = 104;
        _pickBoundary.Click += (_, _) => PickBoundary();
        _boundarySummary.AutoSize = true;
        _boundarySummary.ForeColor = SystemColors.GrayText;
        _boundarySummary.Margin = new Padding(12, 8, 0, 0);

        // 行一律用 FlowLayoutPanel（AutoSize）：嵌套的 Dock=Fill 网格放进 AutoSize 行里
        // 量不出宽度，输入框会被挤成一条（第一版就是这么坏的）。
        var sourceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        sourceButtons.Controls.Add(_fillFromInputs);
        sourceButtons.Controls.Add(_pickBoundary);
        sourceButtons.Controls.Add(_boundarySummary);

        _useBoundary.Text = "按行政区划裁剪";
        _useBoundary.AutoSize = true;
        _useBoundary.CheckedChanged += (_, _) => { UpdateBoundarySummary(); UpdatePlan(); };
        var boundaryNote = new Label
        {
            AutoSize = true,
            Text = "（勾选后按边界裁、边界外补平坦裙边；不勾就是矩形）",
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 0, 0),
        };
        var boundaryRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        boundaryRow.Controls.Add(_useBoundary);
        boundaryRow.Controls.Add(boundaryNote);

        // 三段都横排、纵向依次堆叠：用 TopDown 的 FlowLayoutPanel 而不是表格，
        // 高度完全由内容决定（表格 + Dock=Fill 的分组框会互相试探，留下空白）。
        var boundsRow = FormLayout.BoundsRow(_bounds, BoundLabels);
        boundsRow.Dock = DockStyle.None;
        boundsRow.Margin = new Padding(0);
        var rangeGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        rangeGrid.Controls.Add(boundsRow);
        rangeGrid.Controls.Add(sourceButtons);
        rangeGrid.Controls.Add(boundaryRow);
        root.Controls.Add(FormLayout.Section("裁剪范围", rangeGrid), 0, 1);

        // ---- 输出与 layer.json ----
        foreach (var box in new[] { _root, _layerName, _layerDescription }) box.Width = 430;
        var outGrid = FormLayout.Grid(FormLayout.Fixed(96), FormLayout.Fixed(440), FormLayout.Fixed(84));
        outGrid.AddRow(FormLayout.FieldLabel("根目录：", 90), _root, BrowseButton());
        outGrid.AddRow(FormLayout.FieldLabel("name：", 90), _layerName, null);
        outGrid.AddRow(FormLayout.FieldLabel("description：", 90), _layerDescription, null);
        outGrid.AddRow(FormLayout.FieldLabel("version：", 90), VersionZipRow(), null);
        outGrid.AddRowSpanning(_plan, 3);
        root.Controls.Add(FormLayout.Section("输出与 layer.json（中间产物路径自动推导）", outGrid), 0, 2);

        _layerName.Text = "terrain";
        _layerDescription.Text = "Cesium terrain tiles";
        _layerVersion.Text = "1.0.0";
        _layerName.TextChanged += (_, _) => UpdatePlan();
        _root.TextChanged += (_, _) => UpdatePlan();

        // ---- 按钮 ----
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        _ok.Text = "开始生产";
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        root.Controls.Add(FormLayout.ButtonRow(_ok, cancel), 0, 3);

        _plan.AutoSize = true;
        _plan.MaximumSize = new Size(860, 0);
        _plan.MinimumSize = new Size(0, 96);   // 预留计划文字的高度，内容多时不会顶掉下方按钮
        _plan.ForeColor = SystemColors.ControlText;

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;
        return root;
    }

    private Button BrowseButton()
    {
        var browse = new Button { Text = "浏览…", Width = 80 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择产物根目录" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _root.Text = dialog.SelectedPath;
        };
        return browse;
    }

    /// <summary>version 输入框与两个 zip 勾选合成一行（FlowLayoutPanel：宽度按内容，不会被挤）。</summary>
    private Control VersionZipRow()
    {
        _layerVersion.Width = 120;
        _zipTiles.Text = "瓦片 zip";
        _zipTiles.AutoSize = true;
        _zipTiles.Margin = new Padding(18, 5, 0, 0);
        _zipTiles.CheckedChanged += (_, _) => UpdatePlan();
        _zipAlgo.Text = "算法 zip";
        _zipAlgo.AutoSize = true;
        _zipAlgo.Margin = new Padding(14, 5, 0, 0);
        _zipAlgo.CheckedChanged += (_, _) => UpdatePlan();

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.Add(_layerVersion);
        row.Controls.Add(_zipTiles);
        row.Controls.Add(_zipAlgo);
        return row;
    }

    // ---------------------------------------------------------------- 输入

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 DEM（可多选）",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AddPaths(dialog.FileNames);
    }

    /// <summary>按目录添加：把目录下的 *.tif 全加进来（下载完一批后的常见操作）。</summary>
    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择含 DEM 分幅的目录" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var files = Directory.EnumerateFiles(dialog.SelectedPath, "*.tif", SearchOption.TopDirectoryOnly)
                             .OrderBy(p => p, StringComparer.Ordinal)
                             .ToArray();
        if (files.Length == 0)
        {
            MessageBox.Show(this, "这个目录下没有 *.tif。", "没找到 DEM",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AddPaths(files);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var failed = 0;
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (_infos.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                _infos.Add(RasterInfo.Read(path));
            }
            catch (RasterException)
            {
                failed++;   // 单个文件读不了不该打断整批添加
            }
        }
        RefreshInputs();
        UpdatePlan();
        if (failed > 0)
            AppendPlanNote($"{failed} 个文件读不了，已跳过");
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _inputs.SelectedItems)
            if (item.Tag is RasterInfo info) _infos.Remove(info);
        RefreshInputs();
        UpdatePlan();
    }

    private void RefreshInputs()
    {
        _inputs.BeginUpdate();
        _inputs.Items.Clear();
        foreach (var info in _infos.OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            var item = new ListViewItem(Path.GetFileName(info.Path)) { Tag = info };
            item.SubItems.Add($"{info.Width}×{info.Height}");
            item.SubItems.Add(info.EpsgCode is { } epsg ? $"EPSG:{epsg}" : "未识别");
            item.SubItems.Add(info.Extent.ToString());
            _inputs.Items.Add(item);
        }
        _inputs.EndUpdate();
    }

    private string? _note;

    private void AppendPlanNote(string note)
    {
        _note = note;
        UpdatePlan();
    }

    // ---------------------------------------------------------------- 范围

    private void FillFromInputs()
    {
        if (Coverage.Union(_infos) is not { } union)
        {
            AppendPlanNote("还没有输入 DEM，先添加输入文件。");
            return;
        }
        _boundingNoteClear();
        SetBounds(union);
    }

    private void _boundingNoteClear() => _note = null;

    private void SetBounds(BBox box)
    {
        _bounds[0].Text = box.West.ToString(CultureInfo.InvariantCulture);
        _bounds[1].Text = box.South.ToString(CultureInfo.InvariantCulture);
        _bounds[2].Text = box.East.ToString(CultureInfo.InvariantCulture);
        _bounds[3].Text = box.North.ToString(CultureInfo.InvariantCulture);
        UpdatePlan();
    }

    private void PickBoundary()
    {
        if (BoundaryData.IsEmpty)
        {
            MessageBox.Show(this, BoundaryData.Describe(), "没有边界数据",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var picker = new BoundaryPickerForm(BoundaryData);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _boundaries = picker.Selected;
        _useBoundary.Checked = true;
        UpdateBoundarySummary();
        UpdatePlan();
    }

    private void UpdateBoundarySummary()
    {
        // 永远给一段非空文字：AutoSize 的空标签宽度为 0，排版自检会报硬错误。
        _boundarySummary.Text = !_useBoundary.Checked
            ? "（未按行政区划）"
            : _boundaries.Count == 0
                ? "尚未选择区域。"
                : "已选 " + string.Join("、", _boundaries.Select(b => b.Name));
    }

    // ---------------------------------------------------------------- 计划

    private void UpdatePlan()
    {
        var problems = new List<string>();
        if (_infos.Count == 0) problems.Add("先添加输入 DEM");

        BBox target = default;
        if (Boundaries is { } picked)
        {
            target = BoundaryExport.UnionBounds(picked);
            if (target.IsValid(out _))
            {
                var coverage = Coverage.Check(_infos, target);
                if (_infos.Count > 0 && !coverage.Covered) problems.Add("输入盖不住目标：" + coverage.Describe());
            }
        }
        else
        {
            target = Target;
            if (double.IsNaN(target.West) || double.IsNaN(target.South) ||
                double.IsNaN(target.East) || double.IsNaN(target.North))
            {
                problems.Add("裁剪范围要填四个数，或点「用输入范围」/ 勾「按行政区划」");
            }
            else if (!target.IsValid(out var invalid))
            {
                problems.Add(invalid);
            }
            else if (_infos.Count > 0)
            {
                var coverage = Coverage.Check(_infos, target);
                if (!coverage.Covered) problems.Add("输入盖不住目标：" + coverage.Describe());
            }
        }

        if (string.IsNullOrWhiteSpace(RootDirectory)) problems.Add("选一个输出根目录");
        else if (RootDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) problems.Add("输出目录路径不合法");

        if (problems.Count > 0)
        {
            _plan.Text = "还不能开始：" + string.Join("；", problems)
                       + (_note is null ? "" : "\n· " + _note);
            _plan.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var lines = new List<string>
        {
            $"① 拼接裁剪（{(Boundaries is null ? "矩形" : "行政区划")}模式）→ {AlgoTifPath}",
            $"② 切片（层级自动）→ {TilesDirectory}",
            $"③ 打包 → layer.json{(ZipTiles ? " + 瓦片 zip" : "")}{(ZipAlgo ? " + 算法 zip" : "")}（在 {RootDirectory}）",
        };
        if (File.Exists(AlgoTifPath)) lines.Add("· 注意：已有同名 dem_clipped.tif，会被覆盖");
        if (Directory.Exists(TilesDirectory)) lines.Add("· 注意：已有 cesium_terrain 目录，里面的瓦片会被覆盖");
        if (_note is not null) lines.Add("· " + _note);

        _plan.Text = string.Join("\n", lines);
        _plan.ForeColor = SystemColors.ControlText;
        _ok.Enabled = true;
    }

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : double.NaN;
}
