using System.Collections.ObjectModel;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    private readonly record struct RoundOutcome(InspectionRoundSummary? Summary, Exception? Fatal, bool Skipped);

    private void StartScheduler(bool immediate)
    {
        _scheduler.TrySetInterval(_savedInterval);
        _scheduler.Start(immediate);
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(NextStartText));
    }

    /// <summary>
    /// 调度器每轮的回调：返回 true 继续等待下一轮，false 停止循环。
    /// 所选范围内没有已授权模型（InspectionNotAuthorized）只是这一轮没发请求，不关闭巡检，下一轮再看；
    /// 其他整轮致命失败才停止并保存关闭，不自动重试。
    /// </summary>
    private async Task<bool> RunScheduledRoundAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return false;
        }

        var outcome = await RunRoundAsync(InspectionTrigger.Automatic, ct);
        if (ct.IsCancellationRequested || outcome.Fatal is null)
        {
            return true;
        }

        if (outcome.Fatal is ClientOperationException { Code: "InspectionNotAuthorized" })
        {
            Status = "本轮没有已授权的所选模型，未发送任何请求；仍按间隔等待下一轮。请检查所选范围。";
            return true;
        }

        await StopAfterFatalAsync(outcome.Fatal);
        return false;
    }

    /// <summary>整轮致命失败：停止本机计时并保存关闭，提示用户；不自动重试。</summary>
    private async Task StopAfterFatalAsync(Exception fatal)
    {
        _scheduler.Stop();
        Enabled = false;
        try
        {
            var snap = await _service!.SaveInspectionSettingsAsync(
                new InspectionSettings { Enabled = false, IntervalMinutes = _savedInterval }, null, _lifetime.Token);
            ApplySnapshot(snap, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
        }
        catch (Exception)
        {
        }

        Enabled = false;
        Status = "全局巡检已停止：" + UserMessages.Describe(fatal) + " 不会自动重试，请检查后手动重新开启。";
    }

    /// <summary>串行执行一轮。已有一轮在进行时直接跳过，不排队、不补跑。单项失败由 Core 记录并继续。</summary>
    private async Task<RoundOutcome> RunRoundAsync(InspectionTrigger trigger, CancellationToken ct)
    {
        if (!await _roundGate.WaitAsync(0, CancellationToken.None))
        {
            return new RoundOutcome(null, null, true);
        }

        IsBusy = true;
        _lastNotifiedDone = -1;
        var progress = new Progress<InspectionRoundSummary>(OnProgress);
        try
        {
            var summary = await _service!.RunInspectionRoundAsync(trigger, progress, ct);
            _round = summary;
            Status = summary.Cancelled
                ? "本轮巡检已取消。" + CountsTail(summary)
                : $"{(trigger == InspectionTrigger.Manual ? "立即巡检" : "定时巡检")}完成：{FormatCounts(summary)}。";
            // 只有自动轮次才通知；手动不发。通知故障由协调器吞掉，不影响本轮结果。
            if (trigger == InspectionTrigger.Automatic && !summary.Cancelled && !_disposed && _notifier is not null)
            {
                _ = NotifyRoundAsync(summary.RoundId);
            }

            return new RoundOutcome(summary, null, false);
        }
        catch (OperationCanceledException)
        {
            Status = "本轮巡检已取消。";
            return new RoundOutcome(null, null, false);
        }
        catch (ClientOperationException ex) when (ex.Code == "RoundBusy")
        {
            Status = "已有一轮巡检在进行，本次跳过，不会补跑。";
            return new RoundOutcome(null, null, true);
        }
        catch (Exception ex)
        {
            return new RoundOutcome(null, ex, false);
        }
        finally
        {
            _roundGate.Release();
            IsBusy = false;
            OnRoundChanged();
            if (!_disposed && !_lifetime.IsCancellationRequested)
            {
                _ = FetchAsync(allowPrune: true);
                _feed?.NotifyConnection();
            }
        }
    }

    /// <summary>
    /// 自动轮次结束后的问题通知：读取本轮的本机连接记录和最新快照，构造逐目标观察交给协调器。
    /// 全程吞掉异常，不影响轮次结果、状态文本或授权；手动轮次不会走到这里。
    /// </summary>
    private async Task NotifyRoundAsync(Guid roundId)
    {
        try
        {
            if (_notifier is null || _service is null)
            {
                return;
            }

            var history = await _service.GetConnectionHistoryAsync(_lifetime.Token);
            var snapshot = await _service.GetInspectionSnapshotAsync(_lifetime.Token);
            var items = ProblemObservations.FromConnectionRound(roundId, history, snapshot);
            await _notifier.ObserveAutomaticAsync(NotificationCategory.Connection, items, _lifetime.Token);
        }
        catch (Exception)
        {
            // 通知故障不影响检测。
        }
    }

    private void OnProgress(InspectionRoundSummary p)
    {
        // 轮已结束后到达的过时进度不应覆盖最终结果。
        if (!_busy && _round is { CompletedAt: not null } done && done.RoundId == p.RoundId)
        {
            return;
        }

        _round = p;
        OnRoundChanged();
        // 每完成一个模型（成功/失败/跳过数变化）就通知模型页刷新“最近结果”，并重载本轮结果列表。
        var finished = FinishedCount(p);
        if (finished != _lastNotifiedDone)
        {
            _lastNotifiedDone = finished;
            _feed?.NotifyConnection();
        }
    }

    private void OnRoundChanged()
    {
        OnPropertyChanged(nameof(HasRound));
        OnPropertyChanged(nameof(RoundStatusText));
        OnPropertyChanged(nameof(RoundCountsText));
        OnPropertyChanged(nameof(CurrentItemText));
        RequestResultsReload();
    }

    // ---- 最新一轮的逐模型结果（只显示最新一轮，每个目标显示本轮最高状态结果；各次尝试的完整历史在连接记录窗口）----

    private void RequestResultsReload()
    {
        if (_disposed || _service is null)
        {
            return;
        }

        _resultsAgain = true;
        if (_resultsLoading)
        {
            return;
        }

        _resultsLoading = true;
        _ = ResultsLoopAsync();
    }

    private async Task ResultsLoopAsync()
    {
        try
        {
            while (_resultsAgain && !_disposed)
            {
                _resultsAgain = false;
                var round = _round;
                if (round is null)
                {
                    RoundResults = [];
                    ResultsNote = "";
                    continue;
                }

                IReadOnlyList<ConnectionTestResult> history;
                try
                {
                    history = await _service!.GetConnectionHistoryAsync(_lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    ResultsNote = "无法读取本机连接记录，暂时无法显示各模型结果。";
                    continue;
                }

                // 读取期间又切换到了更新的一轮：丢弃这次结果，由已排队的下一次重载处理。
                if (_round?.RoundId != round.RoundId)
                {
                    continue;
                }

                // 每个目标最多尝试 3 次：这里按精确目标显示本轮已完成尝试中的最高状态（同级取最新），各次尝试仍独立保留在连接记录。
                var rows = ConnectionRoundDisplay.LatestPerTarget(history, round.RoundId)
                    .Select(ToRow)
                    .ToList();
                RoundResults = new ObservableCollection<RoundResultRow>(rows);
                ResultsNote = rows.Count == 0
                    ? "本轮暂无模型结果记录（尚未完成第一个模型，或被跳过的模型不产生记录）。只显示最新一轮，完整历史请到“连接记录”。"
                    : $"只显示最新一轮各模型在本轮的最高状态结果（成功 ＞ 成功（可能）＞ 失败，同级取最新，标记读取跟随该代表记录），按代表记录开始时间倒序；每个模型每轮最多尝试 {ConnectionRoundDisplay.MaxAttempts} 次，其余尝试不单独列出也不重复计入本轮统计。被跳过的模型不产生记录。各次尝试的完整历史请到“连接记录”。";
            }
        }
        finally
        {
            _resultsLoading = false;
        }
    }

    /// <summary>取消时附带已有的计数，让用户看到取消前实际得到的结论。</summary>
    private static string CountsTail(InspectionRoundSummary s) => " 取消前：" + FormatCounts(s) + "。";

    private RoundResultRow ToRow(RoundTargetResult item)
    {
        var r = item.Record;
        string text;
        string tone;
        if (r.CompletedAt is null)
        {
            (text, tone) = _busy ? ("进行中", "none") : ("失败", "bad");
        }
        else
        {
            // 与连接记录窗口、模型页共用同一套分类；中间阶段与未识别阶段显示阶段名，不当作成功，也不武断判为失败。
            var outcome = LatestResultPicker.Classify(r);
            text = outcome == LatestResultPicker.ConnectionOutcome.Incomplete
                ? ConnectionHistoryViewModel.StageText(r.Stage)
                : LatestResultPicker.OutcomeText(outcome);
            tone = LatestResultPicker.OutcomeTone(outcome);
        }

        string contextText;
        string contextTone;
        if (r.CompletedAt is null)
        {
            (contextText, contextTone) = ("—", "none");
        }
        else
        {
            contextText = ProtocolInfo.ContextLabel(r.ContextCheck);
            contextTone = ProtocolInfo.ContextTone(r.ContextCheck);
        }

        var detail = UserMessages.Redact(r.Message);
        if (r.CompletedAt is not null && r.ContextCheck == ConnectionContextCheck.NotChecked && !detail.Contains("标记", StringComparison.Ordinal))
        {
            detail = (detail.Trim() + " 旧记录未检验标记读取，不追溯补判。").Trim();
        }

        if (r.HttpStatusCode is { } http)
        {
            detail = $"HTTP {http}　" + detail;
        }

        if (!string.IsNullOrWhiteSpace(r.Code) && r.Stage != ConnectionTestStage.ModelResponded)
        {
            detail = $"[{UserMessages.Redact(r.Code)}] " + detail;
        }

        var note = ConnectionRoundDisplay.AttemptNote(item);
        if (note.Length > 0)
        {
            detail = note + " " + detail;
        }

        return new RoundResultRow(
            r.ProfileName,
            r.ModelId,
            text,
            tone,
            contextText,
            contextTone,
            (r.CompletedAt ?? r.StartedAt).ToLocalTime().ToString("HH:mm:ss"),
            detail.Trim());
    }
}
