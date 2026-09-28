using CutMap.Core.Download;

namespace CutMap.App.Forms;

/// <summary>
/// 下载源管理（FR-16）：新增 / 编辑 / 删除 / 复制。
///
/// 内置源**不可删除但可禁用**：它删掉之后，如果用户的镜像全挂了就没有任何可用源，
/// 而"下不到数据"的原因会变得难判断。禁用足够表达"我暂时不用它"。
/// </summary>
public sealed class SourceEditorForm : Form
{
    private readonly SourceStore _store;
    private readonly ListView _list = new();

    public SourceEditorForm(SourceStore store)
    {
        _store = store;
        Text = "下载源管理";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 400);
        MinimumSize = new Size(620, 340);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        Reload();
    }

    private void BuildLayout()
    {
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("名称", 240);
        _list.Columns.Add("URL 模板", 380);
        _list.Columns.Add("状态", 80);
        _list.DoubleClick += (_, _) => Edit();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 100,
            Padding = new Padding(8),
            WrapContents = false,
        };
        side.Controls.Add(MakeButton("新增…", Add));
        side.Controls.Add(MakeButton("编辑…", Edit));
        side.Controls.Add(MakeButton("复制", Duplicate));
        side.Controls.Add(MakeButton("删除", Delete));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 84 };
        buttons.Controls.Add(close);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(12, 4, 12, 0),
            Text = "占位符：{name} 分幅全名（N29_E106）、{lat} 纬度标签（N29）、{lon} 经度标签（E106）。\n" +
                   "注意教程里写的是 N{lat}，那要求 {lat} 是纯数字；本软件的 {lat} 已含半球字母，照抄会拼出 N N29。",
        };

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_list);
        panel.Controls.Add(side);

        Controls.Add(panel);
        Controls.Add(hint);
        Controls.Add(buttons);
        AcceptButton = close;
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 84, Height = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var source in _store.All)
        {
            var item = new ListViewItem(source.Name + (source.IsBuiltIn ? "（内置）" : "")) { Tag = source };
            item.SubItems.Add(source.UrlTemplate);
            item.SubItems.Add(source.Enabled ? "启用" : "禁用");
            if (!source.Enabled) item.ForeColor = SystemColors.GrayText;
            if (source.LastError is { } error)
            {
                item.SubItems.Add("");
                item.ToolTipText = "上次失败：" + error;
            }
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private DownloadSource? Selected => _list.SelectedItems.Count > 0
        ? _list.SelectedItems[0].Tag as DownloadSource
        : null;

    private void Add()
    {
        using var dialog = new SourceEditForm(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.Save(dialog.Result);
        Reload();
    }

    private void Edit()
    {
        if (Selected is not { } source) return;
        using var dialog = new SourceEditForm(source);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.Save(dialog.Result);
        Reload();
    }

    private void Duplicate()
    {
        if (Selected is not { } source) return;
        // 复制的目的是"照着一条改一条"（换内网地址、换命名规则），所以带全部字段。
        _store.Save(source with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = source.Name + "（副本）",
            IsBuiltIn = false,
        });
        Reload();
    }

    private void Delete()
    {
        if (Selected is not { } source) return;
        if (source.IsBuiltIn)
        {
            MessageBox.Show(this, "内置源不能删除，但可以编辑为「禁用」。",
                            "删不掉", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除下载源「{source.Name}」？", "确认",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _store.Delete(source.Id);
        Reload();
    }
}

/// <summary>单条下载源的编辑框。</summary>
public sealed class SourceEditForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _template = new();
    private readonly TextBox _description = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _preview = new();
    private readonly Button _ok = new();

    private readonly DownloadSource? _original;
    public DownloadSource Result { get; private set; } = DownloadSource.CopernicusOfficial;

    public SourceEditForm(DownloadSource? original)
    {
        _original = original;
        Text = original is null ? "新增下载源" : "编辑下载源";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 300);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "名称：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.Text = original?.Name ?? "";
        layout.Controls.Add(_name, 1, 0);

        layout.Controls.Add(new Label { Text = "URL 模板：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _template.Dock = DockStyle.Fill;
        _template.Text = original?.UrlTemplate ?? DownloadSource.CopernicusTemplate;
        _template.TextChanged += (_, _) => UpdatePreview();
        _template.Height = 44;
        layout.Controls.Add(_template, 1, 1);

        layout.Controls.Add(new Label { Text = "说明：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _description.Dock = DockStyle.Fill;
        _description.Text = original?.Description ?? "";
        layout.Controls.Add(_description, 1, 2);

        _enabled.Text = "启用（出现在下载对话框的下拉里）";
        _enabled.AutoSize = true;
        _enabled.Checked = original?.Enabled ?? true;
        layout.Controls.Add(_enabled, 1, 3);

        _preview.Dock = DockStyle.Fill;
        _preview.AutoEllipsis = true;
        layout.Controls.Add(_preview, 1, 4);

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
        Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;
        UpdatePreview();
    }

    private DownloadSource Build() => new()
    {
        Id = _original?.Id ?? Guid.NewGuid().ToString("N"),
        Name = string.IsNullOrWhiteSpace(_name.Text) ? "未命名源" : _name.Text.Trim(),
        UrlTemplate = _template.Text.Trim(),
        Description = _description.Text.Trim(),
        IsBuiltIn = _original?.IsBuiltIn ?? false,
        Enabled = _enabled.Checked,
        LastSuccess = _original?.LastSuccess,
        LastError = _original?.LastError,
    };

    private void UpdatePreview()
    {
        var candidate = Build() with { Id = _original?.Id ?? "probe" };
        if (!candidate.IsUsable(out var problem))
        {
            _preview.Text = problem;
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var probe = new DemTile(29, 106, "N29", "E106", new Core.Geo.BBox(106, 29, 107, 30));
        _preview.Text = "预览（N29_E106）：" + candidate.Expand(probe);
        _preview.ForeColor = SystemColors.GrayText;
        _ok.Enabled = true;
    }
}
