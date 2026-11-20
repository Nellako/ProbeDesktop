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

    public static async Task S02_ConfirmedIntervalUsesPersistedCompletion()
    {
        var clock = new ManualClock();
        int count = 0;
        using var scheduler = new InspectionScheduler(_ => { Interlocked.Increment(ref count); return Task.FromResult(true); }, clock);
        scheduler.UpdateSchedule(30, clock.Origin, eligible: true);
        clock.Advance(2);
        scheduler.UpdateSchedule(10, clock.Origin, eligible: true);
        await Until(() => scheduler.NextStartAt == clock.Origin.AddMinutes(10));
        clock.Advance(7);
        Check.Equal(0, count, "new deadline has not arrived");
        clock.Advance(1);
        await Until(() => Volatile.Read(ref count) == 1);
        Check.Equal(1, count, "one round at persisted completion plus confirmed interval, not save time");
    }

    public static Task S03_SaveDuringRoundUsesAbsoluteDue() => BusySaveAsync(false);
    public static Task S04_OverdueSaveCoalescesOnce() => BusySaveAsync(true);

    private static async Task BusySaveAsync(bool overdue)
    {
        var clock = new ManualClock();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        CancellationToken roundToken = default;
        InspectionScheduler scheduler = null!;
        using var lifetime = scheduler = new InspectionScheduler(async ct =>
        {
            roundToken = ct;
            int invocation = Interlocked.Increment(ref count);
            if (invocation == 1) await release.Task;
            scheduler.UpdateSchedule(10, clock.GetUtcNow(), eligible: true);
            return true;
        }, clock);
        scheduler.UpdateSchedule(30, null, eligible: true);
        await Until(() => Volatile.Read(ref count) == 1);
        clock.Advance(2);
        scheduler.UpdateSchedule(10, clock.Origin.AddMinutes(-30), eligible: true, busy: true);
        Check.False(roundToken.IsCancellationRequested, "save never cancels active round");
        clock.Advance(overdue ? 13 : 3);
        Check.Equal(1, count, "no overlapping round");
        release.SetResult(true);
        DateTimeOffset completed = clock.GetUtcNow();
        await Until(() => scheduler.NextStartAt == completed.AddMinutes(10));
        clock.Advance(9);
        Check.Equal(1, count, "even an overdue old deadline waits for the new completion plus interval");
        clock.Advance(1);
        await Until(() => scheduler.NextStartAt == completed.AddMinutes(20));
        Check.Equal(2, count, "only one round dispatches at the new deadline");
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
        scheduler.UpdateSchedule(30, null, eligible: true);
        await Until(() => token.CanBeCanceled);
        bool threw = false;
        try { scheduler.UpdateSchedule(9, clock.Origin, eligible: true); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check.True(threw, "invalid interval is refused");
        Check.Equal(TimeSpan.FromMinutes(30), scheduler.Interval, "invalid save keeps current interval");
        Check.False(token.IsCancellationRequested, "invalid save does not stop round");
        scheduler.Stop();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(token.IsCancellationRequested, "explicit stop cancels round");
        Check.False(scheduler.IsRunning, "explicit stop clears scheduler");
    }

    public static async Task S06_RestartHostCooldownAndEligibility()
    {
        var clock = new ManualClock();
        int count = 0;
        InspectionScheduler scheduler = null!;
        using var lifetime = scheduler = new InspectionScheduler(_ =>
        {
            Interlocked.Increment(ref count);
            scheduler.UpdateSchedule(10, clock.GetUtcNow(), eligible: true);
            return Task.FromResult(true);
        }, clock);
        DateTimeOffset persistedCompletion = clock.Origin.AddMinutes(-18);
        DateTimeOffset hostDeadline = clock.Origin.AddMinutes(4);
        scheduler.UpdateSchedule(10, persistedCompletion, eligible: false, retryNotBefore: hostDeadline);
        Check.Null(scheduler.NextStartAt, "no automatic schedule until the persisted plan is eligible");
        scheduler.UpdateSchedule(10, persistedCompletion, eligible: true, retryNotBefore: hostDeadline);
        Check.Equal(hostDeadline, scheduler.NextStartAt, "overdue after restart still honors host cooldown");
        clock.Advance(3);
        Check.Equal(0, count, "cooldown blocks despite overdue round");
        scheduler.UpdateSchedule(10, persistedCompletion, eligible: true, busy: true, retryNotBefore: hostDeadline);
        Check.Null(scheduler.NextStartAt, "externally running round suppresses dispatch");
        clock.Advance(3);
        Check.Equal(0, count, "busy suppresses overdue catch-up");
        scheduler.UpdateSchedule(10, persistedCompletion, eligible: true, retryNotBefore: hostDeadline);
        await Until(() => scheduler.NextStartAt == clock.Origin.AddMinutes(16));
        Check.Equal(1, count, "one overdue catch-up, then normal cadence resumes");
        scheduler.Stop();
        clock.Advance(100);
        Check.Null(scheduler.NextStartAt, "stop clears the pending deadline");
        Check.Equal(1, count, "stop leaves no pending round");
    }

    public static async Task S07_NoOpBacksOffWhenHostCooldownExpires()
    {
        foreach (int minutesUntilDispatch in new[] { 4, 5 })
        {
            var clock = new ManualClock();
            int count = 0;
            DateTimeOffset persistedCompletion = clock.Origin.AddMinutes(-18);
            DateTimeOffset hostDeadline = clock.Origin.AddMinutes(4);
            InspectionScheduler scheduler = null!;
            using var lifetime = scheduler = new InspectionScheduler(_ =>
            {
                int invocation = Interlocked.Increment(ref count);
                if (invocation == 2)
                    scheduler.UpdateSchedule(10, persistedCompletion, eligible: true, retryNotBefore: clock.GetUtcNow().AddSeconds(30));
                else if (invocation == 3)
                    scheduler.UpdateSchedule(10, clock.GetUtcNow(), eligible: true);
                return Task.FromResult(invocation <= 3);
            }, clock);

            scheduler.UpdateSchedule(10, persistedCompletion, eligible: true, retryNotBefore: hostDeadline);
            clock.Advance(minutesUntilDispatch);
            DateTimeOffset backoff = clock.GetUtcNow().AddMinutes(1);
            await Until(() => scheduler.NextStartAt == backoff || Volatile.Read(ref count) > 1);
            Check.Equal(backoff, scheduler.NextStartAt, "a no-op backs off for one minute when cooldown has expired or is due now");
            Check.Equal(1, count, "the overdue slot does not dispatch twice before backoff");

            clock.Advance(1);
            DateTimeOffset newCooldown = clock.GetUtcNow().AddSeconds(30);
            await Until(() => scheduler.NextStartAt == newCooldown);
            Check.Equal(2, count, "a future cooldown controls the next attempt without adding a minute");

            clock.Advance(1);
            DateTimeOffset normalDue = clock.GetUtcNow().AddMinutes(10);
            await Until(() => scheduler.NextStartAt == normalDue);
            Check.Equal(3, count, "a completed round resumes the normal interval from completion");
        }
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
