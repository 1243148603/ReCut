using System.Globalization;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 单条任务预设的编辑框（FR-08）。字段覆盖 PRD 说的四项：
/// 范围 / 切片参数 / layer.json 字段 / 导出选项。
/// </summary>
public sealed class TaskPresetEditForm : ThemedForm
{
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly TextBox[] _bounds = new TextBox[4];
    private readonly CheckBox _autoZoom = new();
    private readonly NumericUpDown _maxZoom = new();
    private readonly ComboBox _resampling = new();
    private readonly CheckBox _writeMetadata = new();
    private readonly TextBox _layerName = new();
    private readonly TextBox _layerDescription = new();
    private readonly TextBox _layerVersion = new();
    private readonly CheckBox _zipTiles = new();
    private readonly CheckBox _zipAlgo = new();
    private readonly Label _preview = new();
    private readonly Button _ok = new();

    private readonly TaskPreset? _original;
    public TaskPreset Result { get; private set; } = PresetStore.ChongqingTask;

    private static readonly string[] Labels = ["西", "南", "东", "北"];

    public TaskPresetEditForm(TaskPreset? original)
    {
        _original = original;
        Text = original is null ? "新增任务预设" : "编辑任务预设";
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.FitToContent(this, new Size(660, 540));
        MinimumSize = new Size(560, 460);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        LoadFrom(original);
        UpdatePreview();
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

        // ---- 名称与裁剪范围 ----
        var clipBox = new GroupBox { Text = "裁剪范围", Dock = DockStyle.Fill , AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink};
        var clip = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 3, Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        clip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        for (var c = 0; c < 3; c++) clip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

        clip.Controls.Add(new Label { Text = "名称：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.TextChanged += (_, _) => UpdatePreview();
        clip.Controls.Add(_name, 1, 0);
        clip.SetColumnSpan(_name, 3);

        clip.Controls.Add(new Label { Text = "范围：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        // 先把四个输入框建出来——删掉原来的循环时差点把这一步也删了，
        // 结果 _bounds 全是 null，窗体一构造就空引用。
        for (var i = 0; i < _bounds.Length; i++)
        {
            _bounds[i] = new TextBox();
            _bounds[i].TextChanged += (_, _) => UpdatePreview();
        }

        // 四个数值输入用共享的行构造器：每格宽度按内容定，不会挤到截断。
        var boundsRow = FormLayout.BoundsRow(_bounds, Labels);
        clip.Controls.Add(boundsRow, 1, 1);
        clip.SetColumnSpan(boundsRow, 3);

        clip.Controls.Add(new Label { Text = "说明：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _description.Dock = DockStyle.Fill;
        clip.Controls.Add(_description, 1, 2);
        clip.SetColumnSpan(_description, 3);
        clipBox.Controls.Add(clip);
        root.Controls.Add(clipBox, 0, 0);

        // ---- 切片与 layer.json ----
        var sliceBox = new GroupBox { Text = "切片与 layer.json", Dock = DockStyle.Fill , AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink};
        var slice = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 4, Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        slice.Controls.Add(new Label { Text = "最深层级：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var zoomRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        _autoZoom.Text = "自动（按栅格分辨率推算）";
        _autoZoom.AutoSize = true;
        _autoZoom.CheckedChanged += (_, _) =>
        {
            _maxZoom.Enabled = !_autoZoom.Checked;
            UpdatePreview();
        };
        _maxZoom.Minimum = 0;
        _maxZoom.Maximum = 30;
        _maxZoom.Width = 60;
        _maxZoom.ValueChanged += (_, _) => UpdatePreview();
        zoomRow.Controls.Add(_autoZoom);
        zoomRow.Controls.Add(_maxZoom);
        slice.Controls.Add(zoomRow, 1, 0);
        slice.SetColumnSpan(zoomRow, 3);

        slice.Controls.Add(new Label { Text = "重采样：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _resampling.DropDownStyle = ComboBoxStyle.DropDownList;
        _resampling.Width = 130;
        _resampling.Items.AddRange(["bilinear", "near", "cubic", "cubicspline", "lanczos", "average"]);
        slice.Controls.Add(_resampling, 1, 1);
        _writeMetadata.Text = "同时输出算法元数据 json";
        _writeMetadata.AutoSize = true;
        slice.Controls.Add(_writeMetadata, 2, 1);
        slice.SetColumnSpan(_writeMetadata, 2);

        slice.Controls.Add(new Label { Text = "layer.json：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        var layerRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        layerRow.Controls.Add(new Label { Text = "name", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 4, 0) });
        _layerName.Width = 130;
        _layerName.TextChanged += (_, _) => UpdatePreview();
        layerRow.Controls.Add(_layerName);
        layerRow.Controls.Add(new Label { Text = "version", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 6, 4, 0) });
        _layerVersion.Width = 80;
        layerRow.Controls.Add(_layerVersion);
        slice.Controls.Add(layerRow, 1, 2);
        slice.SetColumnSpan(layerRow, 3);

        slice.Controls.Add(new Label { Text = "description：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _layerDescription.Dock = DockStyle.Fill;
        slice.Controls.Add(_layerDescription, 1, 3);
        slice.SetColumnSpan(_layerDescription, 3);
        sliceBox.Controls.Add(slice);
        root.Controls.Add(sliceBox, 0, 1);

        // ---- 导出选项 ----
        var exportBox = new GroupBox { Text = "导出选项", Dock = DockStyle.Fill , AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink};
        var export = new FlowLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(8) };
        _zipTiles.Text = "前端产物打 zip";
        _zipTiles.AutoSize = true;
        _zipAlgo.Text = "算法产物打 zip";
        _zipAlgo.AutoSize = true;
        export.Controls.Add(_zipTiles);
        export.Controls.Add(_zipAlgo);
        exportBox.Controls.Add(export);
        root.Controls.Add(exportBox, 0, 2);

        _preview.Dock = DockStyle.Fill;
        _preview.AutoEllipsis = true;
        _preview.Height = 24;
        root.Controls.Add(_preview, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84 };
        _ok.Text = "确定";
        _ok.Width = 84;
        _ok.Click += (_, _) => { Result = Build(); DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        Controls.Add(root);
        // 填充面板先加、边条后加：WinForms 从后往前停靠，反了会被填充面板抢占整块。

        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private void LoadFrom(TaskPreset? preset)
    {
        var source = preset ?? PresetStore.ChongqingTask;
        _name.Text = preset is null ? "" : source.Name;
        _description.Text = source.Description;

        var box = source.ClipBox ?? PresetStore.Chongqing.Box;
        double[] values = [box.West, box.South, box.East, box.North];
        for (var i = 0; i < 4; i++)
            _bounds[i].Text = values[i].ToString(CultureInfo.InvariantCulture);

        _autoZoom.Checked = source.MaxZoom is null;
        _maxZoom.Enabled = source.MaxZoom is not null;
        _maxZoom.Value = Math.Clamp(source.MaxZoom ?? 14, _maxZoom.Minimum, _maxZoom.Maximum);

        _resampling.SelectedItem = source.Resampling;
        if (_resampling.SelectedIndex < 0) _resampling.SelectedIndex = 0;

        _writeMetadata.Checked = source.WriteMetadata;
        _layerName.Text = source.LayerName;
        _layerDescription.Text = source.LayerDescription;
        _layerVersion.Text = source.LayerVersion;
        _zipTiles.Checked = source.ZipTiles;
        _zipAlgo.Checked = source.ZipAlgo;
    }

    private TaskPreset Build()
    {
        var values = _bounds.Select(Parse).ToArray();
        var valid = !values.Any(double.IsNaN);
        return new TaskPreset
        {
            // 编辑内置项时换新 Id：否则下次升级换了内置定义，用户改的这份会被覆盖。
            Id = _original is { IsBuiltIn: false } ? _original.Id : Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(_name.Text) ? "未命名预设" : _name.Text.Trim(),
            Description = _description.Text.Trim(),
            Bounds = valid ? [.. values] : _original?.Bounds,
            Resampling = _resampling.SelectedItem?.ToString() ?? "bilinear",
            WriteMetadata = _writeMetadata.Checked,
            BoundaryCodes = _original?.BoundaryCodes,
            MaxZoom = _autoZoom.Checked ? null : (int)_maxZoom.Value,
            LayerName = string.IsNullOrWhiteSpace(_layerName.Text) ? "terrain" : _layerName.Text.Trim(),
            LayerDescription = _layerDescription.Text.Trim(),
            LayerVersion = string.IsNullOrWhiteSpace(_layerVersion.Text) ? "1.0.0" : _layerVersion.Text.Trim(),
            ZipTiles = _zipTiles.Checked,
            ZipAlgo = _zipAlgo.Checked,
            IsBuiltIn = false,
        };
    }

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : double.NaN;

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _preview.Text = "给预设起个名字。";
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var values = _bounds.Select(Parse).ToArray();
        if (values.Any(double.IsNaN))
        {
            _preview.Text = "裁剪范围四个数都要填：西 南 东 北。";
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var box = new BBox(values[0], values[1], values[2], values[3]);
        if (!box.IsValid(out var reason))
        {
            _preview.Text = reason;
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        _preview.Text = $"范围 {box.Width:F3}° × {box.Height:F3}°，" +
                        (_autoZoom.Checked ? "层级自动" : $"最深 {_maxZoom.Value} 级");
        _preview.ForeColor = SystemColors.GrayText;
        _ok.Enabled = true;
    }
}
