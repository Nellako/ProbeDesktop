namespace ProbeDesktop.App.Logic;

/// <summary>One scheduler per inspection page. Confirmed settings and completed rounds advance the schedule;
/// editing, replacing the wait, and temporarily losing eligibility never cancel a running round.</summary>
public sealed class InspectionScheduler(Func<CancellationToken, Task<bool>> runRound, TimeProvider? time = null) : IDisposable
{
    public const int DefaultIntervalMinutes = 30;
    public const int MinIntervalMinutes = 10;
    public const int MaxIntervalMinutes = 480;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private int _minutes = DefaultIntervalMinutes;
    private DateTimeOffset? _ended;
    private DateTimeOffset? _retryNotBefore;
    private DateTimeOffset? _backoff;
    private DateTimeOffset? _next;
    private bool _eligible, _busy, _disposed;
    private CancellationTokenSource? _wait, _active;

    public event EventHandler? NextStartChanged;
    public DateTimeOffset? NextStartAt { get { lock (_gate) return _next; } }
    public TimeSpan Interval { get { lock (_gate) return TimeSpan.FromMinutes(_minutes); } }
    public bool IsRunning { get { lock (_gate) return _eligible || _active is { IsCancellationRequested: false }; } }

    public static bool TryParseInterval(string? text, out int minutes) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes) &&
        minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes;

    public void UpdateSchedule(int minutes, DateTimeOffset? lastCompletedAt, bool eligible, bool busy = false, DateTimeOffset? retryNotBefore = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, MinIntervalMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxIntervalMinutes);
        lock (_gate)
        {
            if (_disposed || (_minutes == minutes && _ended == lastCompletedAt && _eligible == eligible && _busy == busy && _retryNotBefore == retryNotBefore)) return;
            if (_minutes != minutes || _ended != lastCompletedAt || _eligible != eligible || _busy != busy) _backoff = null;
            (_minutes, _ended, _eligible, _busy, _retryNotBefore) = (minutes, lastCompletedAt, eligible, busy, retryNotBefore);
        }
        Plan();
    }

    private void Plan()
    {
        CancellationTokenSource? stale = null;
        CancellationTokenSource? wait = null;
        Task? delay = null;
        bool changed;
        lock (_gate)
        {
            DateTimeOffset? due = null;
            if (!_disposed && _eligible && !_busy && _active is null)
            {
                due = _ended?.AddMinutes(_minutes) ?? _time.GetUtcNow();
                if (_retryNotBefore > due) due = _retryNotBefore;
                if (_backoff > due) due = _backoff;
            }
            changed = _next != due;
            if (!changed && (_wait is not null || due is null)) return;
            stale = _wait;
            _wait = null;
            _next = due;
            if (due is { } at)
            {
                wait = _wait = new();
                var remaining = at - _time.GetUtcNow();
                // Task.Delay timers have a finite range; a far-off server deadline is not a dispatch signal.
                delay = Task.Delay(remaining <= TimeSpan.Zero ? TimeSpan.Zero :
                    remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, _time, wait.Token);
            }
        }
        Cancel(stale);
        if (changed) NextStartChanged?.Invoke(this, EventArgs.Empty);
        if (wait is not null) _ = WaitAndRunAsync(wait, delay!);
    }

    private async Task WaitAndRunAsync(CancellationTokenSource wait, Task delay)
    {
        CancellationTokenSource? active = null;
        DateTimeOffset? startedFrom = null;
        try
        {
            // Defer even an overdue dispatch so the host can apply one full snapshot before running it.
            await Task.Yield();
            await delay;
            bool waitAgain;
            lock (_gate)
            {
                if (!ReferenceEquals(_wait, wait) || _active is not null || _disposed || !_eligible || _busy) return;
                _wait = null;
                waitAgain = _next > _time.GetUtcNow();
                if (!waitAgain)
                {
                    _next = null;
                    _active = active = new();
                    startedFrom = _ended;
                }
            }
            if (waitAgain) { Plan(); return; }
            NextStartChanged?.Invoke(this, EventArgs.Empty);
            if (!await runRound(active!.Token))
                lock (_gate) { if (!active.IsCancellationRequested) _eligible = false; }
        }
        catch (OperationCanceledException) { }
        finally
        {
            wait.Dispose();
            if (active is not null)
            {
                lock (_gate)
                {
                    _active = null;
                    // A no-op (e.g. all endpoints limited) is not a completed round. Keep its
                    // persisted baseline and back off unless a future host cooldown already gates dispatch.
                    if (!active.IsCancellationRequested && _ended == startedFrom)
                    {
                        var now = _time.GetUtcNow();
                        if (_retryNotBefore is null || _retryNotBefore <= now)
                            _backoff = now.AddMinutes(1);
                    }
                }
                active.Dispose();
                Plan();
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            _eligible = false;
            _backoff = null;
            active = _active;
        }
        Cancel(active);
        Plan();
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Stop();
    }
}
