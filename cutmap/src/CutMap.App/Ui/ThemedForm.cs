namespace CutMap.App.Ui;

/// <summary>
/// 所有窗体的基类：在加载时套用 <see cref="Theme"/>。
///
/// 用基类而不是让每个窗体自己调 Theme.Apply，是为了**不可能漏**——
/// 新加窗体时忘了套样式是很容易发生的事，而继承是写类声明时就定了的。
/// </summary>
public class ThemedForm : Form
{
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.Apply(this);
    }
}
