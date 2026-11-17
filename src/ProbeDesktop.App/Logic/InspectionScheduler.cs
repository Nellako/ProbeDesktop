namespace ProbeDesktop.App.Logic;

/// <summary>
/// 巡检定时循环（纯调度，无 UI）。三个相互独立的取消源：
/// 循环令牌（Stop 才取消）、等待令牌（保存间隔时只取消当前这次等待并重新计时）、轮令牌（Stop 时随循环取消）。
/// 因此“保存新间隔”只会重置等待，绝不取消正在执行的一轮。
/// 每轮结束后等待一个完整间隔再开始下一轮：不重叠、不补跑。
/// 续体不使用 ConfigureAwait(false)：在 UI 线程调用 Start 时，回调、事件都回到 UI 线程；无同步上下文（测试）时在线程池上运行。
/// </summary>
public sealed class InspectionScheduler : IDisposable
{
    public const int DefaultIntervalMinutes = 30;
    public const int MinIntervalMinutes = 10;
    public const int MaxIntervalMinutes = 480;

    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<bool>> _runRound;
    private readonly object _gate = new();
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _waitCts;
    private TimeSpan _interval = TimeSpan.FromMinutes(DefaultIntervalMinutes);
    private DateTimeOffset? _nextStart;
    private DateTimeOffset? _savedDue;
    private long _scheduleRevision;
    private bool _disposed;

    /// <param name="runRound">
    /// 执行一轮，返回 false 表示整体致命失败、循环应停止（由回调自己负责善后）；返回 true（含“已有轮在进行而跳过”）继续等待下一轮。
    /// 令牌在 Stop 时取消，保存间隔时不会取消。
    /// </param>
    public InspectionScheduler(Func<CancellationToken, Task<bool>> runRound, TimeProvider? time = null)
    {
        _runRound = runRound;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>下一轮计划开始时间（仅在等待中有值）或 null；变化时触发。</summary>
    public event EventHandler? NextStartChanged;

    public bool IsRunning
    {
        get { lock (_gate) { return _loopCts is not null; } }
    }

    public DateTimeOffset? NextStartAt
    {
        get { lock (_gate) { return _nextStart; } }
    }

    public TimeSpan Interval
    {
        get { lock (_gate) { return _interval; } }
    }

    public static bool TryParseInterval(string? text, out int minutes)
    {
        minutes = 0;
        return int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes)
               && minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes;
    }

    /// <summary>保存间隔，以此刻起算绝对到期时间；不启动、不停止，也不打断在途轮次。</summary>
    public bool TrySetInterval(int minutes)
    {
        if (minutes is < MinIntervalMinutes or > MaxIntervalMinutes)
        {
            return false;
        }

        CancellationTokenSource? wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _interval = TimeSpan.FromMinutes(minutes);
            _scheduleRevision++;
            if (_loopCts is not null)
            {
                _savedDue = _time.GetUtcNow() + _interval;
                _nextStart = _savedDue;
            }
            wait = _waitCts;
        }

        Cancel(wait);
        NextStartChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>启动循环。已在运行则先停止旧循环。immediate=true 时先立即执行一轮。</summary>
    public void Start(bool immediate)
    {
        CancellationTokenSource? old;
        CancellationTokenSource loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            old = _loopCts;
            loop = new CancellationTokenSource();
            _loopCts = loop;
            _nextStart = null;
            _savedDue = null;
            _scheduleRevision++;
        }

        Cancel(old);
        _ = LoopAsync(immediate, loop);
    }

    public void Stop()
    {
        CancellationTokenSource? loop;
        lock (_gate)
        {
            loop = _loopCts;
            _loopCts = null;
            _nextStart = null;
            _savedDue = null;
            _scheduleRevision++;
        }

        Cancel(loop);
        NextStartChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoopAsync(bool immediate, CancellationTokenSource loop)
    {
        var ct = loop.Token;
        try
        {
            var first = immediate;
            while (!ct.IsCancellationRequested)
            {
                if (!first)
                {
                    await WaitAsync(ct);
                }

                first = false;
                SetNext(null);
                if (!await _runRound(ct))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var current = false;
            lock (_gate)
            {
                if (ReferenceEquals(_loopCts, loop))
                {
                    _loopCts = null;
                    _nextStart = null;
                    current = true;
                }
            }

            loop.Dispose();
            if (current)
            {
                NextStartChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task WaitAsync(CancellationToken loopCt)
    {
        while (true)
        {
            loopCt.ThrowIfCancellationRequested();
            var waitCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
            DateTimeOffset due;
            long revision;
            Task delay;
            lock (_gate)
            {
                _waitCts = waitCts;
                due = _savedDue ?? _time.GetUtcNow() + _interval;
                revision = _scheduleRevision;
                _nextStart = due;
                var remaining = due - _time.GetUtcNow();
                delay = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time, waitCts.Token);
            }

            NextStartChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await delay;
                lock (_gate)
                {
                    if (revision != _scheduleRevision)
                    {
                        continue;
                    }
                    _savedDue = null;
                }
                return;
            }
            catch (OperationCanceledException) when (!loopCt.IsCancellationRequested)
            {
                // 间隔被重新保存：丢弃本次等待，按新间隔从现在起重新计时。
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_waitCts, waitCts))
                    {
                        _waitCts = null;
                    }
                }

                waitCts.Dispose();
            }
        }
    }

    private void SetNext(DateTimeOffset? value)
    {
        lock (_gate)
        {
            _nextStart = value;
        }

        NextStartChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }
}
