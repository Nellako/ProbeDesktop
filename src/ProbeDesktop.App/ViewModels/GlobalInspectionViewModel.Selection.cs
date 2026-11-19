namespace ProbeDesktop.App.ViewModels;

public sealed partial class GlobalInspectionViewModel
{
    public void NodeIncludedChanged(InspectionProfileNode node)
    {
        if (_syncingNodes) return;
        _selection.SetIncluded(node.ProfileId, node.IsIncluded);
        AfterLocalSelectionChange();
    }

    public void NodeModelToggled(InspectionProfileNode node, InspectionModelNode model)
    {
        if (_syncingNodes) return;
        _selection.SetModelSelected(node.ProfileId, model.Id, model.IsSelected);
        AfterLocalSelectionChange();
    }

    public void NodeSelectAll(InspectionProfileNode node) { _selection.SelectAll(node.ProfileId); SyncNodeModels(node); }
    public void NodeClearModels(InspectionProfileNode node) { _selection.ClearModels(node.ProfileId); SyncNodeModels(node); }

    private void SyncNodeModels(InspectionProfileNode node)
    {
        foreach (var model in node.Models) model.SetSelectedSilently(_selection.IsModelSelected(node.ProfileId, model.Id));
        AfterLocalSelectionChange();
    }

    private void AfterLocalSelectionChange()
    {
        _draftRevision++;
        RecalcSummary();
        RefreshNodeStatuses();
    }
}
