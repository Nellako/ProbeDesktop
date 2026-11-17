using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class IdentityInspectionViewModel
{
    private Task ToggleAsync() => Enabled ? DisableAsync() : EnableAsync();

    /// <summary>关闭无需确认：先停本机计时并取消定时轮的本地等待，不等待网络。已发送的任务无法撤回。</summary>
    private async Task DisableAsync()
    {
        _scheduler.Stop();
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
            var snap = await _service!.SaveIdentityInspectionSettingsAsync(
                new IdentityInspectionSettings { Enabled = false, IntervalMinutes = _savedInterval }, null, _lifetime.Token);
            ApplySnapshot(snap, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            Enabled = false;
            Status = "已关闭定时身份识别巡检，不再派发新的目标，并已取消定时轮的本地等待。已发送到 BazaarLink 的任务无法撤回，也不保证停止计费；可在“鉴别记录”中继续查看。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Enabled = false;
            Status = "本机计时已停止，但保存关闭状态失败：" + UserMessages.Describe(ex) + " 下次启动可能恢复，请重试关闭。";
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task EnableAsync()
    {
        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            var scope = await PrepareScopeAsync();
            if (scope is null)
            {
                return;
            }

            var text = BuildScopeText(scope, _savedInterval) +
                "\n\n确认后会立即巡检一轮，之后每轮结束等待一个完整间隔再开始下一轮；之前的授权不会沿用到这次。";
            if (!_dialogs.Confirm("确认开启定时身份识别巡检（发往 BazaarLink）", text))
            {
                Status = "已取消，定时身份识别巡检保持关闭，未向 BazaarLink 发送任何内容。";
                return;
            }

            var consent = new IdentityInspectionConsent(scope, _savedInterval, _time.GetUtcNow());
            var saved = await _service!.SaveIdentityInspectionSettingsAsync(
                new IdentityInspectionSettings { Enabled = true, IntervalMinutes = _savedInterval }, consent, _lifetime.Token);
            ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            if (!saved.Settings.Enabled)
            {
                Status = "服务未开启定时身份识别巡检，未发送任何请求。";
                return;
            }

            Status = $"已开启：每轮结束后等待 {_savedInterval} 分钟，仅检测已授权范围，正在立即巡检一轮。";
            StartScheduler(immediate: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "开启失败，定时身份识别巡检保持关闭：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// 先把界面选择写入 Core，再读 Core 快照，用 Core 的数据计算范围，保证授权与 Core 校验逐项一致。
    /// 空选择返回 null 并提示，不发请求、不扩权。
    /// </summary>
    private async Task<IReadOnlyList<IdentityInspectionTarget>?> PrepareScopeAsync()
    {
        await FlushSelectionAsync();
        var snap = await FetchAsync(allowPrune: false);
        if (snap is null)
        {
            return null;
        }

        if (_selectionDirty || _persisting)
        {
            await FlushSelectionAsync();
            snap = await FetchAsync(allowPrune: false) ?? snap;
        }

        var scope = SelectedTargets(snap);
        if (scope.Count == 0)
        {
            Status = "尚未选择任何模型：不会发送任何请求，也没有授予任何权限。请勾选配置并展开选择要检测的模型。";
            return null;
        }

        return scope;
    }

    private string NameOf(Guid profileId)
        => _snapshot?.Targets.FirstOrDefault(s => s.Target.ProfileId == profileId)?.ProfileName
           ?? _store.Find(profileId)?.Name ?? "（配置）";

    /// <summary>
    /// 精确范围与外发说明：每个配置的名称、协议、完整 Base URL、Key 版本、模型，官方 POST/GET 地址，
    /// 完整 Key 外发、费用与报告公开风险。只在本地确认框显示，不显示 Key，也不进入托盘通知。
    /// </summary>
    private string BuildScopeText(IReadOnlyList<IdentityInspectionTarget> scope, int minutes)
    {
        var listing = string.Join("\n", scope
            .GroupBy(t => (t.ProfileId, t.KeyRevision, t.BaseUrl, t.Protocol))
            .Select((g, i) =>
                $"{i + 1}. {NameOf(g.Key.ProfileId)}（{ProtocolInfo.Display(g.Key.Protocol)}）\n" +
                $"   Base URL：{g.Key.BaseUrl}\n" +
                $"   Key 版本：{g.Key.KeyRevision}（不显示 Key）\n" +
                string.Join("\n", g.Select(t => $"     - 模型 {t.ModelId}"))));
        return
            $"将对你勾选的 {scope.Count} 个模型逐个向 BazaarLink 提交官方身份识别（Probe）：\n{listing}\n\n" +
            $"• 接收地址：POST {IdentityInspectionText.OfficialProbePostUrl}，之后用 GET {IdentityInspectionText.OfficialProbePostUrl}/{{runId}} 只读查询进度（不重新提交）。\n" +
            "• 每个目标的请求会携带该配置的完整 API Key、上面的 Base URL、协议和模型 ID，BazaarLink 将收到它们。\n" +
            "• 这是真实的第三方检测，可能产生费用，本程序不估算金额；BazaarLink 生成的报告可能公开，服务方的保留策略尚未确认。建议使用低额度、可撤销的测试专用 Key。\n" +
            "• 每轮冻结上述目标，逐个提交，当前目标结束后才处理下一个；每个目标最多等待 10 分钟（只是本地等待上限，不是远端任务或费用上限）。" +
            "等待超时、结果未知、查询暂停或被限流（HTTP 429）会停止本轮剩余目标，不补交、不自动重试；已发送的请求无法撤回。\n" +
            $"• 每轮结束后等待 {minutes} 分钟再开始下一轮，不重叠、不补跑错过的次数。\n" +
            "• 身份判断只是服务基于样本的概率性结论，不是模型真实身份的确定性证明。";
    }

    /// <summary>
    /// 保存间隔。授权与间隔绑定：已开启时先弹确认并携带精确范围，旧授权不沿用；取消则不保存。
    /// 不打断在途轮，等待计时从保存时重新开始。
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

            var scope = SelectedTargets(snap);
            // 已开启时保存间隔必须携带与当前勾选精确一致的确认；空范围无法确认，提示后不保存。
            if (Enabled && scope.Count == 0)
            {
                Status = "定时身份识别巡检已开启，但尚未选择任何模型，无法确认新间隔，未保存。请先选择模型，或先关闭定时。";
                return;
            }

            IdentityInspectionConsent? consent = null;
            if (Enabled)
            {
                var text =
                    $"间隔将由 {_savedInterval} 分钟改为 {minutes} 分钟。授权与间隔绑定，修改后需要对当前所选范围重新确认：\n\n" +
                    BuildScopeText(scope, minutes) + "\n\n" +
                    "• 不会打断正在进行的一轮；保存后等待计时从现在重新开始。\n" +
                    "• 选“否”则不保存，间隔和授权保持不变。";
                if (!_dialogs.Confirm("确认修改身份识别巡检间隔（发往 BazaarLink）", text))
                {
                    Status = $"已取消，间隔仍为 {_savedInterval} 分钟，授权未变。";
                    return;
                }

                consent = new IdentityInspectionConsent(scope, minutes, _time.GetUtcNow());
            }

            var saved = await _service!.SaveIdentityInspectionIntervalAsync(minutes, consent, _lifetime.Token);
            ApplySnapshot(saved, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune: false);
            Status = Enabled
                ? $"已保存间隔 {minutes} 分钟；当前这一轮不受影响，下一次等待从现在起重新计时。"
                : $"已保存间隔 {minutes} 分钟。开启定时身份识别巡检时会再次列出精确范围请你确认。";
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

            var pending = PendingSelectedCount(_snapshot!);
            if (pending == 0)
            {
                Status = "所选范围已全部确认，无需再次确认。";
                return;
            }

            var effect = Enabled
                ? "• 保持当前巡检开关和计时，不额外执行一轮，也不打断当前轮；后续提交仍按现有计划逐次复核授权。"
                : "• 仅保存范围授权，巡检保持关闭，本次不会立即执行；之后开启巡检仍须另行确认。";
            var text =
                $"当前有 {pending} 个已选模型尚未确认，将按已保存的 {_savedInterval} 分钟间隔确认以下精确范围：\n\n" +
                BuildScopeText(scope, _savedInterval) + "\n\n" + effect;
            if (!_dialogs.Confirm("确认身份识别巡检范围（发往 BazaarLink）", text))
            {
                Status = "已取消，新增的模型仍未授权，不会被检测。";
                return;
            }

            var consent = new IdentityInspectionConsent(scope, _savedInterval, _time.GetUtcNow());
            var saved = await _service!.SaveIdentityInspectionIntervalAsync(_savedInterval, consent, _lifetime.Token);
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

    /// <summary>
    /// “立即巡检一轮”：每次都重新列出精确范围并要求确认（旧授权不可复用），只检测所选范围，不改变定时开关。
    /// 确认后先把这次范围写入 Core，再开始一轮；之后与定时轮共用 Core 的最终提交闸门。
    /// </summary>
    private async Task RunOnceAsync()
    {
        if (_disposed || _busy)
        {
            return;
        }

        var proceed = false;
        IdentityInspectionConsent? consent = null;
        await _opLock.WaitAsync(_lifetime.Token);
        try
        {
            var scope = await PrepareScopeAsync();
            if (scope is null)
            {
                return;
            }

            var text = BuildScopeText(scope, _savedInterval) +
                "\n\n此操作只立即运行一轮，不会开启定时；每次手动运行都需要重新确认，之前的确认不会复用。";
            if (!_dialogs.Confirm("确认立即巡检一轮（发往 BazaarLink）", text))
            {
                Status = "已取消，未向 BazaarLink 发送任何内容。";
                return;
            }

            // 手动确认只对这一轮有效：直接交给 Core 的 Begin，不写入持久授权，也不沿用旧的定时授权。
            consent = new IdentityInspectionConsent(scope, _savedInterval, _time.GetUtcNow());
            proceed = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "无法准备本轮范围，未发送任何请求：" + UserMessages.Describe(ex);
        }
        finally
        {
            _opLock.Release();
        }

        if (!proceed)
        {
            return;
        }

        await RunRoundAsync(IdentityInspectionTrigger.Manual, consent, _lifetime.Token);
    }
}
