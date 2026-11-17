using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>
/// 管理全部检测记录与本地轮询（身份识别巡检的等待也复用这里的每记录单飞轮询）。所有方法在 UI 线程调用。
/// 规则：每条记录同一时间最多一个查询请求（单飞）；自动查询间隔 5 秒；连续失败或断网即暂停，由用户手动恢复；
/// 查询从不重新提交；启动时不自动联网。
/// </summary>
public sealed class ProbeRunCoordinator : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(10);
    private const int MaxConsecutiveFailures = 3;

    private readonly IDesktopService _service;
    private readonly IDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, ProbeRecordViewModel> _index = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loops = [];
    private readonly HashSet<Task> _pendingCore = [];
    private readonly HashSet<Guid> _autoStarted = [];
    private readonly HashSet<Guid> _userStopped = [];
    private readonly Dictionary<Guid, string> _lastPollCodes = [];
    private bool _isSubmitting;
    private bool _disposed;

    public ProbeRunCoordinator(IDesktopService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
    }

    public ObservableCollection<ProbeRecordViewModel> Records { get; } = [];

    /// <summary>某条记录内容变化（新增、更新、删除）。</summary>
    public event EventHandler? RecordsChanged;

    public CancellationToken Lifetime => _lifetime.Token;
    public bool IsSubmitting => _isSubmitting;
    public bool AnyAutoPolling => Records.Any(r => r.IsAutoPolling);
    public bool AnyActiveRecord => Records.Any(r => r.IsActive);

    public ProbeRecordViewModel? Find(Guid localId) => _index.GetValueOrDefault(localId);

    public ProbeRecordViewModel Track(ProbeRecord record)
    {
        if (_index.TryGetValue(record.LocalId, out var existing))
        {
            existing.Apply(record);
            RecordsChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }

        var vm = new ProbeRecordViewModel(record, this);
        _index[record.LocalId] = vm;
        Records.Add(vm);
        RecordsChanged?.Invoke(this, EventArgs.Empty);
        return vm;
    }

    /// <summary>用本地存储的历史对齐界面。正在查询中的记录不被覆盖。</summary>
    public void MergeHistory(IReadOnlyList<ProbeRecord> history)
    {
        var ids = history.Select(h => h.LocalId).ToHashSet();
        foreach (var stale in Records.Where(r => !ids.Contains(r.LocalId) && !r.IsPolling && !r.IsAutoPolling).ToList())
        {
            Forget(stale.LocalId, raise: false);
        }

        foreach (var record in history)
        {
            if (_index.TryGetValue(record.LocalId, out var existing))
            {
                if (!existing.IsPolling)
                {
                    existing.Apply(record);
                }
            }
            else
            {
                var vm = new ProbeRecordViewModel(record, this);
                _index[record.LocalId] = vm;
                Records.Add(vm);
            }
        }

        RecordsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启动时调用：说明哪些记录未结束。手动提交的记录和旧版“能力自动”历史记录不会自动联网；身份识别巡检发起的记录另由 ResumeAutomaticRecords 恢复查询。</summary>
    public void AnnotateStartupRecords()
    {
        foreach (var vm in Records.Where(r => r.IsActive && !r.IsAutoPolling))
        {
            vm.PollNote = vm.IsPollable
                ? vm.ManualResumeRequired
                    ? ManualResumeNote
                    : "启动时未自动联网。远端检测可能仍在进行；如需继续，请点击“恢复自动查询”或“查询一次”。"
                : "没有远端任务标识，无法查询，也不会重新提交。如需发起新检测，请先删除此本地记录。";
        }
    }

    /// <summary>从迁移包导入的未结束记录的说明。该标记由 Core 持久保存，每次启动都不会自动查询。</summary>
    public const string ManualResumeNote =
        "这条记录来自完整数据迁移，远端检测状态未知。程序不会自动查询（重启后也不会）；如需继续，请点击“查询一次”或“恢复自动查询”，确认接收方后只向 BazaarLink 读取进度（GET），不会重新提交，也不会解除“已有未结束任务”的提交限制。";

    /// <summary>
    /// 迁移导入的未结束记录须由用户逐次确认接收方后才能查询。返回 false 表示用户取消，不联网。
    /// 只发 GET 读取进度，不携带中转站 Key，不重新 POST。
    /// </summary>
    private bool ConfirmManualResume(ProbeRecordViewModel vm, bool continuous)
    {
        if (!vm.ManualResumeRequired)
        {
            return true;
        }

        var origin = string.IsNullOrWhiteSpace(vm.Record.ServiceOrigin) ? "https://bazaarlink.ai" : vm.Record.ServiceOrigin;
        var message =
            "这条鉴别记录来自完整数据迁移，远端任务状态未知，程序不会自动查询。\n\n"
            + $"配置：{vm.ProfileName}\n模型：{vm.ModelId}\n"
            + $"接收方：{origin}（GET /api/probe/run/{{runId}}，只读取进度）\n"
            + (continuous
                ? "方式：恢复自动查询，每 5 秒读取一次，连续失败、断网或限流会暂停。\n\n"
                : "方式：只查询一次。\n\n")
            + "查询会向 BazaarLink 发送该任务的远端标识，不携带中转站 API Key，不会重新提交检测，也不会撤销或停止远端任务和计费。"
            + "这条记录在结束前仍会阻止重复提交；下次启动也仍须手动恢复。\n\n确定要查询吗？";
        return _dialogs.Confirm("确认查询迁移的鉴别记录", message);
    }

    /// <summary>
    /// 身份识别巡检发起的鉴别记录：对有远端任务标识的未结束记录启动自动查询（只发 GET 读取进度，绝不重新提交，
    /// 也不重放旧轮次剩余的目标）。没有远端标识的记录无法查询，保持“结果未知”。
    /// 旧版“能力自动”来源的历史记录不在此恢复，需要时由用户手动恢复查询。已经启动过自动查询的记录不会被再次启动：
    /// 用户停止、连续失败或网络中断后的暂停，仍需用户手动恢复。返回本次新启动的数量。
    /// </summary>
    public int ResumeAutomaticRecords()
    {
        var started = 0;
        // 迁移导入、须手动恢复的记录（ManualResumeRequired）始终跳过：重启后也只能由用户确认后查询。
        foreach (var vm in Records.Where(r => r.Record.Source == ProbeRecordSource.IdentityInspection && !r.ManualResumeRequired
                                              && r.IsPollable && !r.IsAutoPolling && !_autoStarted.Contains(r.LocalId)).ToList())
        {
            StartAutoPolling(vm, immediate: true);
            started++;
        }

        return started;
    }

    public enum WaitOutcome
    {
        /// <summary>记录已进入终态（含失败、中止、过期，由调用方按状态区分）。</summary>
        Terminal,
        /// <summary>本地等待上限已到。远端任务不视为已取消，只能之后恢复查询。</summary>
        LocalLimit,
        /// <summary>本记录的自动查询已停止（暂停、连续失败、断网或用户停止），结果未知。</summary>
        Stopped,
        /// <summary>查询被限流（HTTP 429）。</summary>
        RateLimited,
        /// <summary>没有远端任务标识或当前状态无法查询，结果未知。</summary>
        Unpollable,
        Cancelled,
    }

    /// <summary>
    /// 复用本协调器的单飞轮询等待一条记录结束：不另开查询通道，因此与鉴别记录窗口的恢复/停止共用同一个循环，不会重复查询。
    /// limit 只是本地等待上限：到达后暂停本记录的 GET 轮询（不再读取进度），但不取消远端任务，也不标记为用户停止，之后可在鉴别记录中恢复查询。UI 线程调用；取消只终止等待。
    /// </summary>
    public async Task<WaitOutcome> WaitForSettledAsync(ProbeRecordViewModel vm, TimeSpan limit, CancellationToken cancellationToken)
    {
        if (vm.IsTerminal)
        {
            return WaitOutcome.Terminal;
        }

        if (!vm.IsPollable)
        {
            return WaitOutcome.Unpollable;
        }

        if (vm.ManualResumeRequired && !vm.IsAutoPolling)
        {
            // 迁移导入的记录不由后台等待自动启动查询，只能用户确认后手动恢复。
            return WaitOutcome.Stopped;
        }

        if (!vm.IsAutoPolling)
        {
            StartAutoPolling(vm, immediate: false);
        }

        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return WaitOutcome.Cancelled;
            }

            if (vm.IsTerminal)
            {
                return WaitOutcome.Terminal;
            }

            if (_lastPollCodes.TryGetValue(vm.LocalId, out var code) && code is "Http429" or "ConnectionRateLimited")
            {
                // 限流：暂停当前记录的 GET，不让后台循环继续退避重试；Core 保存的主机冷却照常生效，之后只能手动恢复。
                PausePolling(vm, "查询被限流（HTTP 429 或官方冷却中），已暂停自动查询并遵守官方冷却，不会自动恢复。远端检测不会因此停止，也不保证停止计费，结果按“未知”处理；冷却结束后可手动“恢复自动查询”，不会重新提交。");
                return WaitOutcome.RateLimited;
            }

            if (!vm.IsPollable)
            {
                return WaitOutcome.Unpollable;
            }

            if (!vm.IsAutoPolling && !vm.IsPolling)
            {
                return WaitOutcome.Stopped;
            }

            if (DateTimeOffset.UtcNow - started >= limit)
            {
                PausePolling(vm, "本地等待已达 10 分钟上限，已暂停自动查询。远端检测不会因此停止，也不保证停止计费，结果按“未知”处理；可在“恢复自动查询”后继续读取进度，不会重新提交。");
                return WaitOutcome.LocalLimit;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return WaitOutcome.Cancelled;
            }
        }
    }

    /// <summary>
    /// 该记录是否还在等待自动查询的结果：可查询、没有被用户停止，且自动查询正在运行或尚未启动。
    /// 启动过又不再运行（暂停、失败、网络中断）即不再等待，结果按“未知”处理。
    /// </summary>
    public bool IsAwaitingAutoResult(ProbeRecordViewModel vm)
        => vm.IsPollable && !_userStopped.Contains(vm.LocalId) && (vm.IsAutoPolling || !_autoStarted.Contains(vm.LocalId));

    public void Forget(Guid localId, bool raise = true)
    {
        _autoStarted.Remove(localId);
        _userStopped.Remove(localId);
        _lastPollCodes.Remove(localId);
        if (_loops.Remove(localId, out var cts))
        {
            cts.Cancel();
        }

        if (_index.Remove(localId, out var vm))
        {
            vm.IsAutoPolling = false;
            Records.Remove(vm);
        }

        if (raise)
        {
            RecordsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ReloadAsync()
    {
        var history = await _service.GetHistoryAsync(_lifetime.Token);
        MergeHistory(history);
    }

    /// <summary>
    /// 提交一次检测。只调用一次 StartProbeAsync，绝不重试。返回本地记录（可能为提交未知或失败状态）。
    /// </summary>
    public async Task<ProbeRecordViewModel?> SubmitAsync(Guid profileId, string modelId, string? claimedModel, OutboundConsent consent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isSubmitting)
        {
            throw new ClientOperationException("SubmissionBusy", "已有提交正在进行，请勿重复点击。");
        }

        _isSubmitting = true;
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        try
        {
            var task = _service.StartProbeAsync(profileId, modelId, claimedModel, consent, _lifetime.Token);
            TrackCore(task);
            ProbeRecord record;
            try
            {
                record = await task;
            }
            catch (ClientOperationException)
            {
                // Core 可能已把本地记录更新为“提交结果未知”后再抛出（HTTP 错误）。同步本地历史后再交给调用方提示。
                await TryReloadQuietlyAsync();
                throw;
            }

            var vm = Track(record);
            if (vm.State == LocalRunState.Running && vm.IsPollable)
            {
                StartAutoPolling(vm, immediate: false);
            }
            else if (vm.State == LocalRunState.SubmissionUnknown)
            {
                vm.PollNote = vm.IsPollable
                    ? "提交结果未知，但已有任务标识。可以手动恢复查询；不会重新提交。"
                    : "提交结果未知且没有任务标识。请勿重复提交；远端可能已开始检测并产生费用。";
            }

            return vm;
        }
        finally
        {
            _isSubmitting = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>找出最近一次提交产生的记录（用于提交抛错后定位）。</summary>
    public ProbeRecordViewModel? LatestFor(Guid profileId, string modelId, DateTimeOffset after)
        => Records.Where(r => r.ProfileId == profileId && r.ModelId == modelId && r.CreatedAt >= after)
                  .OrderByDescending(r => r.CreatedAt).FirstOrDefault();

    public void Resume(ProbeRecordViewModel vm)
    {
        if (!vm.IsPollable)
        {
            vm.PollNote = vm.HasRemoteId ? "该记录已结束，无需查询。" : "没有远端任务标识，无法查询；不会重新提交。";
            return;
        }

        if (_disposed || _loops.ContainsKey(vm.LocalId) || !ConfirmManualResume(vm, continuous: true))
        {
            return;
        }

        StartAutoPolling(vm, immediate: true, manualConfirmed: true);
    }

    public void StartAutoPolling(ProbeRecordViewModel vm, bool immediate) => StartAutoPolling(vm, immediate, manualConfirmed: false);

    /// <param name="manualConfirmed">迁移导入的记录（ManualResumeRequired）只有在用户刚确认接收方后才允许启动。</param>
    private void StartAutoPolling(ProbeRecordViewModel vm, bool immediate, bool manualConfirmed)
    {
        if (_disposed || _loops.ContainsKey(vm.LocalId) || !_index.ContainsKey(vm.LocalId))
        {
            return;
        }

        if (vm.ManualResumeRequired && !manualConfirmed)
        {
            vm.PollNote = ManualResumeNote;
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loops[vm.LocalId] = cts;
        _autoStarted.Add(vm.LocalId);
        _userStopped.Remove(vm.LocalId);
        _lastPollCodes.Remove(vm.LocalId);
        vm.IsAutoPolling = true;
        vm.PollNote = "自动查询中：每 5 秒读取一次进度（同一时间仅一个请求）。";
        RecordsChanged?.Invoke(this, EventArgs.Empty);
        _ = RunLoopAsync(vm, cts, immediate);
    }

    public void StopAutoPolling(ProbeRecordViewModel vm, bool userInitiated)
    {
        if (_loops.Remove(vm.LocalId, out var cts))
        {
            cts.Cancel();
        }

        if (userInitiated)
        {
            _userStopped.Add(vm.LocalId);
        }

        vm.IsAutoPolling = false;
        RecordsChanged?.Invoke(this, EventArgs.Empty);
        if (userInitiated)
        {
            vm.PollNote = "已停止本地查询。远端检测不会因此停止，也不保证停止计费；之后可随时恢复查询。";
            _dialogs.Info("已停止本地查询",
                "已停止本程序对该任务的自动查询。\n\n注意：这只停止本地轮询，BazaarLink 上的远端检测不会因此停止，也不保证停止计费。之后可以随时“恢复自动查询”。");
        }
    }

    /// <summary>
    /// 身份巡检等待中暂停这条记录的自动 GET 轮询（本地等待上限或 429 限流）：不弹窗、不计入“用户停止”，远端任务不视为已取消。
    /// 记录因此不再等待自动结果，身份类通知会把它按“结果未知”处理；用户仍可手动恢复查询（不会重新提交）。
    /// </summary>
    private void PausePolling(ProbeRecordViewModel vm, string note)
    {
        if (_loops.Remove(vm.LocalId, out var cts))
        {
            cts.Cancel();
        }

        vm.IsAutoPolling = false;
        if (!vm.IsTerminal)
        {
            vm.PollNote = note;
        }

        RecordsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PollOnceAsync(ProbeRecordViewModel vm)
    {
        if (_disposed)
        {
            return;
        }

        if (vm.IsPollable && !vm.IsAutoPolling && !ConfirmManualResume(vm, continuous: false))
        {
            return;
        }

        var outcome = await PollCoreAsync(vm, _lifetime.Token);
        switch (outcome.Kind)
        {
            case PollKind.Updated:
                if (!vm.IsAutoPolling)
                {
                    vm.PollNote = $"已手动查询一次（{Format.Time(DateTimeOffset.Now)}）。自动查询未开启。";
                }

                break;
            case PollKind.Terminal:
                vm.PollNote = $"任务已结束：{vm.StateText}。";
                break;
            case PollKind.Failure:
            case PollKind.Paused:
                vm.PollNote = "本次查询失败：" + outcome.Message;
                break;
            case PollKind.NotPollable:
                vm.PollNote = outcome.Message ?? "当前记录无法查询。";
                break;
        }
    }

    // ---- 内部实现 ----

    private enum PollKind { Skipped, Updated, Terminal, Failure, Paused, Cancelled, NotPollable }

    private readonly record struct PollOutcome(PollKind Kind, string? Message = null, TimeSpan? RetryAfter = null);

    private async Task<PollOutcome> PollCoreAsync(ProbeRecordViewModel vm, CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested || !_index.ContainsKey(vm.LocalId))
        {
            return new(PollKind.Cancelled);
        }

        if (!vm.IsPollable)
        {
            return vm.IsTerminal
                ? new(PollKind.Terminal)
                : new(PollKind.NotPollable, vm.HasRemoteId ? "当前状态无法查询。" : "没有远端任务标识，无法查询；不会重新提交。");
        }

        if (vm.IsPolling)
        {
            return new(PollKind.Skipped);
        }

        vm.IsPolling = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(PollTimeout);
        try
        {
            var task = _service.PollProbeAsync(vm.LocalId, timeout.Token);
            TrackCore(task);
            var record = await task;
            if (!_index.ContainsKey(vm.LocalId))
            {
                return new(PollKind.Cancelled);
            }

            vm.Apply(record);
            _lastPollCodes.Remove(vm.LocalId);
            RecordsChanged?.Invoke(this, EventArgs.Empty);
            if (token.IsCancellationRequested)
            {
                return new(PollKind.Cancelled);
            }

            if (vm.IsTerminal)
            {
                return new(PollKind.Terminal);
            }

            return record.State switch
            {
                LocalRunState.PollingInterrupted => new(PollKind.Failure, UserMessages.Redact(record.Error) is { Length: > 0 } e ? e : "查询中断。"),
                LocalRunState.IncompatibleResponse => new(PollKind.Paused, "服务响应结构不兼容，已暂停自动查询。可稍后手动查询。"),
                _ => new(PollKind.Updated),
            };
        }
        catch (Exception) when (token.IsCancellationRequested || _disposed)
        {
            return new(PollKind.Cancelled);
        }
        catch (ClientOperationException ex)
        {
            _lastPollCodes[vm.LocalId] = ex.Code;
            await TryReloadOneAsync(vm);
            var message = UserMessages.FormatClient(ex);
            // ConnectionRateLimited：Core 在官方主机冷却期内拒绝发 GET，不是网络失败，不能退避重试，需等冷却结束后手动恢复。
            var permanent = ex.Code is "NoRunId" or "RecordNotFound" or "ConnectionRateLimited"
                || (ex.Code.StartsWith("Http4", StringComparison.Ordinal) && ex.Code is not ("Http429" or "Http408"));
            return permanent ? new(PollKind.Paused, message) : new(PollKind.Failure, message, ex.RetryAfter);
        }
        catch (OperationCanceledException)
        {
            await TryReloadOneAsync(vm);
            return new(PollKind.Failure, "查询超时。");
        }
        catch (Exception ex)
        {
            return new(PollKind.Paused, UserMessages.Describe(ex));
        }
        finally
        {
            vm.IsPolling = false;
        }
    }

    private async Task RunLoopAsync(ProbeRecordViewModel vm, CancellationTokenSource cts, bool immediate)
    {
        var token = cts.Token;
        var failures = 0;
        var delay = immediate ? TimeSpan.Zero : PollInterval;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token);
                }

                delay = PollInterval;
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    vm.PollNote = "检测到网络不可用，已暂停自动查询。远端检测不受影响；网络恢复后请手动点击“恢复自动查询”。";
                    return;
                }

                var outcome = await PollCoreAsync(vm, token);
                if (token.IsCancellationRequested)
                {
                    // 已被暂停或停止（例如身份巡检遇到 429）：不再用“稍后重试”之类的提示覆盖暂停说明。
                    return;
                }

                switch (outcome.Kind)
                {
                    case PollKind.Cancelled:
                        return;
                    case PollKind.Terminal:
                        vm.PollNote = $"任务已结束：{vm.StateText}。自动查询已停止。";
                        return;
                    case PollKind.NotPollable:
                        vm.PollNote = outcome.Message ?? "当前记录无法查询，自动查询已停止。";
                        return;
                    case PollKind.Paused:
                        vm.PollNote = "已暂停自动查询：" + outcome.Message + "\n远端检测不受影响；确认问题后可手动恢复。";
                        return;
                    case PollKind.Updated:
                        failures = 0;
                        vm.PollNote = $"自动查询中：每 5 秒读取一次进度。上次查询 {Format.Time(DateTimeOffset.Now)}。";
                        break;
                    case PollKind.Skipped:
                        break;
                    case PollKind.Failure:
                        failures++;
                        if (failures >= MaxConsecutiveFailures)
                        {
                            vm.PollNote = $"连续 {failures} 次查询失败，已暂停自动查询：{outcome.Message}\n远端检测可能仍在进行；请检查网络后手动点击“恢复自动查询”。";
                            return;
                        }

                        delay = TimeSpan.FromTicks(PollInterval.Ticks * (failures + 1));
                        if (outcome.RetryAfter is { } retry && retry > delay)
                        {
                            if (retry > MaxRetryAfter)
                            {
                                vm.PollNote = $"服务要求等待 {UserMessages.FormatDuration(retry)} 后再查询，已暂停自动查询。请稍后手动恢复。";
                                return;
                            }

                            delay = retry;
                        }

                        vm.PollNote = $"查询失败（第 {failures} 次），{UserMessages.FormatDuration(delay)}后重试：{outcome.Message}";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 被停止或程序退出。
        }
        catch (Exception ex)
        {
            vm.PollNote = "已暂停自动查询：" + UserMessages.Describe(ex);
        }
        finally
        {
            if (_loops.TryGetValue(vm.LocalId, out var current) && ReferenceEquals(current, cts))
            {
                _loops.Remove(vm.LocalId);
                vm.IsAutoPolling = false;
                if (!_disposed)
                {
                    RecordsChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            cts.Dispose();
        }
    }

    private async Task TryReloadOneAsync(ProbeRecordViewModel vm)
    {
        try
        {
            var history = await _service.GetHistoryAsync(_lifetime.Token);
            var match = history.FirstOrDefault(h => h.LocalId == vm.LocalId);
            if (match is not null && _index.ContainsKey(vm.LocalId))
            {
                vm.Apply(match);
                RecordsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // 仅用于同步显示，失败时保持当前界面。
        }
    }

    private async Task TryReloadQuietlyAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch
        {
            // 同上。
        }
    }

    private void TrackCore(Task task)
    {
        _pendingCore.Add(task);
        task.ContinueWith(
            t =>
            {
                _ = t.Exception; // 标记为已观察。
                _pendingCore.Remove(t);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 取消全部本地查询与进行中的提交，并短暂等待 Core 把“查询中断/提交未知”写入本地，避免记录停留在过时状态。
    /// 远端检测不会因此停止。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _loops.Clear();
        foreach (var vm in Records)
        {
            vm.IsAutoPolling = false;
        }

        var pending = _pendingCore.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length > 0)
        {
            try
            {
                // Core 内部全部使用 ConfigureAwait(false)，在 UI 线程上有限等待不会死锁。
                Task.WaitAll(pending, TimeSpan.FromSeconds(3));
            }
            catch
            {
                // 取消或失败均可接受。
            }
        }
    }
}
