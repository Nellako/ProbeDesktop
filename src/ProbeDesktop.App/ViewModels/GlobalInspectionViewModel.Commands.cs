using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    private async Task ToggleAsync()
    {
        if (!await _opLock.WaitAsync(0, _lifetime.Token)) return;
        try
        {
            var enable = !Enabled;
            var saved = await _service!.SaveInspectionSettingsAsync(new() { Enabled = enable }, cancellationToken: _lifetime.Token);
            _snapshotEpoch++;
            if (!enable)
            {
                _scheduler.Stop();
                _manualCts?.Cancel();
            }
            ApplySnapshot(saved, reloadSelection: !_selectionDirty, allowPrune: false);
            Status = enable ? "自动巡检已开启。" : "自动巡检已关闭。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status = "未能更改开关：" + UserMessages.Describe(ex); }
        finally { _opLock.Release(); }
    }

    private async Task ConfirmScopeAsync()
    {
        if (!await _opLock.WaitAsync(0, _lifetime.Token)) return;
        try
        {
            if (_snapshot is not { } snap || !InspectionScheduler.TryParseInterval(IntervalText, out var minutes)) return;
            var selection = _selection.Snapshot().Select(x => new InspectionSelection(x.ProfileId, x.Included, x.ModelIds)).ToArray();
            var scope = snap.Targets.Select(t => t.Target with
            {
                Models = t.Target.Models.Where(m => _selection.IsIncluded(t.Target.ProfileId) && _selection.IsModelSelected(t.Target.ProfileId, m)).ToArray()
            }).Where(t => t.Models.Count > 0).ToArray();
            var retries = MaxRetries;
            var draftRevision = _draftRevision;
            var consent = new InspectionConsent(scope, minutes, _time.GetUtcNow()) { SettingsRevision = snap.SettingsRevision, MaxAttempts = retries + 1 };
            var text = ConnectionScopeText.Build(scope, minutes, t => snap.Targets.Single(x => x.Target.ProfileId == t.ProfileId).ProfileName, retries + 1);
            if (!_dialogs.ConfirmInspectionSettings("确认连接巡检设置", text)) { Status = "未保存，继续使用已确认的设置。"; return; }
            var saved = await _service!.ConfirmInspectionSettingsAsync(selection, consent, _lifetime.Token);
            _snapshotEpoch++;
            bool unchanged = draftRevision == _draftRevision;
            if (unchanged) _selectionDirty = false;
            ApplySnapshot(saved, reloadSelection: unchanged, allowPrune: false);
            Status = scope.Length == 0 ? "已保存空范围，不安排下一轮。" : "已保存巡检设置。当前轮不受影响。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status = "保存巡检设置失败，原设置未变：" + UserMessages.Describe(ex); }
        finally { _opLock.Release(); }
    }

    private async Task RunOnceAsync()
    {
        if (IntervalError.Length > 0) { Status = "请先修正巡检间隔，本次未发送请求。"; return; }
        if (HasDraft) { Status = "请先确认巡检设置，本次未发送请求。"; return; }
        if (_snapshot is not { } snap || InspectionScope.AuthorizedSelectedCount(snap) == 0)
        { Status = "没有可巡检的已授权模型。"; return; }
        if (_opLock.CurrentCount == 0 || _busy) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _manualCts = cts;
        try
        {
            var outcome = await RunRoundAsync(InspectionTrigger.Manual, cts.Token);
            if (outcome.Fatal is { } error) Status = "未能完成本轮巡检：" + UserMessages.Describe(error);
        }
        finally { _manualCts = null; }
    }
}
