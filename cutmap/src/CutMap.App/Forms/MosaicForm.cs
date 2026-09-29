using System.Globalization;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.Core.Vector;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 拼接裁剪参数对话框（FR-01 多选与覆盖拦截 / FR-07 矩形模式 / FR-17 算法产物）。
///
/// 覆盖检查放在这里而不是任务执行时，是因为"输入盖不住目标"是**提交前就该知道**的事：
/// 让它跑完才发现，用户已经等了几分钟，而且产物是一片 nodata 瓦片——
/// CTB 不处理 nodata，那些值会被当作真实高程编码进去，前端看到的是平坦台地而不是空洞。
/// </summary>
public sealed class MosaicForm : ThemedForm
{
    private readonly ListView _inputs = new();
    private readonly Label _inputSummary = new();
    private readonly ComboBox _preset = new();
    private readonly TextBox[] _bounds = new TextBox[4];
    private readonly Label _coverage = new();
    private readonly TextBox _output = new();
    private readonly ComboBox _resampling = new();
    private readonly CheckBox _metadata = new();
    private readonly Button _ok = new();
    private readonly CheckBox _useBoundary = new();
    private readonly Button _pickBoundary = new();
    private readonly Label _boundarySummary = new();
    private readonly BoundaryStore _boundaryStore = BoundaryStore.Locate();
    private readonly PresetStore _presets = new();
    private IReadOnlyList<BoundaryFeature> _boundaries = [];

    private readonly List<RasterInfo> _infos = [];
    private bool _suppressPresetEvent;

    private static readonly string[] BoundLabels = ["西", "南", "东", "北"];

    public IReadOnlyList<string> InputPaths => _infos.Select(i => i.Path).ToArray();
    public string OutputPath => _output.Text.Trim();
    public BBox Target => new(
        Parse(_bounds[0]), Parse(_bounds[1]), Parse(_bounds[2]), Parse(_bounds[3]));
    public string Resampling => _resampling.SelectedItem?.ToString() ?? Mosaic.DefaultResampling;
    public bool WriteMetadata => _metadata.Checked;
    /// <summary>非空即走矢量模式。</summary>
    public IReadOnlyList<BoundaryFeature>? Boundaries =>
        _useBoundary.Checked && _boundaries.Count > 0 ? _boundaries : null;

