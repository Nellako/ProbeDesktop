using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    private bool _syncingNodes;

    /// <summary>启动时只执行一次：读取已保存设置。已开启且所选范围内有已授权模型时立即巡检一轮并进入定时循环，否则不发请求。</summary>
    public async Task LoadAsync()
    {
        if (!IsAvailable)
        {
            Status = "当前服务不支持全局巡检。";
            return;
        }

        if (_startupDone || _disposed)
        {
            return;
        }

        _startupDone = true;
        var snap = await FetchAsync(allowPrune: true);
        if (snap is null)
        {
            _startupDone = false;
            return;
        }

        _loaded = true;
        if (!snap.Settings.Enabled)
        {
            Status = "全局巡检未开启。请先勾选要巡检的配置和模型，再点“开启全局巡检”；开启时会列出范围并请你确认。";
            return;
        }

        if (InspectionScope.AuthorizedSelectedCount(snap) == 0)
        {
            Status = "全局巡检已保存为开启，但所选范围内没有已授权的模型（或尚未选择），未发送任何请求。请选择模型并重新确认。";
            return;
        }

        Status = "已按已授权范围恢复全局巡检，正在立即巡检一轮。";
        StartScheduler(immediate: true);
    }

    private async Task<InspectionSnapshot?> FetchAsync(bool allowPrune)
    {
        try
        {
            var snap = await _service!.GetInspectionSnapshotAsync(_lifetime.Token);
            if (_disposed)
            {
                return null;
            }

            ApplySnapshot(snap, reloadSelection: !_selectionDirty && !_selectionSaving, allowPrune);
            return snap;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Status = "读取巡检范围失败：" + UserMessages.Describe(ex);
            return null;
        }
    }

    private void ApplySnapshot(InspectionSnapshot snap, bool reloadSelection, bool allowPrune)
    {
        var first = _snapshot is null;
        _snapshot = snap;
        Enabled = snap.Settings.Enabled;

        // 间隔：只在已保存值真的变化时通知调度器（它会丢弃当前等待并重新计时，不打断在途轮）。
        var oldSaved = _savedInterval;
        var minutes = snap.Settings.IntervalMinutes;
        _savedInterval = minutes;
        if (minutes != oldSaved)
        {
            _scheduler.TrySetInterval(minutes);
        }

        // 输入框：用户没改过（仍等于旧的已保存值）或首次加载时跟随；用户正在编辑的内容不被覆盖。
        if (first || _intervalText.Trim() == oldSaved.ToString(CultureInfo.InvariantCulture))
        {
            IntervalText = minutes.ToString(CultureInfo.InvariantCulture);
        }

        UpdateIntervalState();

        // 选择：未在编辑/保存时以 Core 为准载入；任何时候都与当前缓存求交集（移除已不存在的模型，新增模型不选）。
        if (reloadSelection)
        {
            _selection.Load(snap.Selections.Select(s => new SelectionEntry(s.ProfileId, s.IsSelected, s.SelectedModelIds)));
        }

        _selection.Reconcile(snap.Targets.Select(t => (t.Target.ProfileId, (IReadOnlyCollection<string>)t.Target.Models)));
        if (allowPrune && reloadSelection
            && !_selection.IsSameAs(snap.Selections.Select(s => new SelectionEntry(s.ProfileId, s.IsSelected, s.SelectedModelIds))))
        {
            // 修剪后与 Core 保存的不一致（例如缓存里已消失的模型）：整体回写，保证旧选择不会在模型重新出现时被复活。
            _selectionDirty = true;
            SchedulePersist();
        }

        if (!_busy)
        {
            _round = snap.LatestRound;
        }

        SyncNodes(snap);
        RecalcSummary();
        OnRoundChanged();
    }

    private void SyncNodes(InspectionSnapshot snap)
    {
        _syncingNodes = true;
        try
        {
            var ordered = snap.Targets.OrderBy(t => t.ProfileName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var ids = ordered.Select(t => t.Target.ProfileId).ToHashSet();
            foreach (var gone in _nodeIndex.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                ProfileNodes.Remove(_nodeIndex[gone]);
                _nodeIndex.Remove(gone);
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var status = ordered[i];
                var target = status.Target;
                if (!_nodeIndex.TryGetValue(target.ProfileId, out var node))
                {
                    node = new InspectionProfileNode(target.ProfileId, this);
                    _nodeIndex[target.ProfileId] = node;
                    ProfileNodes.Insert(Math.Min(i, ProfileNodes.Count), node);
                }
                else
                {
                    var current = ProfileNodes.IndexOf(node);
                    if (current != i)
                    {
                        ProfileNodes.Move(current, i);
                    }
                }

                node.Name = status.ProfileName;
                node.ProtocolText = ProtocolInfo.Display(target.Protocol);
                node.SetModels(target.Models, status.HasModelCache);
                node.SetIncludedSilently(_selection.IsIncluded(target.ProfileId));
                foreach (var model in node.Models)
                {
                    model.SetSelectedSilently(_selection.IsModelSelected(target.ProfileId, model.Id));
                }

                UpdateNodeStatus(node, status);
            }
        }
        finally
        {
            _syncingNodes = false;
        }

        OnPropertyChanged(nameof(HasProfiles));
    }

    private void RefreshNodeStatuses()
    {
        if (_snapshot is not { } snap)
        {
            return;
        }

        foreach (var status in snap.Targets)
        {
            if (_nodeIndex.TryGetValue(status.Target.ProfileId, out var node))
            {
                UpdateNodeStatus(node, status);
            }
        }
    }

    private void UpdateNodeStatus(InspectionProfileNode node, InspectionTargetStatus status)
    {
        var id = status.Target.ProfileId;
        var authorized = new HashSet<string>(status.AuthorizedModels, StringComparer.Ordinal);
        var paused = status.Authorization == InspectionAuthorizationState.Paused;
        var selected = 0;
        var pending = 0;
        foreach (var model in node.Models)
        {
            if (_selection.IsModelSelected(id, model.Id))
            {
                selected++;
                if (authorized.Contains(model.Id))
                {
                    model.SetAuth("已授权", "ok");
                }
                else
                {
                    pending++;
                    model.SetAuth(paused ? "已暂停" : "待确认", "warn");
                }
            }
            else
            {
                model.SetAuth("", "none");
            }
        }

        string text;
        string tone;
        if (!status.HasModelCache)
        {
            (text, tone) = ("未拉取模型，不在范围内", "none");
        }
        else if (status.Target.Models.Count == 0)
        {
            (text, tone) = ("缓存为空，不在范围内", "none");
        }
        else if (selected == 0)
        {
            (text, tone) = node.IsIncluded ? ("已勾选但未选模型：不会巡检", "warn") : ("未选择", "none");
        }
        else if (!node.IsIncluded)
        {
            (text, tone) = ($"配置未勾选，不巡检（已保留 {selected} 个模型选择）", "none");
        }
        else if (paused)
        {
            (text, tone) = ("鉴权失败已暂停，需重新确认", "warn");
        }
        else if (pending > 0)
        {
            (text, tone) = ($"待确认 {pending} 个模型", "warn");
        }
        else
        {
            (text, tone) = ("已授权", "ok");
        }

        if (status.RetryNotBefore is { } retry)
        {
            text += $"；限流等待至 {retry.ToLocalTime():HH:mm}";
            tone = "warn";
        }

        node.SetSummary(selected, text, tone);
    }

    /// <summary>按当前本地选择与快照里的授权情况重算概览（选择变化后立即调用，不等待保存完成）。</summary>
    private void RecalcSummary()
    {
        var scope = _selection.EffectiveScope();
        var models = scope.Sum(s => s.ModelIds.Count);
        var authorized = 0;
        if (_snapshot is { } snap)
        {
            foreach (var (profileId, ids) in scope)
            {
                var status = snap.Targets.FirstOrDefault(t => t.Target.ProfileId == profileId);
                if (status is null)
                {
                    continue;
                }

                var granted = new HashSet<string>(status.AuthorizedModels, StringComparer.Ordinal);
                authorized += ids.Count(granted.Contains);
            }
        }

        _pendingCount = models - authorized;
        ScopeSummary = models == 0
            ? "尚未选择任何模型：不会发送任何巡检请求，开启和立即巡检也不会生效。请勾选配置并展开选择模型。"
            : $"已选 {scope.Count} 个配置、{models} 个模型；已授权 {authorized} 个，待确认 {_pendingCount} 个。";
        PendingText = _pendingCount > 0 ? "待确认的模型在你确认前不会被巡检，已授权部分不受影响。" : "";
        OnPropertyChanged(nameof(CanConfirmScope));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- 配置/模型缓存变化 ----

    private void OnStoreProfileUpdated(object? sender, Guid id) => RequestRefetch();

    private void OnStoreItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestRefetch();

    /// <summary>配置被保存、删除，或模型缓存被更新后，重新读取快照并对齐选择。多次请求合并为一个循环。</summary>
    private void RequestRefetch()
    {
        if (!_loaded || _disposed || !IsAvailable)
        {
            return;
        }

        _refetchAgain = true;
        if (_refetching)
        {
            return;
        }

        _refetching = true;
        _ = RefetchLoopAsync();
    }

    private async Task RefetchLoopAsync()
    {
        try
        {
            while (_refetchAgain && !_disposed)
            {
                _refetchAgain = false;
                await FetchAsync(allowPrune: true);
            }
        }
        finally
        {
            _refetching = false;
        }
    }
}
