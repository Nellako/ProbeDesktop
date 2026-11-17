using ProbeDesktop.App.Logic;

namespace ProbeDesktop.Tests;

public static class AppSchedulerTests
{
    public static Task S01_IntervalValidation()
    {
        Check.Equal(30, InspectionScheduler.DefaultIntervalMinutes, "default interval");
        foreach (var text in new[] { "10", "30", "480" })
            Check.True(InspectionScheduler.TryParseInterval(text, out _), "valid integer " + text);
        foreach (var text in new[] { "9", "481", "1.5", "", "abc", "-10" })
            Check.False(InspectionScheduler.TryParseInterval(text, out _), "invalid integer " + text);
        return Task.CompletedTask;
    }

    public static async Task S02_WaitResetUsesSaveTime()
    {
        var clock = new ManualClock();
        int count = 0;
        using var scheduler = new InspectionScheduler(_ => { Interlocked.Increment(ref count); return Task.FromResult(true); }, clock);
        scheduler.Start(false);
        clock.Advance(2);
        scheduler.TrySetInterval(10);
        await Until(() => scheduler.NextStartAt == clock.Origin.AddMinutes(12));
        clock.Advance(9);
        Check.Equal(0, count, "new deadline has not arrived");
        clock.Advance(1);
        await Until(() => Volatile.Read(ref count) == 1);
        Check.Equal(1, count, "one round at save plus interval");
    }

    public static Task S03_SaveDuringRoundUsesAbsoluteDue() => BusySaveAsync(false);
    public static Task S04_OverdueSaveCoalescesOnce() => BusySaveAsync(true);

    private static async Task BusySaveAsync(bool overdue)
    {
        var clock = new ManualClock();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        CancellationToken roundToken = default;
        using var scheduler = new InspectionScheduler(ct =>
        {
            roundToken = ct;
            return Interlocked.Increment(ref count) == 1 ? release.Task : Task.FromResult(true);
        }, clock);
        scheduler.Start(true);
        Check.Equal(1, count, "initial round starts immediately");
        clock.Advance(2);
        scheduler.TrySetInterval(10);
        Check.False(roundToken.IsCancellationRequested, "save never cancels active round");
        clock.Advance(overdue ? 13 : 3);
        Check.Equal(1, count, "no overlapping round");
        release.SetResult(true);
        if (overdue)
        {
            await Until(() => Volatile.Read(ref count) == 2);
            await Until(() => scheduler.NextStartAt == clock.Origin.AddMinutes(25));
            Check.Equal(2, count, "overdue deadline merges into exactly one round");
        }
        else
        {
            await Until(() => scheduler.NextStartAt == clock.Origin.AddMinutes(12));
            clock.Advance(6);
            Check.Equal(1, count, "finishing at five does not start before twelve");
            clock.Advance(1);
            await Until(() => Volatile.Read(ref count) == 2);
            Check.Equal(2, count, "next round at twelve not fifteen");
        }
    }

    public static async Task S05_StopCancelsButInvalidSaveDoesNot()
    {
        var clock = new ManualClock();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        using var scheduler = new InspectionScheduler(async ct =>
        {
            token = ct;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { ended.SetResult(); throw; }
            return true;
        }, clock);
        scheduler.Start(true);
        Check.False(scheduler.TrySetInterval(9), "invalid interval is refused");
        Check.Equal(TimeSpan.FromMinutes(30), scheduler.Interval, "invalid save keeps current interval");
        Check.False(token.IsCancellationRequested, "invalid save does not stop round");
        scheduler.Stop();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(token.IsCancellationRequested, "explicit stop cancels round");
        Check.False(scheduler.IsRunning, "explicit stop clears scheduler");
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TestFailure("scheduler continuation did not reach expected state");
            await Task.Delay(1);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Origin { get; } = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(int minutes)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now = _now.AddMinutes(minutes);
                due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToList();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) { Disposed = true; Due = DateTimeOffset.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
