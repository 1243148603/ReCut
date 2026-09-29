using System.Globalization;
using CutMap.Core.Download;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.Core.Vector;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 「下载 DEM 数据…」对话框（FR-06 / FR-16）。工具条上的**独立入口**，不在新建任务向导内。
///
/// 界面必须显示**展开后的 URL 预览**：PRD 的占位符口径（{lat}=N29）与 204 教程 §3.1
/// 的写法（N{lat}，{lat}=29）不同，把教程那条 URL 原样粘进模板会拼出 "NN29"。
/// 与其等下载全 404 再回头查，不如在拼的时候就把结果显示出来。
/// </summary>
public sealed class DownloadForm : ThemedForm
{
    private readonly SourceStore _store = new();
    private readonly ComboBox _source = new();
    private readonly TextBox[] _area = new TextBox[4];
    private readonly Label _preview = new();
    private readonly Label _urlPreview = new();
    private readonly TextBox _saveDir = new();
    private readonly Label _status = new();
    private readonly Button _ok = new();

    private IReadOnlyList<DemTile> _tiles = [];

    private readonly CheckBox _useBoundary = new();
    private readonly Button _pickBoundary = new();
    private readonly Label _boundarySummary = new();
    private readonly BoundaryStore _boundaryStore = BoundaryStore.Locate();
    private IReadOnlyList<BoundaryFeature> _boundaries = [];

    private static readonly string[] AreaLabels = ["西", "南", "东", "北"];

    public DownloadSource SelectedSource =>
        _source.SelectedItem as DownloadSource ?? DownloadSource.CopernicusOfficial;
    public IReadOnlyList<DemTile> Tiles => _tiles;
    public string SaveDirectory => _saveDir.Text.Trim();
    public BBox Area => new(Parse(_area[0]), Parse(_area[1]), Parse(_area[2]), Parse(_area[3]));

