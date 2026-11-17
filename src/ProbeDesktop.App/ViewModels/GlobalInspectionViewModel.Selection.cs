using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    // ---- 树节点事件（IInspectionNodeHost）----
    // 选择本身只是偏好，不是授权：这里只更新本地状态并保存，不联网、不弹确认、不申请授权。

    public void NodeIncludedChanged(InspectionProfileNode node)
    {
        if (_syncingNodes)
        {
            return;
        }

        _selection.SetIncluded(node.ProfileId, node.IsIncluded);
        AfterLocalSelectionChange();
    }

    public void NodeModelToggled(InspectionProfileNode node, InspectionModelNode model)
    {
        if (_syncingNodes)
        {
            return;
        }

        _selection.SetModelSelected(node.ProfileId, model.Id, model.IsSelected);
        AfterLocalSelectionChange();
    }

    /// <summary>用户明确点击“全选”才会选中该配置当前缓存的全部模型；勾选配置本身不会隐式全选。</summary>
    public void NodeSelectAll(InspectionProfileNode node)
    {
        _selection.SelectAll(node.ProfileId);
        SyncNodeModelsFromState(node);
        AfterLocalSelectionChange();
    }

    public void NodeClearModels(InspectionProfileNode node)
    {
        _selection.ClearModels(node.ProfileId);
        SyncNodeModelsFromState(node);
        AfterLocalSelectionChange();
    }

    private void SyncNodeModelsFromState(InspectionProfileNode node)
    {
        _syncingNodes = true;
        try
        {
            foreach (var model in node.Models)
            {
                model.SetSelectedSilently(_selection.IsModelSelected(node.ProfileId, model.Id));
            }
        }
        finally
        {
            _syncingNodes = false;
        }
    }

    private void AfterLocalSelectionChange()
    {
        _selectionDirty = true;
        RecalcSummary();
        RefreshNodeStatuses();
        SchedulePersist();
    }

    // ---- 持久化：合并连续修改，同一时间只有一个保存在进行 ----

    private void SchedulePersist()
    {
        if (_persisting || _disposed || _selectionService is null)
        {
            return;
        }

        _persisting = true;
        _persistTask = PersistLoopAsync();
    }

    private async Task PersistLoopAsync()
    {
        try
        {
            while (_selectionDirty && !_disposed)
            {
                _selectionDirty = false;
                _selectionSaving = true;
                var entries = _selection.Snapshot()
                    .Select(e => new InspectionSelection(e.ProfileId, e.Included, e.ModelIds))
                    .ToList();
                try
                {
                    var snap = await _selectionService!.SaveInspectionSelectionsAsync(entries, _lifetime.Token);
                    if (_disposed)
                    {
                        return;
                    }

                    // 保存期间用户又改了：保留本地编辑，不用 Core 结果覆盖；否则以 Core 返回为准。
                    ApplySnapshot(snap, reloadSelection: !_selectionDirty, allowPrune: false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 保存失败（例如缓存刚被更新导致所选模型已不存在）：放弃本地未保存的修改，重新以 Core 为准，且不自动重试。
                    _selectionDirty = false;
                    _selectionSaving = false;
                    Status = "保存巡检选择失败，已恢复为已保存的选择：" + UserMessages.Describe(ex);
                    await FetchAsync(allowPrune: false);
                    return;
                }
            }
        }
        finally
        {
            _selectionSaving = false;
            _persisting = false;
        }
    }

    /// <summary>等待尚未写入 Core 的选择保存完成。开启、立即巡检、保存间隔等需要“Core 里的选择=界面所见”的操作先调用它。</summary>
    private async Task FlushSelectionAsync()
    {
        while ((_persisting || _selectionDirty) && !_disposed && _selectionService is not null)
        {
            if (!_persisting)
            {
                SchedulePersist();
            }

            var task = _persistTask;
            if (task is null)
            {
                break;
            }

            await task;
        }
    }
}
