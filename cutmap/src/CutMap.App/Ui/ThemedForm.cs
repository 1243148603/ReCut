namespace CutMap.App.Ui;

/// <summary>
/// 所有窗体的基类：统一按 DPI 缩放布局，并在加载时套用 <see cref="Theme"/>。
///
/// 用基类而不是让每个窗体自己调 Theme.Apply，是为了**不可能漏**——
/// 新加窗体时忘了套样式是很容易发生的事，而继承是写类声明时就定了的。
///
/// **为什么必须自己缩一次。** 代码里所有尺寸都是照 96 DPI 写的字面量
/// （标签 98px、固定列 104px、窗体 680×460）。在 150% 缩放的机器上字体按 DPI
/// 渲染成 23px 高（9pt 的点值本身就是物理单位），而这些字面量不会自己变大。
/// 实测打包窗口：文字需要 102~144px、盒子只有 98px，标签被裁成"瓦片目""versio"；
/// 各分组框加起来比窗体高 50px，底部的提示和按钮整排被挤出可视区。
///
/// 试过让 WinForms 自己缩（AutoScaleMode.Dpi + AutoScaleDimensions=96）：无论
/// 先设哪个属性，构造完再读设计尺寸都已经被改写成 144×144，缩放系数恒为 1，
/// 等于没缩。所以这里在句柄创建时按 <c>设备DPI / 96</c> 显式缩一次整棵控件树。
/// <see cref="Control.Scale(SizeF)"/> 会带上 TableLayoutPanel 的绝对列宽与子控件
/// 边距；**字号不缩**——9pt 在任何 DPI 下都是同一物理大小，缩了反而变大。
///
/// 之后再遇到跨显示器换 DPI，WinForms 会按"新DPI/旧DPI"再缩一次，与这一次叠乘
/// 正好等于 新DPI/96，不会双倍。
/// </summary>
public class ThemedForm : Form
{
    protected ThemedForm()
    {
        // 只影响后续的 DpiChanged 路径（它按 DPI 比值缩放）；
        // 首次显示由 OnHandleCreated 里的显式缩放负责。
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var factor = DeviceDpi / 96f;
        if (Math.Abs(factor - 1f) > 0.001f) Scale(new SizeF(factor, factor));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.Apply(this);
    }
}
