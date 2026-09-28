using CutMap.Core.Vector;

namespace CutMap.App.Forms;

/// <summary>
/// 行政区划选择器（FR-14）：树形浏览 + 名称/代码搜索 + 多选。
///
/// 选中的多个区域**不需要先做 dissolve**：gdalwarp 的 <c>-cutline</c> 把整个
/// 数据源当作一个掩膜，并集是掩膜本身自然得到的。
/// </summary>
public sealed class BoundaryPickerForm : Form
{
    private readonly BoundaryStore _store;
    private readonly TreeView _tree = new();
    private readonly TextBox _search = new();
    private readonly Label _status = new();
    private readonly Button _ok = new();

    public IReadOnlyList<BoundaryFeature> Selected { get; private set; } = [];

    public BoundaryPickerForm(BoundaryStore store)
    {
        _store = store;
        Text = "选择行政区划边界";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        BuildTree(null);
    }

    private void BuildLayout()
    {
        _tree.Dock = DockStyle.Fill;
        _tree.CheckBoxes = true;
        _tree.HideSelection = false;
        _tree.AfterCheck += (_, _) => UpdateStatus();

        var searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 3,
            Padding = new Padding(8, 6, 8, 0),
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        searchRow.Controls.Add(new Label { Text = "搜索：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.TextChanged += (_, _) => BuildTree(_search.Text);
        searchRow.Controls.Add(_search, 1, 0);
        var clear = new Button { Text = "清空", Dock = DockStyle.Fill };
        clear.Click += (_, _) => _search.Text = "";
        searchRow.Controls.Add(clear, 2, 0);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 40;
        _status.Padding = new Padding(10, 6, 10, 0);
        _status.ForeColor = SystemColors.GrayText;

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
        _ok.Click += (_, _) => Accept();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        Controls.Add(_tree);
        Controls.Add(searchRow);
        Controls.Add(_status);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// 重建树。有搜索词时**只列匹配项**（平铺），不保留层级——三千多个要素里
    /// 找一条，按层级展开比直接列出来慢得多，而且用户此刻已经不关心它在哪一级。
    /// </summary>
    private void BuildTree(string? term)
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        if (_store.IsEmpty)
        {
            _status.Text = _store.Describe();
            _status.ForeColor = Color.Firebrick;
            _tree.EndUpdate();
            _ok.Enabled = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            var matches = _store.Search(term, 500);
            foreach (var feature in matches)
                _tree.Nodes.Add(MakeNode(feature));
            _status.Text = matches.Count == 0
                ? "没有匹配的行政区。"
                : $"匹配 {matches.Count} 个。";
            _status.ForeColor = SystemColors.GrayText;
        }
        else
        {
            foreach (var province in _store.All.Where(f => f.Level == BoundaryLevel.省))
            {
                var provinceNode = MakeNode(province);
                foreach (var city in _store.ChildrenOf(province.Code!))
                {
                    var cityNode = MakeNode(city);
                    foreach (var county in _store.ChildrenOf(city.Code!))
                        cityNode.Nodes.Add(MakeNode(county));
                    provinceNode.Nodes.Add(cityNode);
                }
                _tree.Nodes.Add(provinceNode);
            }
            _status.Text = _store.Describe();
            _status.ForeColor = SystemColors.GrayText;
        }

        _tree.EndUpdate();
        _ok.Enabled = true;
    }

    private static TreeNode MakeNode(BoundaryFeature feature) =>
        new(feature.Display) { Tag = feature };

    private void UpdateStatus()
    {
        var count = CountChecked(_tree.Nodes);
        _status.Text = count == 0
            ? _store.Describe()
            : $"已选 {count} 个区域。选中多个时合并成一个掩膜，不必先做融合。";
        _status.ForeColor = count == 0 ? SystemColors.GrayText : Color.DarkGreen;
    }

    private static int CountChecked(TreeNodeCollection nodes) =>
        nodes.Cast<TreeNode>().Sum(node => (node.Checked ? 1 : 0) + CountChecked(node.Nodes));

    private static void CollectChecked(TreeNodeCollection nodes, List<BoundaryFeature> into)
    {
        foreach (TreeNode node in nodes)
        {
            // 勾了父级就不必再单列子级——父级的几何本来就含子级的地盘。
            if (node.Checked && node.Tag is BoundaryFeature feature)
                into.Add(feature);
            else
                CollectChecked(node.Nodes, into);
        }
    }

    private void Accept()
    {
        var picked = new List<BoundaryFeature>();
        CollectChecked(_tree.Nodes, picked);

        if (picked.Count == 0)
        {
            MessageBox.Show(this, "还没有选中任何行政区。", "请先选择",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Selected = picked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
