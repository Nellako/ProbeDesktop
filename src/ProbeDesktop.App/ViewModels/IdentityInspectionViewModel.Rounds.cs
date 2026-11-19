using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class IdentityInspectionViewModel
{
    private void RefreshSchedule()
    {
        if (!_loaded || _disposed || _snapshot is not { } snap) return;
        _scheduler.UpdateSchedule(snap.Settings.IntervalMinutes, snap.LastCompletedAt,
            Enabled && AuthorizedSelectedCount(snap) > 0, _busy || _manualPromptOpen || _runs.Records.Any(r => r.IsActive), snap.RetryNotBefore);
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(NextStartText));
    }

    /// <summary>
    /// 调度器每轮的回调。单轮失败不会重试：已有的状态由 Core 持久化，之后仍按完整间隔等待下一轮。
    /// 定时已被关闭时停止本机计时。
    /// </summary>
    private async Task<bool> RunScheduledRoundAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        var keepGoing = await RunRoundAsync(IdentityInspectionTrigger.Automatic, null, ct);
        return keepGoing && !_disposed;
    }

    /// <summary>
    /// 串行执行一轮：Core 冻结目标清单，宿主逐个提交；当前目标明确结束后才处理下一个。
    /// 已有轮次在进行时直接跳过，不排队、不补跑。取消只终止本地等待，已发送的任务无法撤回。
    /// 本地等待超时、结果未知、查询暂停、限流或没有可查询标识时停止本轮剩余目标；
    /// 明确结束的失败（失败、已中止）继续下一个，身份不符或警示是检测结果，不阻止轮次继续。
    /// 手动与定时共用 Core 的最终提交闸门。返回 false 表示应停止定时计时。
    /// </summary>
    private async Task<bool> RunRoundAsync(IdentityInspectionTrigger trigger, IdentityInspectionConsent? consent, CancellationToken ct)
    {
        if (!await _runGate.WaitAsync(0, CancellationToken.None))
        {
            Status = "已有身份识别巡检在进行，本次跳过，不会补跑。";
            return true;
        }

        IsBusy = true;
        RefreshSchedule();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _roundCts = linked;
        var token = linked.Token;
        IdentityInspectionRoundSummary? round = null;
        string? stop = null;
        Exception? fatal = null;
        var cancelled = false;
        var keepGoing = true;
        try
        {
            round = trigger == IdentityInspectionTrigger.Automatic
                ? await _service!.BeginDueIdentityInspectionRoundAsync(token)
                : await _service!.BeginIdentityInspectionRoundAsync(trigger, consent, token);
            _stopRoundId = null;
            _stopNote = "";
            _round = round;
            OnRoundChanged();
            SyncRows();

            // 每一步最多处理一个目标，步数不会超过冻结清单的目标数；多留 2 步仅作保险，防止异常返回造成死循环。
            var guard = round.Total + 2;
            for (var i = 0; i < guard; i++)
            {
                token.ThrowIfCancellationRequested();
                var step = await _service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId, token);
                round = step.Round;
                _round = round;
                OnRoundChanged();
                SyncRows();
                if (step.Code == "IdentityInspectionDisabled")
                {
                    keepGoing = false;
                }

                if (step.Probe is not { } probe)
                {
                    if (!step.HasMore)
                    {
                        break;
                    }

                    continue;
                }

                // 通知只看记录自带的触发类型（Automatic），手动单次与手动一轮的记录不产生自动问题通知。
                var vm = _runs.Track(probe);
                if (!step.WaitingForResult)
                {
                    if (!step.HasMore)
                    {
                        break;
                    }

                    continue;
                }

                var outcome = await _runs.WaitForSettledAsync(vm, IdentityInspectionText.LocalWaitLimit, token);
                if (outcome == ProbeRunCoordinator.WaitOutcome.Cancelled)
                {
                    cancelled = true;
                    break;
                }

                // 已过期表示服务已无法查询该任务，不代表任务从未提交，按结果未知处理并停止本轮剩余目标。
                var expired = vm.State == LocalRunState.Expired;
                var settled = outcome == ProbeRunCoordinator.WaitOutcome.Terminal && !expired;
                var recorded = await _service.RecordIdentityInspectionResultAsync(
                    round.RoundId, probe.LocalId, localPollingLimitReached: !settled && outcome != ProbeRunCoordinator.WaitOutcome.RateLimited, _lifetime.Token);
                round = recorded.Round;
                _round = round;
                OnRoundChanged();
                SyncRows();
                if (!settled)
                {
                    stop = expired
                        ? "该目标的任务已无法查询（可能已过期），结果未知，本轮剩余目标不再提交。这不代表任务从未提交，也不会重新提交。"
                        : IdentityInspectionText.WaitStopText(outcome);
                    break;
                }

                if (!recorded.HasMore)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            fatal = ex;
            if (ex is ClientOperationException { Code: "IdentityInspectionDisabled" })
            {
                keepGoing = false;
            }
        }
        finally
        {
            if (round is not null)
            {
                try
                {
                    using var endLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var ended = await _service!.EndIdentityInspectionRoundAsync(round.RoundId, cancelled || token.IsCancellationRequested, endLimit.Token);
                    round = ended;
                    _round = ended;
                }
                catch (Exception)
                {
                    // 收尾失败时保留已展示的状态；Core 下次读取会给出已持久化的轮次。
                }

                if (!string.IsNullOrEmpty(stop))
                {
                    _stopRoundId = round.RoundId;
                    _stopNote = stop;
                }
            }

            _roundCts = null;
            if (!_disposed && !_lifetime.IsCancellationRequested) await FetchAsync(allowPrune: false);
            _runGate.Release();
            IsBusy = false;
            RefreshSchedule();
        }

        var kind = IdentityInspectionText.Kind(trigger);
        Status = fatal is not null
            ? round is null
                ? $"本次{kind}身份识别巡检没有开始，未发送任何请求：{UserMessages.Describe(fatal)}"
                : $"本轮{kind}身份识别巡检未能完成：{UserMessages.Describe(fatal)} 不会自动重试；已发送的请求无法撤回。"
            : cancelled || token.IsCancellationRequested
                ? $"本轮{kind}身份识别巡检已取消，不再提交剩余目标。已发送的请求无法撤回，也不保证停止计费。"
                : !string.IsNullOrEmpty(stop)
                    ? $"本轮{kind}身份识别巡检提前结束：{stop}"
                    : round is { StopCode: { Length: > 0 } code }
                        ? $"本轮{kind}身份识别巡检提前结束：{IdentityInspectionText.CodeText(code)}。"
                        : round is null
                            ? $"{kind}身份识别巡检没有开始。"
                            : $"{kind}身份识别巡检完成：{round.Completed}/{round.Total} 个目标，跳过 {round.Skipped} 个。";
        OnRoundChanged();
        SyncRows();
        if (!_disposed && !_lifetime.IsCancellationRequested)
        {
            // 只读恢复：对本轮产生的、尚未启动自动查询且有远端标识的记录启动 GET 查询，绝不重新提交。
            _runs.ResumeAutomaticRecords();
        }

        if (!keepGoing && !_disposed)
        {
            _scheduler.Stop();
            OnPropertyChanged(nameof(StateText));
        }

        return keepGoing;
    }

    private string RowName(Guid profileId)
        => _store.Find(profileId)?.Name
           ?? _snapshot?.Targets.FirstOrDefault(t => t.Target.ProfileId == profileId)?.ProfileName
           ?? "（已删除的配置）";

    private void OnRunsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        SyncRows();
        OnPropertyChanged(nameof(RoundCountsText));
        RefreshSchedule();
    }

    /// <summary>最新一轮各目标行：就地更新，保持键盘焦点。状态以对应鉴别记录为准。</summary>
    private void SyncRows()
    {
        var attempts = _round?.Attempts ?? [];
        var keys = attempts.Select(a => a.AttemptId).ToHashSet();
        foreach (var gone in _rowIndex.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            AttemptRows.Remove(_rowIndex[gone]);
            _rowIndex.Remove(gone);
        }

        for (var i = 0; i < attempts.Count; i++)
        {
            var a = attempts[i];
            if (!_rowIndex.TryGetValue(a.AttemptId, out var row))
            {
                row = new IdentityAttemptRow(a.AttemptId, RowName(a.Target.ProfileId), a.Target.ModelId,
                    ProtocolInfo.Display(a.Target.Protocol), OpenReport);
                _rowIndex[a.AttemptId] = row;
                AttemptRows.Insert(Math.Min(i, AttemptRows.Count), row);
            }
            else
            {
                var current = AttemptRows.IndexOf(row);
                if (current != i)
                {
                    AttemptRows.Move(current, i);
                }
            }

            var vm = a.ProbeLocalId is { } id ? _runs.Find(id) : null;
            var (state, tone, detail) = IdentityInspectionText.Describe(a, vm, vm is not null && _runs.IsAwaitingAutoResult(vm), _busy);
            row.Update(state, tone, detail, a.ProbeLocalId);
        }

        OnPropertyChanged(nameof(HasAttempts));
    }
}
