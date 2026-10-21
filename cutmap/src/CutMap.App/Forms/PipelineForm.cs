using CutMap.App.Jobs;
using CutMap.App.Ui;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.Core.Terrain;
using CutMap.Core.Vector;

namespace CutMap.App.Forms;

public sealed class PipelineForm : ThemedForm
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _data = new("输入与步骤"), _clip = new("拼接裁剪"), _slice = new("切片"), _export = new("导出");
    private readonly List<RasterInfo> _infos = [];
    private readonly ListView _inputs = new() { View = View.Details, FullRowSelect = true, Height = 190, Width = 820 };
    private readonly CheckBox _mosaic = Toggle("拼接裁剪", true), _slicing = Toggle("切片", true), _packaging = Toggle("打包 / layer.json", true);
    private readonly CheckBox _tiles = Toggle("前端瓦片", true), _algo = Toggle("算法 GeoTIFF + 元数据", true), _boundary = Toggle("按行政边界裁剪");
    private readonly CheckBox _zipTiles = Toggle("瓦片 zip"), _zipAlgo = Toggle("算法 zip");
    private readonly TextBox _root = Box(), _existing = Box(), _bounds = Box(), _zoom = Box(), _layerBounds = Box(), _layerZoom = Box();
    private readonly TextBox _name = Box("terrain"), _description = Box("Cesium terrain tiles"), _version = Box("1.0.0");
    private readonly ComboBox _resampling = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _reference = new() { AutoSize = true, MaximumSize = new Size(780, 0), Text = "选择 DEM 后显示参考层级。" };
    private readonly Label _boundaryLabel = new() { Text = "未选边界", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(820, 0), Dock = DockStyle.Fill };
    private readonly Button _start = new() { Text = "开始执行", AutoSize = true };
    private IReadOnlyList<BoundaryFeature> _boundaries = [];
    public IReadOnlyList<Job> PlannedJobs { get; private set; } = [];

    public PipelineForm(TaskPreset? preset = null)
    {
        Text = "新建处理向导";
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.Resizable(this, new Size(940, 700), new Size(850, 600));
        _inputs.Columns.Add("完整路径", 380); _inputs.Columns.Add("坐标系 / 尺寸 / 分辨率", 420);
        Add(_data, _inputs, Row(Button("添加 DEM…", AddFiles), Button("移除选中", RemoveInputs)),
            Row(_mosaic, _slicing, _packaging), Row(_tiles, _algo),
            Field("输出根目录", _root, () => Browse(_root)),
            Field("已有瓦片（跳过切片时）", _existing, () => Browse(_existing)),
            Theme.Hint("跳过拼接时选单幅现成 DEM；跳过切片时指定已有瓦片。下载 DEM 从工具条进入。"));
        _resampling.Items.AddRange(["bilinear", "near", "cubic", "average"]); _resampling.SelectedIndex = 0;
        Add(_clip, Field("范围：西 南 东 北", _bounds),
            Row(Button("使用输入范围", () => { if (Coverage.Union(_infos) is { } b) _bounds.Text = string.Join(" ", b.ToTeArguments()); }),
                Button("重庆全域", () => _bounds.Text = "105.3 28.1 110.2 32.2")),
            Row(_boundary, Button("选择边界…", PickBoundary), _boundaryLabel), Field("重采样", _resampling));
        Add(_slice, Field("最大层级（留空自动）", _zoom), _reference);
        Add(_export, Row(_zipTiles, _zipAlgo), Field("name", _name), Field("description", _description), Field("version", _version),
            Field("layer bounds（留空反推）", _layerBounds), Field("layer maxzoom（留空反推）", _layerZoom),
            Theme.Hint("算法数据共用裁剪结果；未裁剪时保留原 TIFF，只生成实测元数据。"));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_tabs, 0, 0); root.Controls.Add(_status, 0, 1);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        root.Controls.Add(FormLayout.ButtonRow(_start, Button("下一步", () => _tabs.SelectedIndex = Math.Min(_tabs.TabCount - 1, _tabs.SelectedIndex + 1)),
            Button("上一步", () => _tabs.SelectedIndex = Math.Max(0, _tabs.SelectedIndex - 1)), cancel), 0, 2);
        Controls.Add(root); CancelButton = cancel;
        _start.Click += (_, _) => { try { PlannedJobs = Plan().CreateJobs(); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { _status.Text = ex.Message; } };
        foreach (var c in new[] { _mosaic, _slicing, _packaging, _tiles, _algo, _boundary, _zipTiles, _zipAlgo }) c.CheckedChanged += (_, _) => UpdatePlan();
        foreach (var c in new[] { _root, _existing, _bounds, _zoom, _layerBounds, _layerZoom, _name, _description, _version }) c.TextChanged += (_, _) => UpdatePlan();
        if (preset is not null)
        {
            _bounds.Text = preset.ClipBox is { } b ? string.Join(" ", b.ToTeArguments()) : "";
            _zoom.Text = preset.MaxZoom?.ToString() ?? ""; _resampling.SelectedItem = preset.Resampling;
            _name.Text = preset.LayerName; _description.Text = preset.LayerDescription; _version.Text = preset.LayerVersion;
            _zipTiles.Checked = preset.ZipTiles; _zipAlgo.Checked = preset.ZipAlgo;
            if (preset.BoundaryCodes is { Length: > 0 }) { _boundaries = BoundaryStore.Locate().All.Where(b => preset.BoundaryCodes.Contains(b.Code)).ToArray(); _boundary.Checked = _boundaries.Count > 0; }
        }
        UpdatePlan();
    }
    private static CheckBox Toggle(string text, bool check = false) => new() { Text = text, AutoSize = true, Checked = check };
    private static TextBox Box(string text = "") => new() { Text = text, Width = 440 };
    private static Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => action(); return b; }
    private static Control Row(params Control[] controls) { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 820 }; row.Controls.AddRange(controls); return row; }
    private static Control Field(string label, Control value, Action? browse = null)
    {
        var controls = new List<Control> { new Label { Text = label, Width = 220, AutoSize = false, Height = 28 }, value };
        if (browse is not null) controls.Add(Button("浏览…", browse));
        return Row(controls.ToArray());
    }
    private static void Add(TabPage page, params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        panel.Controls.AddRange(controls); page.Controls.Add(panel);
    }
    private void Browse(TextBox box) { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath; }
    private void AddFiles()
    {
        using var d = new OpenFileDialog { Filter = "GeoTIFF|*.tif;*.tiff", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in d.FileNames)
        {
            if (_infos.Any(i => i.Path == file)) continue;
            try { var info = RasterInfo.Read(file); _infos.Add(info); _inputs.Items.Add(new ListViewItem([file, info.Summary()]) { Tag = info }); }
            catch (RasterException ex) { MessageBox.Show(this, ex.Message, "无法读取 DEM"); }
        }
        if (string.IsNullOrWhiteSpace(_bounds.Text) && Coverage.Union(_infos) is { } b) _bounds.Text = string.Join(" ", b.ToTeArguments());
        UpdatePlan();
    }
    private void RemoveInputs() { foreach (ListViewItem item in _inputs.SelectedItems) { _infos.Remove((RasterInfo)item.Tag!); _inputs.Items.Remove(item); } UpdatePlan(); }
    private void PickBoundary()
    {
        using var picker = new BoundaryPickerForm(BoundaryStore.Locate());
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _boundaries = picker.Selected; _boundary.Checked = true;
        _boundaryLabel.Text = string.Join("、", _boundaries.Select(b => b.Name)); UpdatePlan();
    }
    private static int? Zoom(TextBox box)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        if (!int.TryParse(box.Text, out var n) || n < 0 || n > 30) throw new ArgumentException("层级应为 0–30 整数或留空。");
        return n;
    }
    private PipelinePlan Plan()
    {
        BBox target = default;
        if (_mosaic.Checked && !_boundary.Checked && !BBox.TryParse(_bounds.Text, out target)) throw new ArgumentException("请填写有效裁剪范围。");
        if (_mosaic.Checked && _boundary.Checked && _boundaries.Count == 0) throw new ArgumentException("请选择裁剪边界。");
        BBox? layer = null;
        if (_packaging.Checked && _tiles.Checked && !string.IsNullOrWhiteSpace(_layerBounds.Text))
        { if (!BBox.TryParse(_layerBounds.Text, out var b)) throw new ArgumentException("layer bounds 无效。"); layer = b; }
        return new PipelinePlan(_infos.Select(i => i.Path).ToArray(), _root.Text.Trim(), _mosaic.Checked, _slicing.Checked, _packaging.Checked,
            _tiles.Checked, _algo.Checked, target, _existing.Text.Trim(), _mosaic.Checked && _boundary.Checked ? _boundaries : null,
            _slicing.Checked ? Zoom(_zoom) : null, _resampling.SelectedItem?.ToString() ?? "bilinear", _zipTiles.Checked, _zipAlgo.Checked,
            _name.Text, _description.Text, _version.Text, layer, _packaging.Checked && _tiles.Checked ? Zoom(_layerZoom) : null, _infos);
    }
    private void UpdatePlan()
    {
        if (!_tiles.Checked && _slicing.Checked) _slicing.Checked = false;
        _slicing.Enabled = _tiles.Checked; _zipTiles.Enabled = _packaging.Checked && _tiles.Checked; _zipAlgo.Enabled = _packaging.Checked && _algo.Checked;
        var pages = new List<TabPage> { _data };
        if (_mosaic.Checked) pages.Add(_clip); if (_slicing.Checked) pages.Add(_slice); if (_packaging.Checked || _algo.Checked) pages.Add(_export);
        var current = _tabs.SelectedTab;
        if (!_tabs.TabPages.Cast<TabPage>().SequenceEqual(pages)) { _tabs.TabPages.Clear(); _tabs.TabPages.AddRange(pages.ToArray()); if (current is not null && pages.Contains(current)) _tabs.SelectedTab = current; }
        try { var jobs = Plan().CreateJobs(); _status.Text = string.Join(" → ", jobs.Select(j => j.TypeName + "：" + j.Display)); _status.ForeColor = SystemColors.ControlText; _start.Enabled = true; }
        catch (Exception ex) when (ex is ArgumentException or IOException or RasterException or InvalidOperationException) { _status.Text = ex.Message; _status.ForeColor = Color.Firebrick; _start.Enabled = false; }
        if (_infos.Count > 0) _reference.Text = $"自动参考 {CtbGrid.ReferenceMaxZoom(_infos[0].PixelSizeX)} 级；超过参考值不会更清晰，瓦片数每级约 ×4，超出护栏会拒绝。";
    }
}
