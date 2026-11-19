using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class IdentityInspectionViewModel
{
    private bool _syncingNodes;

    private sealed record ProfileView(Guid Id, string Name, ApiProtocol Protocol, bool HasCache, IReadOnlyList<string> Models);

    /// <summary>树的数据来源：配置存储里的缓存模型（与用户在配置页看到的一致）。</summary>
    private IReadOnlyList<ProfileView> CurrentProfiles()
        => _store.Items
            .Select(p => new ProfileView(p.Id, p.Name, p.Protocol, p.HasModelCache,
                p.Profile.Models.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToArray()))
            .ToList();

    /// <summary>
    /// 启动时读取已确认设置，再按上轮完成时间恢复排期；不重新提交未确定的意图。
    /// 上次中断的旧轮由 Core 首次加载时以 ApplicationInterrupted 收尾，App 不再额外调用 End。
    /// 既有远端任务的只读查询由 MainViewModel 通过 ProbeRunCoordinator 恢复（GET）。
    /// </summary>
    public async Task LoadAsync()
    {
        if (!IsAvailable)
        {
            Status = "当前服务不支持身份识别巡检。";
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
        Status = "";
        RefreshSchedule();
    }

    private async Task<IdentityInspectionSnapshot?> FetchAsync(bool allowPrune)
    {
        try
        {
            int epoch = _snapshotEpoch;
            var snap = await _service!.GetIdentityInspectionSnapshotAsync(_lifetime.Token);
            if (_disposed)
            {
                return null;
            }

            if (epoch == _snapshotEpoch) ApplySnapshot(snap, reloadSelection: !_selectionDirty, allowPrune);
            return _snapshot;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Status = "读取身份识别巡检范围失败：" + UserMessages.Describe(ex);
            return null;
        }
    }

    private void ApplySnapshot(IdentityInspectionSnapshot snap, bool reloadSelection, bool allowPrune)
    {
        var first = _snapshot is null;
        _snapshot = snap;
        Enabled = snap.Settings.Enabled;

        var oldSaved = _savedInterval;
        var minutes = snap.Settings.IntervalMinutes;
        _savedInterval = minutes;

        // 用户没改过输入框（仍等于旧的已保存值）或首次加载时跟随；正在编辑的内容不被覆盖。
        if (first || _intervalText.Trim() == oldSaved.ToString(CultureInfo.InvariantCulture))
        {
            IntervalText = minutes.ToString(CultureInfo.InvariantCulture);
        }

        UpdateIntervalState();

        var saved = snap.Selections.Select(s => new SelectionEntry(s.ProfileId, s.IsSelected, s.SelectedModelIds)).ToList();
        if (reloadSelection)
        {
            _selection.Load(saved);
        }

        var profiles = CurrentProfiles();
        _selection.Reconcile(profiles.Select(p => (p.Id, (IReadOnlyCollection<string>)p.Models)));

        if (!_busy)
        {
            _round = snap.LatestRound;
        }

        SyncNodes(profiles, snap);
        RecalcSummary();
        SyncRows();
        OnRoundChanged();
        RefreshSchedule();
    }

    private void SyncNodes(IReadOnlyList<ProfileView> profiles, IdentityInspectionSnapshot snap)
    {
        _syncingNodes = true;
        try
        {
            var ordered = profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var ids = ordered.Select(p => p.Id).ToHashSet();
            foreach (var gone in _nodeIndex.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                ProfileNodes.Remove(_nodeIndex[gone]);
                _nodeIndex.Remove(gone);
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                if (!_nodeIndex.TryGetValue(p.Id, out var node))
                {
                    node = new InspectionProfileNode(p.Id, this);
                    _nodeIndex[p.Id] = node;
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

                node.Name = p.Name;
                node.ProtocolText = ProtocolInfo.Display(p.Protocol);
                node.SetModels(p.Models, p.HasCache);
                node.SetIncludedSilently(_selection.IsIncluded(p.Id));
                foreach (var model in node.Models)
                {
                    model.SetSelectedSilently(_selection.IsModelSelected(p.Id, model.Id));
                }

                UpdateNodeStatus(node, snap);
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

        foreach (var node in _nodeIndex.Values)
        {
            UpdateNodeStatus(node, snap);
        }
    }

    private void UpdateNodeStatus(InspectionProfileNode node, IdentityInspectionSnapshot snap)
    {
        var id = node.ProfileId;
        var targets = snap.Targets.Where(t => t.Target.ProfileId == id).ToList();
        var paused = targets.Any(t => t.Paused);
        var selected = 0;
        var pending = 0;
        foreach (var model in node.Models)
        {
            if (!_selection.IsModelSelected(id, model.Id))
            {
                model.SetAuth("", "none");
                continue;
            }

            selected++;
            var status = targets.FirstOrDefault(t => string.Equals(t.Target.ModelId, model.Id, StringComparison.Ordinal));
            if (status is { Authorized: true, Paused: false })
            {
                model.SetAuth("已授权", "ok");
            }
            else
            {
                pending++;
                model.SetAuth(status?.Paused == true ? "已暂停" : "待确认", "warn");
            }
        }

        string text;
        string tone;
        if (!node.HasCache)
        {
            (text, tone) = ("未拉取模型，不在范围内", "none");
        }
        else if (node.CachedCount == 0)
        {
            (text, tone) = ("缓存为空，不在范围内", "none");
        }
        else if (selected == 0)
        {
            (text, tone) = node.IsIncluded ? ("已勾选但未选模型：不会检测", "warn") : ("未选择", "none");
        }
        else if (!node.IsIncluded)
        {
            (text, tone) = ($"配置未勾选，不检测（已保留 {selected} 个模型选择）", "none");
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

        node.SetSummary(selected, text, tone);
    }

    /// <summary>按本地选择与快照里的授权情况重算概览（选择变化后立即调用，不等待保存完成）。</summary>
    private void RecalcSummary()
    {
        _selectionDirty = _snapshot is { } saved && !_selection.IsSameAs(saved.Selections.Select(x => new SelectionEntry(x.ProfileId, x.IsSelected, x.SelectedModelIds)));
        var scope = _selection.EffectiveScope();
        var models = scope.Sum(s => s.ModelIds.Count);
        var authorized = 0;
        if (_snapshot is { } snap)
        {
            foreach (var (profileId, ids) in scope)
            {
                foreach (var modelId in ids)
                {
                    var status = snap.Targets.FirstOrDefault(t => t.Target.ProfileId == profileId
                        && string.Equals(t.Target.ModelId, modelId, StringComparison.Ordinal));
                    if (status is { Authorized: true, Paused: false })
                    {
                        authorized++;
                    }
                }
            }
        }

        _pendingCount = models - authorized;
        ScopeSummary = models == 0
            ? (_selectionDirty ? "草稿范围为空。确认后不再安排下一轮。" : "还没有可巡检的模型。可在下方选择配置和模型。")
            : $"已选 {scope.Count} 个配置、{models} 个模型；已授权 {authorized} 个，待确认 {_pendingCount} 个。";
        UpdatePendingText();
        OnPropertyChanged(nameof(IntervalHint));
        OnPropertyChanged(nameof(CanConfirmScope));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdatePendingText() => PendingText = _snapshot?.NeedsUpgradeConfirmation == true
        ? "旧授权已失效。请重新确认巡检设置，无需重新开关。"
        : HasDraft ? "有未确认的更改，自动巡检仍使用已确认设置。" : _pendingCount > 0 ? "待确认目标不会参加巡检。" : "";

    // ---- 配置/模型缓存变化 ----

    private bool _refetching;
    private bool _refetchAgain;

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

    // ---- 当前范围计算（与 Core 的 Selected 标记一致）----

    private static IReadOnlyList<IdentityInspectionTarget> SelectedTargets(IdentityInspectionSnapshot snap)
        => snap.Targets
            .Where(t => t.Selected)
            .OrderBy(t => t.Target.ProfileId)
            .ThenBy(t => t.Target.ModelId, StringComparer.Ordinal)
            .Select(t => t.Target)
            .ToList();

    private static int AuthorizedSelectedCount(IdentityInspectionSnapshot snap)
        => snap.Targets.Count(t => t.Selected && t.Authorized && !t.Paused);

    private static int PendingSelectedCount(IdentityInspectionSnapshot snap)
        => snap.Targets.Count(t => t.Selected && !(t.Authorized && !t.Paused));
}