    /// <param name="initialInputs">
    /// 预填的输入文件。用于「下载完这批 DEM → 继续处理」这条路径（FR-06），
    /// 免得用户刚下载完又得手动把 30 幅一个个选回来。
    /// </param>
    /// <param name="initialBoundaryCodes">
    /// 预填的行政区划编码（从历史重跑时用）。传编码而不是几何：
    /// 历史里只存了编码，几何从边界库现取——几何动辄几十万顶点，存进去会把历史撑爆。
    /// </param>
    /// <param name="preset">任务预设（FR-08）。给了就按它预填范围/重采样/元数据开关。</param>
    public MosaicForm(IReadOnlyList<string>? initialInputs = null, string? initialOutput = null,
                      IReadOnlyList<string>? initialBoundaryCodes = null,
                      TaskPreset? preset = null)
    {
        Text = "新建拼接裁剪任务";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(880, 640);
        MinimumSize = new Size(760, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        // 预设优先于内置的重庆范围：用户选了预设就是要那套参数。
        ApplyPreset(preset?.ClipBox is { } presetBox
            ? new ClipPreset { Id = "preset", Name = "预设", Bounds = [presetBox.West, presetBox.South, presetBox.East, presetBox.North] }
            : PresetStore.Chongqing);
        if (preset is not null)
        {
            _resampling.SelectedItem = preset.Resampling;
            _metadata.Checked = preset.WriteMetadata;
            if (preset.ClipBox is null && preset.BoundaryCodes is { Length: > 0 })
                _useBoundary.Checked = true;
        }

        foreach (var path in initialInputs ?? [])
        {
            try
            {
                _infos.Add(RasterInfo.Read(path));
            }
            catch (RasterException)
            {
                // 预填路径里有读不了的，跳过即可——用户还能手动添加。
            }
        }
        if (initialOutput is not null) _output.Text = initialOutput;

        if (initialBoundaryCodes is { Count: > 0 })
        {
            var resolved = initialBoundaryCodes
                .Select(code => _boundaryStore.ByCode(code))
                .OfType<BoundaryFeature>()
                .ToArray();
            if (resolved.Length > 0)
            {
                _boundaries = resolved;
                _useBoundary.Checked = true;
            }
        }

        if (_infos.Count > 0 && string.IsNullOrWhiteSpace(_output.Text))
        {
            var first = Path.GetDirectoryName(_infos[0].Path) ?? "";
            _output.Text = Path.Combine(first, "dem_clipped.tif");
        }

        RefreshInputs();
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
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 输入列表 ----
        _inputs.View = View.Details;
        _inputs.FullRowSelect = true;
        _inputs.GridLines = true;
        _inputs.Dock = DockStyle.Fill;
        _inputs.Columns.Add("文件", 150);
        _inputs.Columns.Add("尺寸", 90);
        _inputs.Columns.Add("坐标系", 80);
        _inputs.Columns.Add("像元(角秒)", 80);
        _inputs.Columns.Add("范围（像素中心口径）", 380);

        var inputButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true };
        var add = new Button { Text = "添加 DEM…", Width = 96 };
        add.Click += (_, _) => AddInputs();
        var remove = new Button { Text = "移除选中", Width = 88 };
        remove.Click += (_, _) => RemoveSelected();
        var clear = new Button { Text = "清空", Width = 64 };
        clear.Click += (_, _) => { _infos.Clear(); RefreshInputs(); };
        inputButtons.Controls.Add(add);
        inputButtons.Controls.Add(remove);
        inputButtons.Controls.Add(clear);

        var inputPanel = new Panel { Dock = DockStyle.Fill };
        inputPanel.Controls.Add(_inputs);
        inputPanel.Controls.Add(inputButtons);

        var inputBox = new GroupBox { Text = "输入 DEM（可多选，多幅先拼接再裁剪）", Dock = DockStyle.Fill };
        inputBox.Controls.Add(inputPanel);
        root.Controls.Add(inputBox, 0, 0);

        _inputSummary.Dock = DockStyle.Fill;
        _inputSummary.Height = 22;
        _inputSummary.ForeColor = SystemColors.GrayText;
        _inputSummary.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_inputSummary, 0, 1);

