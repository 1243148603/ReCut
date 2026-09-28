using CutMap.Core.Geo;
using CutMap.Core.Terrain;

namespace CutMap.App.Forms;

/// <summary>
/// 切片参数对话框（FR-02）。
///
/// 参考层级与瓦片数预估是这里的重点：FR-02 要求填得比参考值更深时给出提示，
/// 并对失控的输入**拒绝提交**。两者都建立在 <see cref="CtbGrid"/> 的精确计数上，
/// 而不是估算——计数是精确的，没理由近似。
/// </summary>
public sealed class SliceForm : Form
{
    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly NumericUpDown _maxZoom = new();
    private readonly CheckBox _autoZoom = new();
    private readonly Label _inputSummary = new();
    private readonly Label _estimate = new();
    private readonly Button _ok = new();

    private RasterInfo? _info;

    public string InputPath => _input.Text.Trim();
    public string OutputDirectory => _output.Text.Trim();
    public int? MaxZoom => _autoZoom.Checked ? null : (int)_maxZoom.Value;

    public SliceForm()
    {
        Text = "新建切片任务";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 330);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        UpdateEstimate();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 3,
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));

        // ---- 输入 ----
        layout.Controls.Add(new Label { Text = "输入 DEM：", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        _input.Dock = DockStyle.Fill;
        _input.ReadOnly = true;
        _input.BackColor = SystemColors.Window;
        layout.Controls.Add(_input, 1, 0);
        var browseIn = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browseIn.Click += (_, _) => BrowseInput();
        layout.Controls.Add(browseIn, 2, 0);

        _inputSummary.Dock = DockStyle.Fill;
        _inputSummary.ForeColor = SystemColors.GrayText;
        _inputSummary.AutoEllipsis = true;
        layout.Controls.Add(_inputSummary, 1, 1);
        layout.SetColumnSpan(_inputSummary, 2);

        // ---- 输出 ----
        layout.Controls.Add(new Label { Text = "输出目录：", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BackColor = SystemColors.Window;
        layout.Controls.Add(_output, 1, 2);
        var browseOut = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browseOut.Click += (_, _) => BrowseOutput();
        layout.Controls.Add(browseOut, 2, 2);

        // ---- 层级 ----
        layout.Controls.Add(new Label { Text = "最深层级：", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        var zoomRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        _autoZoom.Text = "自动（与 ctb-tile 省略 -s 的行为一致）";
        _autoZoom.Checked = true;
        _autoZoom.AutoSize = true;
        _autoZoom.CheckedChanged += (_, _) =>
        {
            _maxZoom.Enabled = !_autoZoom.Checked;
            UpdateEstimate();
        };
        _maxZoom.Minimum = 0;
        _maxZoom.Maximum = 30;
        _maxZoom.Value = 14;
        _maxZoom.Width = 64;
        _maxZoom.Enabled = false;
        _maxZoom.ValueChanged += (_, _) => UpdateEstimate();
        zoomRow.Controls.Add(_autoZoom);
        zoomRow.Controls.Add(_maxZoom);
        layout.Controls.Add(zoomRow, 1, 3);

        // ---- 预估 ----
        _estimate.Dock = DockStyle.Fill;
        _estimate.AutoEllipsis = true;
        _estimate.Padding = new Padding(0, 6, 0, 0);
        layout.Controls.Add(_estimate, 1, 4);
        layout.SetColumnSpan(_estimate, 2);

        // ---- 按钮 ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84 };
        _ok.Text = "开始切片";
        _ok.Width = 96;
        _ok.Enabled = false;
        _ok.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 3);

        for (var r = 0; r < 5; r++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private void BrowseInput()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 DEM（可多选；M2 支持多幅拼接）",
            Filter = "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|所有文件 (*.*)|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _input.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_output.Text))
            _output.Text = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? "", "cesium_terrain");

        try
        {
            _info = RasterInfo.Read(dialog.FileName);
            _inputSummary.Text = _info.Summary();
            _inputSummary.ForeColor = SystemColors.GrayText;

            var reference = CtbGrid.ReferenceMaxZoom(_info.PixelSizeX);
            _maxZoom.Value = Math.Clamp(reference, _maxZoom.Minimum, _maxZoom.Maximum);
            _autoZoom.Text = $"自动（参考值 {reference} 级）";
        }
        catch (RasterException ex)
        {
            _info = null;
            _inputSummary.Text = ex.Message;
            _inputSummary.ForeColor = Color.Firebrick;
        }
        UpdateEstimate();
    }

    private void BrowseOutput()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择瓦片输出目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _output.Text = dialog.SelectedPath;
    }

    /// <summary>输入或层级一变就重算。FR-02 的护栏在这里生效：失控的输入直接禁用提交。</summary>
    private void UpdateEstimate()
    {
        if (_info is null || !_info.HasProjection)
        {
            _estimate.Text = _info is null ? "请先选择输入 DEM。" : "输入没有坐标系，CTB 无法切片。";
            _estimate.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var maxZoom = (int)_maxZoom.Value;
        var estimate = CtbGrid.Estimate(_info.Extent, maxZoom);
        var reference = CtbGrid.ReferenceMaxZoom(_info.PixelSizeX);

        if (CtbGrid.ValidateCost(estimate) is { } rejection)
        {
            _estimate.Text = rejection;
            _estimate.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        _estimate.Text = estimate.Summary();
        _estimate.ForeColor = maxZoom > reference ? Color.DarkOrange : SystemColors.ControlText;
        if (maxZoom > reference)
            _estimate.Text += $"　⚠ 比参考值 {reference} 深：不会更清晰，瓦片数每级约 ×4";

        _ok.Enabled = !string.IsNullOrWhiteSpace(_output.Text);
    }
}
