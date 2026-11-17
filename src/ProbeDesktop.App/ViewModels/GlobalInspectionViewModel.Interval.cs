using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    /// <summary>
    /// 保存间隔。开启或一轮进行中也可以保存：走 SaveInspectionIntervalAsync，Core 不取消在途轮；
    /// 保存后调度器丢弃当前等待并从现在起按新间隔重新计时。
    /// Core 的授权与间隔绑定：间隔变化会使旧授权失效。因此“已开启且当前有已授权模型”时，先弹确认并携带当前所选精确范围，
    /// 确认后这些授权按新间隔延续；取消则不保存。未开启时不弹确认，授权随之失效，之后开启或立即巡检会再次确认。
    /// </summary>
    private async Task SaveIntervalAsync()
    {
        if (!InspectionScheduler.TryParseInterval(IntervalText, out var minutes))
        {
            Status = $"间隔需为 {InspectionScheduler.MinIntervalMinutes}–{InspectionScheduler.MaxIntervalMinutes} 的整数分钟，未保存。";
            return;
        }

        if (minutes == _savedInterval)
        {
            Status = $"间隔已是 {minutes} 分钟，无需保存。";
            return;
        }

        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            await FlushSelectionAsync();
            var snap = await FetchAsync(allowPrune: false);
            if (snap is null)
            {
                return;
            }

            var scope = InspectionScope.Selected(snap);
            var carry = Enabled && scope.Count > 0 && InspectionScope.AuthorizedSelectedCount(snap) > 0;
            InspectionConsent? consent = null;
            if (carry)
            {
                var pending = InspectionScope.PendingSelectedCount(snap);
                var text =
                    $"间隔将由 {_savedInterval} 分钟改为 {minutes} 分钟。授权与间隔绑定，修改后需要对当前所选范围重新确认：\n\n" +
                    BuildScopeText(scope, minutes) + "\n\n" +
                    (pending > 0 ? $"其中 {pending} 个模型此前尚未确认，这次确认后一并纳入。\n" : "") +
                    "• 不会打断正在进行的一轮；保存后等待计时从现在重新开始。\n" +
                    "• 选“否”则不保存，间隔和授权保持不变。";
                if (!_dialogs.Confirm("确认修改巡检间隔", text))
                {
                    Status = $"已取消，间隔仍为 {_savedInterval} 分钟，授权未变。";
                    return;
                }

                consent = NewConsent(scope, minutes);
            }

            var saved = await _intervalService!.SaveInspectionIntervalAsync(minutes, consent, _lifetime.Token);
            ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            Status = Enabled
                ? $"已保存间隔 {minutes} 分钟；当前这一轮不受影响，下一次等待从现在起重新计时。"
                : $"已保存间隔 {minutes} 分钟。该间隔下的授权需在开启或立即巡检时重新确认。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "保存间隔失败，间隔未改变：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>显式确认所选范围的授权，不修改开关、重置计时或取消在途轮。</summary>
    private async Task ConfirmScopeAsync()
    {
        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            var scope = await PrepareScopeAsync();
            if (scope is null)
            {
                return;
            }

            var pending = InspectionScope.PendingSelectedCount(_snapshot!);
            if (pending == 0)
            {
                Status = "所选范围已全部确认，无需再次确认。";
                return;
            }

            var effect = Enabled
                ? "• 保持当前巡检开关和计时，不额外执行一轮，也不打断当前轮；后续请求仍按现有计划逐次复核授权。"
                : "• 仅保存范围授权，巡检保持关闭，本次不会立即执行；之后开启巡检仍须另行确认。";
            var text =
                $"当前有 {pending} 个已选模型尚未确认，将按已保存的 {_savedInterval} 分钟间隔确认以下精确范围：\n\n" +
                BuildScopeText(scope, _savedInterval) + "\n\n" + effect;
            if (!_dialogs.Confirm("确认连接巡检范围", text))
            {
                Status = "已取消，新增的模型仍未授权，不会被巡检。";
                return;
            }

            var consent = NewConsent(scope, _savedInterval);
            var saved = await _intervalService!.SaveInspectionIntervalAsync(_savedInterval, consent, _lifetime.Token);
            ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            Status = "已确认所选范围。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "确认失败，授权未改变：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>“立即巡检一轮”：只巡检所选范围；范围内有未授权模型时先确认；不调用 Probe，不改变定时开关。</summary>
    private async Task RunOnceAsync()
    {
        if (_disposed || _busy)
        {
            return;
        }

        var proceed = false;
        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            var scope = await PrepareScopeAsync();
            if (scope is null)
            {
                return;
            }

            if (InspectionScope.PendingSelectedCount(_snapshot!) > 0)
            {
                var text = BuildScopeText(scope, _savedInterval) + "\n\n此操作只巡检一轮，不会开启定时巡检。";
                if (!_dialogs.Confirm("确认立即巡检一轮", text))
                {
                    Status = "已取消，未发送任何请求。";
                    return;
                }

                var consent = NewConsent(scope, _savedInterval);
                var saved = await _intervalService!.SaveInspectionIntervalAsync(_savedInterval, consent, _lifetime.Token);
                ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            }

            proceed = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "无法记录授权，未发送任何请求：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }

        if (!proceed)
        {
            return;
        }

        _manualCts?.Dispose();
        _manualCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var outcome = await RunRoundAsync(InspectionTrigger.Manual, _manualCts.Token);
        if (outcome.Fatal is { } fatal)
        {
            Status = fatal is ClientOperationException { Code: "InspectionNotAuthorized" }
                ? "所选范围内没有已授权的模型，未发送任何请求。"
                : "本轮巡检中止：" + UserMessages.Describe(fatal);
        }
    }
}
