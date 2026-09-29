namespace CutMap.Core.Download;

/// <summary>
/// 协作式暂停。
///
/// PRD FR-11 / OUT-07 只给**下载**做运行中暂停：下载有一个天然的循环（分块读写），
/// 在块与块之间插一个等待点即可，恢复后靠 HTTP Range 接着下，不重下已完成的字节。
/// 切片、拼接、打包都没有可插等待点的循环——ctb-tile 是子进程、gdal.Warp 是一次性调用——
/// 所以那三种只能退化为"跑完当前即停"。
///
/// <see cref="WaitIfPaused"/> 必须接受取消令牌：**暂停中的任务被取消要立即退出**，
/// 不允许因为死等恢复信号而把工作线程卡住（这条是 PRD 的硬要求）。
/// </summary>
public sealed class PauseToken : IDisposable
{
    private readonly ManualResetEventSlim _resume = new(initialState: true);

    public bool IsPaused => !_resume.IsSet;

    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    public void WaitIfPaused(CancellationToken cancellation) =>
        _resume.Wait(cancellation);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resume.Dispose();
    }
}
