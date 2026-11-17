using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>配置管理页。API Key 不经绑定：由视图从 PasswordBox 读取后直接传给 SaveAsync，并立即清空控件。</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly IDesktopService _service;
    private readonly ProfileStore _store;
    private readonly IDialogService _dialogs;
    private readonly ModelRefreshCoordinator _refresh;
    private readonly CancellationToken _lifetime;
    private ProfileItem? _selected;
    private string _searchText = "";
    private bool _isNew;
    private string _editName = "";
    private string _editBaseUrl = "";
    private ApiProtocol _editProtocol = ProtocolInfo.DefaultForNew;
    private string _editDefaultModel = "";
    private string _validation = "";
    private string _status = "";
    private bool _isSaving;

    public ProfilesViewModel(IDesktopService service, ProfileStore store, IDialogService dialogs, ModelRefreshCoordinator refresh, CancellationToken lifetime)
    {
        _service = service;
        _store = store;
        _dialogs = dialogs;
        _refresh = refresh;
        _lifetime = lifetime;
        RefreshSelectedCommand = new AsyncCommand(
            () => Selected is { } s ? _refresh.RefreshOneAsync(s) : Task.CompletedTask,
            () => Selected is { IsBusy: false } && !IsNew && !IsSaving && !_refresh.IsRunning);
        RefreshAllCommand = new AsyncCommand(_refresh.RefreshAllAsync, () => _store.Items.Count > 0 && !IsSaving && !_refresh.IsRunning);
        _refresh.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelRefreshCoordinator.IsRunning))
            {
                OnPropertyChanged(nameof(IsRefreshRunning));
            }
            else if (e.PropertyName is nameof(ModelRefreshCoordinator.ProgressText))
            {
                OnPropertyChanged(nameof(RefreshProgress));
            }
            else if (e.PropertyName is nameof(ModelRefreshCoordinator.LastSummary))
            {
                OnPropertyChanged(nameof(RefreshSummary));
            }
        };
        // 独立视图：本页搜索不影响其他页面绑定同一集合的列表。
        View = new ListCollectionView(store.Items);
        View.Filter = Filter;
        NewCommand = new RelayCommand(BeginNew, () => !IsSaving);
        CancelCommand = new RelayCommand(CancelEdit, () => IsEditorVisible && !IsSaving);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => Selected is { IsBusy: false } && !IsNew && !IsSaving);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        _store.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Count));
        _store.ProfileUpdated += (_, id) =>
        {
            if (Selected?.Id == id)
            {
                OnPropertyChanged(nameof(SelectedDetail));
            }
        };
    }

    /// <summary>请求视图清空 PasswordBox（切换选择、取消、保存后）。</summary>
    public event EventHandler? ClearKeyRequested;

    public ICollectionView View { get; }
    public ICommand NewCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearSearchCommand { get; }

    /// <summary>仅拉取列表中选中的这一个配置（已保存的内容；需先保存未保存的修改）。</summary>
    public ICommand RefreshSelectedCommand { get; }

    /// <summary>对全部配置各拉取一次：统一确认、串行、不重试、失败隔离。不受搜索筛选影响。</summary>
    public ICommand RefreshAllCommand { get; }

    public bool IsRefreshRunning => _refresh.IsRunning;
    public string RefreshProgress => _refresh.ProgressText;
    public string RefreshSummary => _refresh.LastSummary;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                View.Refresh();
            }
        }
    }

    public ProfileItem? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (SetProperty(ref _selected, value))
            {
                if (old is not null)
                {
                    old.PropertyChanged -= OnSelectedItemChanged;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedItemChanged;
                    LoadEditor(value);
                }
                else if (!_isNew)
                {
                    ResetEditor();
                }

                OnPropertyChanged(nameof(SelectedDetail));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNew
    {
        get => _isNew;
        private set
        {
            if (SetProperty(ref _isNew, value))
            {
                OnPropertyChanged(nameof(IsEditorVisible));
                OnPropertyChanged(nameof(EditorTitle));
                OnPropertyChanged(nameof(KeyHint));
            }
        }
    }

    public bool IsEditorVisible => IsNew || Selected is not null;
    public string EditorTitle => IsNew ? "新建配置" : "编辑配置";

    public string KeyHint => IsNew
        ? "新建配置必须填写 API Key。Key 使用 Windows DPAPI 加密保存，不会显示或导出。"
        : "留空表示保留已保存的 Key；只有输入新值才会替换。已保存的 Key 不会回显。";

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    public string EditBaseUrl
    {
        get => _editBaseUrl;
        set
        {
            if (SetProperty(ref _editBaseUrl, value))
            {
                OnPropertyChanged(nameof(BaseUrlAdvice));
                OnPropertyChanged(nameof(ProtocolPathHint));
            }
        }
    }

    public string EditDefaultModel
    {
        get => _editDefaultModel;
        set => SetProperty(ref _editDefaultModel, value);
    }

    public IReadOnlyList<ProtocolOption> ProtocolOptions => ProtocolInfo.Options;

    /// <summary>新建配置默认 OpenAI Responses；已有配置按已保存值加载，不会被改写。</summary>
    public ApiProtocol EditProtocol
    {
        get => _editProtocol;
        set
        {
            if (SetProperty(ref _editProtocol, value))
            {
                OnPropertyChanged(nameof(ProtocolPathHint));
                OnPropertyChanged(nameof(BaseUrlAdvice));
            }
        }
    }

    /// <summary>按当前协议与 Base URL 显示最终请求路径（不自动补 /v1）。</summary>
    public string ProtocolPathHint
    {
        get
        {
            var inference = ProtocolInfo.TryInferenceUrl(EditBaseUrl.Trim(), EditProtocol);
            var models = ProtocolInfo.TryModelsUrl(EditBaseUrl.Trim(), EditProtocol);
            if (inference is null || models is null)
            {
                return $"协议：{ProtocolInfo.Display(EditProtocol)}。填写有效的 https Base URL 后，这里会显示最终请求路径。";
            }

            return $"最终请求路径（不自动补 /v1）：\n模型列表 GET {models}\n连接测试 POST {inference}\n认证：{ProtocolInfo.AuthHeaderText(EditProtocol)}";
        }
    }

    public string? BaseUrlAdvice
        => Endpoints.TryValidateBaseUrl(EditBaseUrl, out var uri, out _) ? Endpoints.BaseUrlAdvice(uri!, EditProtocol) : null;

    public string Validation
    {
        get => _validation;
        private set => SetProperty(ref _validation, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanSave => IsEditorVisible && !IsSaving && !(Selected?.IsBusy ?? false);

    public string SelectedDetail => Selected is { } s && !IsNew
        ? $"协议：{s.ProtocolDisplay}　Key 版本：{s.KeyRevision}　模型缓存：{s.ModelSummary}　默认模型：{s.DefaultModelText}　更新于：{s.UpdatedAtText}"
        : "";

    public int Count => _store.Items.Count;

    private void OnSelectedItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SelectedDetail));
        // 拉取期间 IsBusy 会变化：让删除/拉取命令及时刷新可用状态。
        CommandManager.InvalidateRequerySuggested();
    }

    private bool Filter(object obj)
    {
        if (obj is not ProfileItem item)
        {
            return false;
        }

        var q = SearchText.Trim();
        return q.Length == 0
            || item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || item.BaseUrl.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.ProtocolDisplay.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || item.DefaultModelId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void BeginNew()
    {
        IsNew = true;
        Selected = null;
        EditName = "";
        EditBaseUrl = "https://";
        EditProtocol = ProtocolInfo.DefaultForNew;
        EditDefaultModel = "";
        Validation = "";
        Status = "";
        ClearKeyRequested?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(CanSave));
    }

    private void LoadEditor(ProfileItem item)
    {
        IsNew = false;
        EditName = item.Name;
        EditBaseUrl = item.BaseUrl;
        EditProtocol = item.Protocol;
        EditDefaultModel = item.DefaultModelId;
        Validation = "";
        ClearKeyRequested?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(CanSave));
    }

    private void ResetEditor()
    {
        IsNew = false;
        EditName = "";
        EditBaseUrl = "";
        EditProtocol = ProtocolInfo.DefaultForNew;
        EditDefaultModel = "";
        Validation = "";
        ClearKeyRequested?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(CanSave));
    }

    private void CancelEdit()
    {
        if (IsNew)
        {
            IsNew = false;
            ResetEditor();
        }
        else if (Selected is { } s)
        {
            LoadEditor(s);
        }

        Status = "已放弃未保存的修改。";
    }

    /// <summary>读取密钥前的校验，失败时不读取、不清空 PasswordBox，用户无需重输。</summary>
    public bool ValidateEditor(bool keyProvided)
    {
        Validation = "";
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            Validation = "请填写配置名称。";
            return false;
        }

        if (name.Length > 100 || name.Any(char.IsControl))
        {
            Validation = "配置名称最长 100 字符，且不能包含控制字符。";
            return false;
        }

        var editingId = IsNew ? (Guid?)null : Selected?.Id;
        if (_store.Items.Any(p => p.Id != editingId && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Validation = "已存在同名配置，请换一个名称以便区分。";
            return false;
        }

        if (!Endpoints.TryValidateBaseUrl(EditBaseUrl, out _, out var urlError))
        {
            Validation = urlError;
            return false;
        }

        var model = EditDefaultModel.Trim();
        if (model.Length > 256 || model.Any(char.IsControl))
        {
            Validation = "默认模型最长 256 字符，且不能包含控制字符。";
            return false;
        }

        if (IsNew && !keyProvided)
        {
            Validation = "新建配置必须填写 API Key。";
            return false;
        }

        if (!IsNew && Selected is null)
        {
            Validation = "所编辑的配置已不存在。";
            return false;
        }

        return true;
    }

    /// <summary>保存。replacementKey 为 null 表示保留原密钥（编辑时）。调用方负责在调用后立即清空 PasswordBox。</summary>
    public async Task SaveAsync(string? replacementKey)
    {
        if (IsSaving || !CanSave)
        {
            return;
        }

        var target = IsNew ? null : Selected;
        var latest = target is null ? null : _store.Find(target.Id);
        if (!IsNew && latest is null)
        {
            Validation = "所编辑的配置已不存在。";
            return;
        }

        var name = EditName.Trim();
        var baseUrl = EditBaseUrl.Trim();
        var model = EditDefaultModel.Trim();
        var protocol = EditProtocol;

        if (latest is not null && latest.HasModelCache)
        {
            var urlChanged = !string.Equals(latest.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.Ordinal);
            var protocolChanged = latest.Protocol != protocol;
            if (urlChanged || protocolChanged || replacementKey is not null)
            {
                var reasons = new List<string>();
                if (urlChanged)
                {
                    reasons.Add("Base URL 已修改");
                }

                if (protocolChanged)
                {
                    reasons.Add($"协议由 {latest.ProtocolDisplay} 改为 {ProtocolInfo.Display(protocol)}");
                }

                if (replacementKey is not null)
                {
                    reasons.Add("输入了新的 API Key");
                }

                var reason = string.Join("；", reasons);
                if (!_dialogs.Confirm("确认保存", $"{reason}。保存后，该配置已缓存的 {latest.ModelCount} 个模型列表可能被清空，需要重新拉取；使用该配置的连接测试目标会取消，自动测试会关闭。\n\n是否继续保存？", warning: false))
                {
                    Status = replacementKey is null ? "已取消保存。" : "已取消保存。为安全起见，输入框中的 Key 已清空，请重新输入。";
                    return;
                }
            }
        }

        IsSaving = true;
        if (latest is not null)
        {
            latest.IsBusy = true;
        }

        OnPropertyChanged(nameof(CanSave));
        try
        {
            var toSave = latest is null
                ? new RelayProfile { Name = name, BaseUrl = baseUrl, Protocol = protocol, DefaultModelId = model }
                : latest.Profile with { Name = name, BaseUrl = baseUrl, Protocol = protocol, DefaultModelId = model };

            var saved = await _service.SaveProfileAsync(toSave, replacementKey, _lifetime);
            var item = _store.Upsert(saved);
            IsNew = false;
            if (ReferenceEquals(Selected, item))
            {
                LoadEditor(item);
                OnPropertyChanged(nameof(SelectedDetail));
            }
            else
            {
                Selected = item;
            }

            View.Refresh();
            Status = $"已保存“{saved.Name}”（{Format.Time(DateTimeOffset.Now)}）。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Validation = UserMessages.Describe(ex) + (replacementKey is null ? "" : "\n为安全起见，Key 输入框已清空，请重新输入。");
        }
        finally
        {
            if (latest is not null)
            {
                latest.IsBusy = false;
            }

            IsSaving = false;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(Count));
        }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } item || item.IsBusy)
        {
            return;
        }

        var message =
            $"确定删除配置“{item.Name}”吗？\n\n" +
            $"协议：{item.ProtocolDisplay}\nBase URL：{item.BaseUrl}\n\n" +
            "将从本机移除该配置、其当前密钥和模型缓存。检测历史会保留。\n\n" +
            "注意：这不是安全擦除。用于历史脱敏的旧密钥和本地备份文件中可能仍保留加密密文。不会通知中转站，也不会撤销该 Key；如需彻底失效，请到中转站后台撤销。\n\n" +
            "此操作无法撤销。";
        if (!_dialogs.Confirm("删除配置", message))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            await _service.DeleteProfileAsync(item.Id, _lifetime);
            _store.Remove(item.Id);
            Selected = null;
            ResetEditor();
            Status = $"已删除“{item.Name}”。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dialogs.ShowException("删除失败", ex);
        }
        finally
        {
            item.IsBusy = false;
            OnPropertyChanged(nameof(Count));
        }
    }
}