    /// <param name="initialArea">从历史重跑时预填的区域。</param>
    /// <param name="initialSaveDir">预填的保存目录。</param>
    /// <param name="initialSourceId">预填的下载源；找不到就退回第一个可用源。</param>
    public DownloadForm(Core.Geo.BBox? initialArea = null, string? initialSaveDir = null,
                        string? initialSourceId = null)
    {
        Text = "下载 DEM 数据";
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.FitToContent(this, new Size(760, 400));
        MinimumSize = new Size(680, 380);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        ReloadSources();

        if (initialSourceId is not null)
        {
            for (var i = 0; i < _source.Items.Count; i++)
            {
                if (_source.Items[i] is DownloadSource s && s.Id == initialSourceId)
                {
                    _source.SelectedIndex = i;
                    break;
                }
            }
        }
        if (initialSaveDir is not null) _saveDir.Text = initialSaveDir;

        ApplyArea(initialArea ?? new BBox(105.3, 28.1, 110.2, 32.2), "重庆全域");
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
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- 下载源 ----
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 2, AutoSize = true };
        sourceRow.Controls.Add(new Label { Text = "下载源：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.Width = 340;
        _source.SelectedIndexChanged += (_, _) => UpdatePreview();
        sourceRow.Controls.Add(_source, 1, 0);
        var manage = new Button { Text = "管理源…", Width = 88 };
        manage.Click += (_, _) => ManageSources();
        sourceRow.Controls.Add(manage, 2, 0);
        _urlPreview.Dock = DockStyle.Fill;
        _urlPreview.ForeColor = SystemColors.GrayText;
        _urlPreview.AutoEllipsis = true;
        sourceRow.Controls.Add(_urlPreview, 1, 1);
        sourceRow.SetColumnSpan(_urlPreview, 3);
        root.Controls.Add(sourceRow, 0, 0);

        // ---- 区域 ----
        var areaBox = new GroupBox { Text = "区域（WGS84 经纬度，按 1°×1° 网格推导分幅）", Dock = DockStyle.Fill , AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink};
        var areaRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 9, RowCount = 2, Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        areaRow.Controls.Add(new Label { Text = "预设：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        foreach (var p in new PresetStore().ClipPresets) preset.Items.Add(p);
        preset.SelectedIndex = 0;
        preset.SelectedIndexChanged += (_, _) =>
        {
            if (preset.SelectedItem is ClipPreset p) ApplyArea(p.Box, p.Name);
        };
        areaRow.Controls.Add(preset, 1, 0);

        for (var i = 0; i < 4; i++)
        {
            areaRow.Controls.Add(new Label { Text = AreaLabels[i], AutoSize = true, Anchor = AnchorStyles.Left }, i * 2 + 2, 0);
            var box = new TextBox { Width = 92 };
            box.TextChanged += (_, _) => UpdatePreview();
            _area[i] = box;
            areaRow.Controls.Add(box, i * 2 + 3, 0);
        }

        _useBoundary.Text = "按行政区划（按边界真实相交推导，比外接矩形省流量）";
        _useBoundary.AutoSize = true;
        _useBoundary.CheckedChanged += (_, _) => UpdatePreview();
        areaRow.Controls.Add(_useBoundary, 0, 2);
        areaRow.SetColumnSpan(_useBoundary, 4);

        _pickBoundary.Text = "选择边界…";
        _pickBoundary.Width = 96;
        _pickBoundary.Click += (_, _) => PickBoundary();
        areaRow.Controls.Add(_pickBoundary, 4, 2);

        _boundarySummary.AutoEllipsis = true;
        _boundarySummary.ForeColor = SystemColors.GrayText;
        areaRow.Controls.Add(_boundarySummary, 5, 2);
        areaRow.SetColumnSpan(_boundarySummary, 5);

        _preview.Dock = DockStyle.Fill;
        _preview.Padding = new Padding(0, 4, 0, 0);
        areaRow.Controls.Add(_preview, 0, 1);
        areaRow.SetColumnSpan(_preview, 10);
        areaRow.RowCount = 3;
        areaBox.Controls.Add(areaRow);
        root.Controls.Add(areaBox, 1, 0);

        // ---- 保存目录 ----
        var saveRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 2, AutoSize = true };
        saveRow.Controls.Add(new Label { Text = "保存到：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _saveDir.Dock = DockStyle.Fill;
        saveRow.Controls.Add(_saveDir, 1, 0);
        var browse = new Button { Text = "浏览…", Width = 80 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择分幅保存目录" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _saveDir.Text = dialog.SelectedPath;
        };
        saveRow.Controls.Add(browse, 2, 0);

        _status.Dock = DockStyle.Fill;
        _status.AutoEllipsis = true;
        saveRow.Controls.Add(_status, 1, 1);
        saveRow.SetColumnSpan(_status, 2);
        root.Controls.Add(saveRow, 2, 0);

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
        _ok.Text = "开始下载";
        _ok.Width = 96;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        Controls.Add(root);
        // 填充面板先加、边条后加：WinForms 从后往前停靠，反了会被填充面板抢占整块。

        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private static void ApplyAreaTo(TextBox[] boxes, BBox area)
    {
        boxes[0].Text = area.West.ToString(CultureInfo.InvariantCulture);
        boxes[1].Text = area.South.ToString(CultureInfo.InvariantCulture);
        boxes[2].Text = area.East.ToString(CultureInfo.InvariantCulture);
        boxes[3].Text = area.North.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyArea(BBox area, string name)
    {
        ApplyAreaTo(_area, area);
        _ = name;
        UpdatePreview();
    }

    private void ReloadSources()
    {
        _source.Items.Clear();
        var enabled = _store.Enabled;
        foreach (var s in enabled) _source.Items.Add(s);
        if (_source.Items.Count > 0) _source.SelectedIndex = 0;
    }

    private void ManageSources()
    {
        using var dialog = new SourceEditorForm(_store);
        dialog.ShowDialog(this);
        var previous = (_source.SelectedItem as DownloadSource)?.Id;
        ReloadSources();
        if (previous is not null && _store.ById(previous) is { } found)
            _source.SelectedIndex = Math.Max(0, _source.Items.IndexOf(found));
        UpdatePreview();
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

        // 把手填区域也同步成边界的外接矩形——保存目录与任务参数里要记一个范围，
        // 按边界下载时用它的外接矩形是合理的口径。
        var box = BoundaryExport.UnionBounds(_boundaries);
        ApplyAreaTo(_area, box);
        UpdatePreview();
    }

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : double.NaN;

    /// <summary>区域或源一变就重算：分幅数、总量、以及**展开后的 URL**。</summary>
    private void UpdatePreview()
    {
        var source = SelectedSource;
        if (!source.IsUsable(out var sourceProblem))
        {
            _urlPreview.Text = sourceProblem;
            _urlPreview.ForeColor = Color.Firebrick;
            _status.Text = sourceProblem;
            _status.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            _tiles = [];
            return;
        }

        var area = Area;
        if (double.IsNaN(area.West) || double.IsNaN(area.South) ||
            double.IsNaN(area.East) || double.IsNaN(area.North))
        {
            SetPreview("区域要填四个数：西 南 东 北。", Color.Firebrick, []);
            return;
        }
        if (!area.IsValid(out var invalid))
        {
            SetPreview(invalid, Color.Firebrick, []);
            return;
        }

        // 按行政区划时用**真实相交**推导分幅（FR-06）：重庆这样形状不规则的地方，
        // 比外接矩形少下好几幅。
        if (_useBoundary.Checked)
        {
            if (_boundaries.Count == 0)
            {
                SetPreview("请先选择行政区划边界。", Color.Firebrick, []);
                return;
            }
            _boundarySummary.Text = "已选 " + string.Join("、", _boundaries.Select(b => b.Name));
            _pickBoundary.Enabled = true;

            var byBounds = DemTileGrid.Enumerate(area);
            _tiles = BoundaryTiles.Enumerate(_boundaries);
            if (_tiles.Count == 0)
            {
                SetPreview("选中的边界落不到任何 1° 分幅上。", Color.Firebrick, []);
                return;
            }
            var saved = byBounds.Count - _tiles.Count;
            _urlPreview.Text = "URL 预览：" + source.Expand(_tiles[0]);
            _urlPreview.ForeColor = SystemColors.GrayText;
            SetPreview($"按边界推导出 {_tiles.Count} 幅（{_tiles[0].Name} … {_tiles[^1].Name}），" +
                       (saved > 0 ? $"比外接矩形省 {saved} 幅（约 {saved * 45} MB）。" : "与用外接矩形相当。"),
                       Color.DarkGreen, _tiles);
            return;
        }

        _boundarySummary.Text = "";
        _tiles = DemTileGrid.Enumerate(area);
        if (_tiles.Count == 0)
        {
            SetPreview("这个范围落不到任何 1° 分幅上。", Color.Firebrick, []);
            return;
        }

        // 单幅约 45 MB（教程 §3.1）。按幅数估总量，误差来自各幅压缩率不同（44–48 MB）。
        var estimatedGb = _tiles.Count * 45.0 / 1024.0;
        _urlPreview.Text = "URL 预览：" + source.Expand(_tiles[0]);
        _urlPreview.ForeColor = SystemColors.GrayText;
        SetPreview($"将下载 {_tiles.Count} 幅（{_tiles[0].Name} … {_tiles[^1].Name}），" +
                   $"约 {estimatedGb:F2} GB。", Color.DarkGreen, _tiles);
    }

    private void SetPreview(string text, Color color, IReadOnlyList<DemTile> tiles)
    {
        _tiles = tiles;
        _status.Text = text;
        _status.ForeColor = color;
        _ok.Enabled = tiles.Count > 0 && !string.IsNullOrWhiteSpace(_saveDir.Text);
    }
}
