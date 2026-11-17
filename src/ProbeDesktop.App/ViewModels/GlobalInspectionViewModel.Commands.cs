using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    private Task ToggleAsync() => Enabled ? DisableAsync() : EnableAsync();

    /// <summary>关闭无需确认：先停本机计时并取消手动轮（Core 保存关闭时也会取消在途轮），不等待网络。</summary>
    private async Task DisableAsync()
    {
        _scheduler.Stop();
        try { _manualCts?.Cancel(); } catch (ObjectDisposedException) { }
        OnPropertyChanged(nameof(StateText));
        try
        {
            await _opLock.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var snap = await _service!.SaveInspectionSettingsAsync(
                new InspectionSettings { Enabled = false, IntervalMinutes = _savedInterval }, null, _lifetime.Token);
            ApplySnapshot(snap, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            Enabled = false;
            Status = "已关闭全局巡检，在途的一轮已取消。已发出的请求无法撤回。手动单个连接测试仍可使用。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Enabled = false;
            Status = "本机巡检已停止，但保存关闭状态失败：" + UserMessages.Describe(ex) + " 下次启动可能恢复，请重试关闭。";
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task EnableAsync()
    {
        if (_savedInterval is < InspectionScheduler.MinIntervalMinutes or > InspectionScheduler.MaxIntervalMinutes)
        {
            Status = $"已保存的间隔 {_savedInterval} 分钟不在 {InspectionScheduler.MinIntervalMinutes}–{InspectionScheduler.MaxIntervalMinutes} 范围内。请先输入有效间隔并点“保存间隔”，未开启。";
            return;
        }

        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            var scope = await PrepareScopeAsync();
            if (scope is null)
            {
                return;
            }

            var text = BuildScopeText(scope, _savedInterval) + "\n\n确认后会立即巡检一轮，之后每轮结束等待一个完整间隔再开始下一轮。";
            if (!_dialogs.Confirm("确认全局连接巡检范围", text))
            {
                Status = "已取消，全局巡检保持关闭，未发送任何请求。";
                return;
            }

            var consent = NewConsent(scope, _savedInterval);
            var saved = await _service!.SaveInspectionSettingsAsync(
                new InspectionSettings { Enabled = true, IntervalMinutes = _savedInterval }, consent, _lifetime.Token);
            ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            if (!saved.Settings.Enabled)
            {
                Status = "服务未开启全局巡检，未发送任何请求。";
                return;
            }

            Status = $"已开启：每 {_savedInterval} 分钟一轮，仅巡检已授权范围，正在立即巡检一轮。";
            StartScheduler(immediate: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "开启失败，全局巡检保持关闭：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// 取得“当前所选范围”：先把界面选择写入 Core，再读 Core 快照，用 Core 的数据计算范围，
    /// 这样授权同意与 Core 的校验逐项一致。空选择返回 null 并提示，不发请求、不扩权。
    /// </summary>
    private async Task<IReadOnlyList<InspectionTarget>?> PrepareScopeAsync()
    {
        await FlushSelectionAsync();
        var snap = await FetchAsync(allowPrune: false);
        if (snap is null)
        {
            return null;
        }

        // 读取过程中若又触发了修剪回写，等它落盘后再取一次，保证范围与 Core 一致。
        if (_selectionDirty || _persisting)
        {
            await FlushSelectionAsync();
            snap = await FetchAsync(allowPrune: false) ?? snap;
        }

        var scope = InspectionScope.Selected(snap);
        if (scope.Count == 0)
        {
            Status = "尚未选择任何模型：不会发送任何请求，也没有授予任何权限。请勾选配置并展开选择要巡检的模型。";
            return null;
        }

        return scope;
    }

    /// <summary>
    /// 连接巡检授权。MaxAttempts 与确认文案披露的“每目标每轮最多 3 次”一致；
    /// Core 只接受带这个上限的授权，旧的“每轮一次”授权需要重新确认。
    /// </summary>
    private InspectionConsent NewConsent(IReadOnlyList<InspectionTarget> scope, int minutes)
        => new(scope, minutes, _time.GetUtcNow()) { MaxAttempts = ConnectionRoundDisplay.MaxAttempts };

    private string BuildScopeText(IReadOnlyList<InspectionTarget> scope, int minutes)
        => ConnectionScopeText.Build(scope, minutes,
            t => _snapshot?.Targets.FirstOrDefault(s => s.Target.ProfileId == t.ProfileId)?.ProfileName ?? "（配置）");
}
