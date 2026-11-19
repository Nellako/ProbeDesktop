using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class IdentityInspectionViewModel
{
    private async Task ToggleAsync()
    {
        if (!await _opLock.WaitAsync(0, _lifetime.Token)) return;
        try
        {
            bool enable = !Enabled;
            var saved = await _service!.SaveIdentityInspectionSettingsAsync(new() { Enabled = enable }, cancellationToken: _lifetime.Token);
            _snapshotEpoch++;
            if (!enable) { _scheduler.Stop(); _roundCts?.Cancel(); }
            ApplySnapshot(saved, reloadSelection: !_selectionDirty, allowPrune: false);
            Status = enable ? "自动巡检已开启。" : "自动巡检已关闭。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status = "未能更改开关：" + UserMessages.Describe(ex); }
        finally { _opLock.Release(); }
    }

    private static string BuildScopeText(IdentityInspectionSnapshot snap, IReadOnlyList<IdentityInspectionTarget> scope, int minutes)
    {
        if (scope.Count == 0) return $"范围为空。保存 {minutes} 分钟间隔，不授予发送权限，也不改变开关。";
        var listing = string.Join("\n", scope.Select((t, i) =>
            $"{i + 1}. {snap.Targets.First(x => x.Target == t).ProfileName} · {t.ModelId}\n    {t.BaseUrl} · {ProtocolInfo.Display(t.Protocol)} · Key 版本 {t.KeyRevision}"));
        return $"间隔：{minutes} 分钟。每轮对以下 {scope.Count} 个目标各提交至多 1 次，不自动重试。\n\n{listing}\n\n" +
            "提交会将完整 API Key、目标地址、模型和协议发给 BazaarLink（https://bazaarlink.ai/api/probe/run），可能产生费用，报告可能公开。此处不显示密钥值。";
    }

    private async Task ConfirmScopeAsync()
    {
        if (!await _opLock.WaitAsync(0, _lifetime.Token)) return;
        try
        {
            if (_snapshot is not { } snap || !InspectionScheduler.TryParseInterval(IntervalText, out var minutes)) return;
            var selection = _selection.Snapshot().Select(x => new IdentityInspectionSelection(x.ProfileId, x.Included, x.ModelIds)).ToArray();
            var scope = snap.Targets.Select(x => x.Target).Where(t => _selection.IsIncluded(t.ProfileId) && _selection.IsModelSelected(t.ProfileId, t.ModelId)).ToArray();
            var revision = _draftRevision;
            var consent = new IdentityInspectionConsent(scope, minutes, _time.GetUtcNow()) { SettingsRevision = snap.SettingsRevision };
            var text = BuildScopeText(snap, scope, minutes) + "\n\n保存不改变自动开关，当前轮继续使用原设置。";
            if (!_dialogs.ConfirmInspectionSettings("确认身份识别巡检设置", text)) { Status = "未保存，继续使用已确认的设置。"; return; }
            var saved = await _service!.ConfirmIdentityInspectionSettingsAsync(selection, consent, _lifetime.Token);
            _snapshotEpoch++;
            bool unchanged = revision == _draftRevision;
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
        if (IntervalError.Length > 0) { Status = "请先修正巡检间隔，本次未提交任务。"; return; }
        if (HasDraft) { Status = "请先确认巡检设置，本次未提交任务。"; return; }
        if (_busy || !await _opLock.WaitAsync(0, _lifetime.Token)) return;
        IdentityInspectionConsent? consent = null;
        _manualPromptOpen = true;
        RefreshSchedule();
        try
        {
            if (_snapshot is not { } snap) return;
            var scope = snap.Targets.Where(t => t.Selected && t.Authorized && !t.Paused).Select(t => t.Target).ToArray();
            if (scope.Length == 0) { Status = "没有可巡检的已授权模型。"; return; }
            var approval = new IdentityInspectionConsent(scope, snap.Settings.IntervalMinutes, _time.GetUtcNow()) { SettingsRevision = snap.SettingsRevision };
            if (!_dialogs.Confirm("确认立即巡检一轮", BuildScopeText(snap, scope, snap.Settings.IntervalMinutes) + "\n\n仅运行这一轮，不改变自动开关。"))
            { Status = "已取消，未提交任务。"; return; }
            consent = approval;
        }
        finally
        {
            _opLock.Release();
            _manualPromptOpen = false;
            if (consent is null) RefreshSchedule();
        }
        if (consent is not null) await RunRoundAsync(IdentityInspectionTrigger.Manual, consent, _lifetime.Token);
    }
}
