using System.Collections.Concurrent;

namespace CutMap.App.Ui;

/// <summary>
/// 工作线程 → UI 线程的唯一通道。
///
/// WinForms 的控件只能在创建它的线程上碰，而切片跑在后台线程里。直接跨线程写
/// 控件的行为是"通常抛异常、偶尔在某些控件上静默出错"——后者更难查。所以约定是：
/// **后台线程只往这里 Post，任何控件访问都发生在泵出来的委托里。**
///
/// 用轮询而不是 <c>Control.BeginInvoke</c>，是因为后台线程的产出速率远高于 UI
/// 需要刷新的速率（ctb-tile 一次切片能刷两百次进度），逐条 BeginInvoke 会把消息队列
/// 塞满。轮询天然做了合并：一次泵出把当前的都取走。
/// </summary>
public sealed class EventPump : IDisposable
{
    /// <summary>有活干时的轮询间隔。够快，看不出延迟。</summary>
    private const int BusyIntervalMs = 100;

    /// <summary>连续几次泵空之后改为慢轮询——长任务里绝大多数时间是无事的，100ms 一次纯属浪费。</summary>
    private const int IdleIntervalMs = 300;

    private const int IdleAfterEmptyDrains = 3;

    /// <summary>一次最多泵出多少条，防止 UI 线程被一帧内的海量事件卡住。</summary>
    private const int DrainLimit = 500;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly System.Windows.Forms.Timer _timer;
    private int _emptyDrains;

    /// <summary>因超过 <see cref="DrainLimit"/> 而被丢弃的旧事件数。非零说明 UI 跟不上。</summary>
    public int Dropped { get; private set; }

    public EventPump()
    {
        _timer = new System.Windows.Forms.Timer { Interval = BusyIntervalMs };
        _timer.Tick += (_, _) => Drain();
    }

    public void Start() => _timer.Start();

    /// <summary>任意线程可调，不阻塞、不抛异常。</summary>
    public void Post(Action action) => _queue.Enqueue(action);

    private void Drain()
    {
        var handled = 0;
        while (handled < DrainLimit && _queue.TryDequeue(out var action))
        {
            handled++;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 一个坏委托不该把泵打死：泵一停，整个界面就永远不再更新了。
                System.Diagnostics.Debug.WriteLine($"[EventPump] 委托抛出：{ex}");
            }
        }

        if (handled >= DrainLimit) Dropped += _queue.Count;

        _emptyDrains = handled == 0 ? _emptyDrains + 1 : 0;
        var wanted = _emptyDrains >= IdleAfterEmptyDrains ? IdleIntervalMs : BusyIntervalMs;
        if (_timer.Interval != wanted) _timer.Interval = wanted;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