        // ---- 裁剪范围 ----
        var rangeBox = new GroupBox { Text = "裁剪范围（WGS84 经纬度）", Dock = DockStyle.Fill, Height = 96 };
        var range = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 2, Padding = new Padding(8) };

        range.Controls.Add(new Label { Text = "预设：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.Width = 200;
        foreach (var p in _presets.ClipPresets) _preset.Items.Add(p);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressPresetEvent) return;
            if (_preset.SelectedItem is ClipPreset p) ApplyPreset(p);
        };
        range.Controls.Add(_preset, 1, 0);
        var managePresets = new Button { Text = "管理…", Width = 64 };
        managePresets.Click += (_, _) => ManagePresets();
        range.Controls.Add(managePresets, 3, 0);
        range.SetColumnSpan(_preset, 2);

        for (var i = 0; i < 4; i++)
        {
            range.Controls.Add(
                new Label { Text = BoundLabels[i], AutoSize = true, Anchor = AnchorStyles.Left }, i + 2, 0);
            var box = new TextBox { Width = 96, Text = "0" };
            box.TextChanged += (_, _) => UpdateCoverage();
            _bounds[i] = box;
            range.Controls.Add(box, i + 2, 1);
        }

        _useBoundary.Text = "按行政区划边界裁剪";
        _useBoundary.AutoSize = true;
        _useBoundary.CheckedChanged += (_, _) => UpdateEnabled();
        range.Controls.Add(_useBoundary, 0, 2);
        range.SetColumnSpan(_useBoundary, 2);

        _pickBoundary.Text = "选择边界…";
        _pickBoundary.Width = 96;
        _pickBoundary.Click += (_, _) => PickBoundary();
        range.Controls.Add(_pickBoundary, 2, 2);
        range.Controls.Add(_boundarySummary, 3, 2);
        range.SetColumnSpan(_boundarySummary, 4);
        _boundarySummary.AutoEllipsis = true;
        _boundarySummary.ForeColor = SystemColors.GrayText;

        for (var c = 0; c < 7; c++) range.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        range.RowCount = 3;
        rangeBox.Height = 128;
        rangeBox.Controls.Add(range);
        root.Controls.Add(rangeBox, 0, 2);

        // ---- 输出与选项 ----
        var outputBox = new GroupBox { Text = "输出", Dock = DockStyle.Fill, Height = 132 };
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, Padding = new Padding(8) };

        output.Controls.Add(new Label { Text = "输出文件：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _output.Dock = DockStyle.Fill;
        output.Controls.Add(_output, 1, 0);
        output.SetColumnSpan(_output, 2);
        var browse = new Button { Text = "浏览…", Width = 80 };
        browse.Click += (_, _) => BrowseOutput();
        output.Controls.Add(browse, 3, 0);

        output.Controls.Add(new Label { Text = "重采样：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _resampling.DropDownStyle = ComboBoxStyle.DropDownList;
        _resampling.Width = 140;
        _resampling.Items.AddRange(["bilinear", "near", "cubic", "cubicspline", "lanczos", "average"]);
        _resampling.SelectedIndex = 0;
        output.Controls.Add(_resampling, 1, 1);

        _metadata.Text = "同时输出算法元数据 json";
        _metadata.Checked = true;
        _metadata.AutoSize = true;
        output.Controls.Add(_metadata, 2, 1);
        output.SetColumnSpan(_metadata, 2);

        _coverage.Dock = DockStyle.Fill;
        _coverage.AutoEllipsis = true;
        output.Controls.Add(_coverage, 0, 2);
        output.SetColumnSpan(_coverage, 4);

        outputBox.Controls.Add(output);
        root.Controls.Add(outputBox, 0, 3);

        // ---- 按钮 ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84 };
        Theme.MarkPrimary(_ok);
        _ok.Text = "开始";
        _ok.Width = 96;
        _ok.Enabled = false;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    // ---------------------------------------------------------------- 输入

    private void AddInputs()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 DEM（可多选）",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        foreach (var path in dialog.FileNames.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (_infos.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                _infos.Add(RasterInfo.Read(path));
            }
            catch (RasterException ex)
            {
                MessageBox.Show(this, ex.Message, "读不了这个文件",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        RefreshInputs();
        UpdateEnabled();
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _inputs.SelectedItems)
            if (item.Tag is RasterInfo info) _infos.Remove(info);
        RefreshInputs();
        UpdateEnabled();
    }

    private void RefreshInputs()
    {
        _inputs.BeginUpdate();
        _inputs.Items.Clear();
        foreach (var info in _infos)
        {
            var ext = info.PixelCentreExtent;
            var item = new ListViewItem(Path.GetFileName(info.Path)) { Tag = info };
            item.SubItems.Add($"{info.Width}×{info.Height}");
            item.SubItems.Add(info.EpsgCode is { } code ? $"EPSG:{code}" : "无/未识别");
            item.SubItems.Add((info.PixelSizeX * 3600).ToString("F3", CultureInfo.InvariantCulture));
            item.SubItems.Add(
                $"{ext.West:F6}, {ext.South:F6}, {ext.East:F6}, {ext.North:F6}");
            _inputs.Items.Add(item);
        }
        _inputs.EndUpdate();

        _inputSummary.Text = _infos.Count == 0
            ? "尚未选择输入。"
            : $"共 {_infos.Count} 幅，合计 {_infos.Sum(i => i.FileBytes) / (1024.0 * 1024 * 1024):F2} GB";
        UpdateCoverage();
    }

    // ---------------------------------------------------------------- 范围

    private void ApplyPreset(ClipPreset preset)
    {
        _suppressPresetEvent = true;
        _bounds[0].Text = preset.Box.West.ToString(CultureInfo.InvariantCulture);
        _bounds[1].Text = preset.Box.South.ToString(CultureInfo.InvariantCulture);
        _bounds[2].Text = preset.Box.East.ToString(CultureInfo.InvariantCulture);
        _bounds[3].Text = preset.Box.North.ToString(CultureInfo.InvariantCulture);
        _suppressPresetEvent = false;
        UpdateCoverage();
    }

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : double.NaN;

    private void BrowseOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "选择输出 GeoTIFF",
            Filter = "GeoTIFF (*.tif)|*.tif",
            FileName = "dem_clipped.tif",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.FileName;
    }

    /// <summary>
    /// 输入或范围一变就重查。FR-01 要求"提交前报错并说明差在哪"，所以这里
    /// 必须给出缺口的大小与方位，而不是一句"覆盖不足"。
    /// </summary>
    private void UpdateCoverage()
    {
        if (_infos.Count == 0)
        {
            SetCoverage("请先添加输入 DEM。", Color.Gray);
            _ok.Enabled = false;
            return;
        }

        // 矢量模式下真正的裁剪范围是选中边界的并集，不是右边那四个框——
        // 用矩形框去做覆盖检查会检查错对象。
        var target = Boundaries is { Count: > 0 } picked
            ? BoundaryExport.UnionBounds(picked)
            : Target;

        // "填了没有"只在矩形模式下问：矢量模式的范围来自几何，不存在没填这回事。
        if (Boundaries is null &&
            (double.IsNaN(target.West) || double.IsNaN(target.South) ||
             double.IsNaN(target.East) || double.IsNaN(target.North)))
        {
            SetCoverage("裁剪范围要填四个数：西 南 东 北。", Color.Firebrick);
            _ok.Enabled = false;
            return;
        }
        if (!target.IsValid(out var invalid))
        {
            SetCoverage(invalid, Color.Firebrick);
            _ok.Enabled = false;
            return;
        }

        var coverage = Coverage.Check(_infos, target);
        if (!coverage.Covered)
        {
            SetCoverage("输入盖不住目标：" + coverage.Describe(), Color.Firebrick);
            _ok.Enabled = false;
            return;
        }

        SetCoverage($"覆盖检查通过。{coverage.Describe()}", Color.DarkGreen);
        _ok.Enabled = !string.IsNullOrWhiteSpace(_output.Text);
    }

    private void ManagePresets()
    {
        using var dialog = new PresetForm(_presets);
        dialog.ShowDialog(this);

        // 预设可能被增删改过，重建下拉并尽量保持当前选择。
        var previousId = (_preset.SelectedItem as ClipPreset)?.Id;
        _preset.Items.Clear();
        foreach (var p in _presets.ClipPresets) _preset.Items.Add(p);
        var restored = _presets.ClipPresets.FirstOrDefault(p => p.Id == previousId);
        _preset.SelectedIndex = restored is not null ? _preset.Items.IndexOf(restored) : 0;
    }

    private void PickBoundary()
    {
        if (_boundaryStore.IsEmpty)
        {
            MessageBox.Show(this, _boundaryStore.Describe(), "没有边界数据",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var picker = new BoundaryPickerForm(_boundaryStore);
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _boundaries = picker.Selected;
        _useBoundary.Checked = true;
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        var vector = _useBoundary.Checked;
        foreach (var box in _bounds) box.Enabled = !vector;
        _pickBoundary.Enabled = vector;

        if (!vector)
        {
            _boundarySummary.Text = "";
        }
        else
        {
            _boundarySummary.Text = _boundaries.Count == 0
                ? "尚未选择区域。"
                : "已选 " + string.Join("、", _boundaries.Select(b => b.Name)) +
                  $"（{BoundaryExport.UnionBounds(_boundaries)}）";
        }
        UpdateCoverage();
    }

    private void SetCoverage(string text, Color color)
    {
        _coverage.Text = text;
        _coverage.ForeColor = color;
    }
}
