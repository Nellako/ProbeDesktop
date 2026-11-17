using System.Collections.ObjectModel;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;

namespace ProbeDesktop.App.ViewModels;

/// <summary>树节点把用户操作交给巡检 VM 处理（选择状态与持久化都在 VM 中）。</summary>
public interface IInspectionNodeHost
{
    void NodeIncludedChanged(InspectionProfileNode node);
    void NodeModelToggled(InspectionProfileNode node, InspectionModelNode model);
    void NodeSelectAll(InspectionProfileNode node);
    void NodeClearModels(InspectionProfileNode node);
}

/// <summary>某个配置下的一个缓存模型。以模型 ID 字符串为稳定标识。</summary>
public sealed class InspectionModelNode : ObservableObject
{
    private readonly InspectionProfileNode _owner;
    private bool _isSelected;
    private string _authText = "";
    private string _authTone = "none";

    public InspectionModelNode(InspectionProfileNode owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    public string Id { get; }

    /// <summary>用户勾选/取消。程序同步状态时用 SetSelectedSilently，不会触发保存。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _owner.ModelToggled(this);
            }
        }
    }

    public string AuthText
    {
        get => _authText;
        private set => SetProperty(ref _authText, value);
    }

    /// <summary>none / ok / warn。</summary>
    public string AuthTone
    {
        get => _authTone;
        private set => SetProperty(ref _authTone, value);
    }

    public void SetSelectedSilently(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

    public void SetAuth(string text, string tone)
    {
        AuthText = text;
        AuthTone = tone;
    }
}

/// <summary>巡检树的一个配置节点：配置勾选 + 可展开的子模型勾选。</summary>
public sealed class InspectionProfileNode : ObservableObject
{
    private readonly IInspectionNodeHost _host;
    private bool _isIncluded;
    private bool _isExpanded;
    private string _name = "";
    private string _protocolText = "";
    private string _summaryText = "";
    private string _statusText = "";
    private string _statusTone = "none";
    private bool _hasCache;
    private int _cachedCount;
    private int _selectedCount;
    private ObservableCollection<InspectionModelNode> _models = [];

    public InspectionProfileNode(Guid profileId, IInspectionNodeHost host)
    {
        ProfileId = profileId;
        _host = host;
        SelectAllCommand = new RelayCommand(() => _host.NodeSelectAll(this), () => HasCache && CachedCount > 0);
        ClearModelsCommand = new RelayCommand(() => _host.NodeClearModels(this), () => HasCache && SelectedCount > 0);
    }

    public Guid ProfileId { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearModelsCommand { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string ProtocolText
    {
        get => _protocolText;
        set => SetProperty(ref _protocolText, value);
    }

    /// <summary>配置勾选（是否纳入巡检）。取消勾选不清除子模型选择。</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value))
            {
                _host.NodeIncludedChanged(this);
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool HasCache
    {
        get => _hasCache;
        private set
        {
            if (SetProperty(ref _hasCache, value))
            {
                OnPropertyChanged(nameof(CanSelect));
            }
        }
    }

    public int CachedCount
    {
        get => _cachedCount;
        private set
        {
            if (SetProperty(ref _cachedCount, value))
            {
                OnPropertyChanged(nameof(CanSelect));
            }
        }
    }

    /// <summary>有已拉取且非空的模型缓存才可勾选；否则提示先到“模型与检测”或“配置管理”拉取。</summary>
    public bool CanSelect => HasCache && CachedCount > 0;

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    /// <summary>例如“已选 3 / 缓存 12 个模型”。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>none / ok / warn。</summary>
    public string StatusTone
    {
        get => _statusTone;
        private set => SetProperty(ref _statusTone, value);
    }

    public ObservableCollection<InspectionModelNode> Models
    {
        get => _models;
        private set => SetProperty(ref _models, value);
    }

    public void ModelToggled(InspectionModelNode model) => _host.NodeModelToggled(this, model);

    public void SetIncludedSilently(bool value) => SetProperty(ref _isIncluded, value, nameof(IsIncluded));

    /// <summary>用缓存模型 ID 对齐子节点。ID 序列不变时只更新状态，不重建集合，因此展开状态、滚动位置与键盘焦点保持不变。</summary>
    public void SetModels(IReadOnlyList<string> cachedIds, bool hasCache)
    {
        HasCache = hasCache;
        CachedCount = cachedIds.Count;
        if (_models.Count == cachedIds.Count && _models.Select(m => m.Id).SequenceEqual(cachedIds, StringComparer.Ordinal))
        {
            return;
        }

        var keep = _models.Where(m => m.IsSelected).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var next = new ObservableCollection<InspectionModelNode>();
        foreach (var id in cachedIds)
        {
            var node = new InspectionModelNode(this, id);
            node.SetSelectedSilently(keep.Contains(id));
            next.Add(node);
        }

        Models = next;
    }

    public void SetSummary(int selected, string status, string tone)
    {
        SelectedCount = selected;
        SummaryText = HasCache
            ? CachedCount == 0 ? "缓存为空" : $"已选 {selected} / 缓存 {CachedCount} 个模型"
            : "未拉取模型";
        StatusText = status;
        StatusTone = tone;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
