using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>模型列表与检测页。</summary>
public sealed class ProbeViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopService _service;
    private readonly ProfileStore _store;
    private readonly ProbeRunCoordinator _runs;
    private readonly IDialogService _dialogs;
    private readonly ModelRefreshCoordinator _refresh;
    private ProfileItem? _profile;
    private ICollectionView? _modelsView;
    private string _modelSearch = "";
    private ModelEntry? _selectedModel;
    private string _manualModel = "";
    private string _claimedModel = "";
    private string _status = "";
    private bool _isSubmitting;
    private string _submitStatus = "";
    private ProbeRecordViewModel? _currentRun;

    public ProbeViewModel(IDesktopService service, ProfileStore store, ProbeRunCoordinator runs, IDialogService dialogs,
        ModelRefreshCoordinator refresh, RecordFeed feed)
    {
        _service = service;
        _store = store;
        _runs = runs;
        _dialogs = dialogs;
        _refresh = refresh;
        Connection = new ConnectionTestViewModel(
            service as IDirectConnectionService ?? throw new ArgumentException("服务不支持直连连接测试。", nameof(service)),
            dialogs, runs.Lifetime, feed: feed);
        Recent = new ModelRecentResultsViewModel(service as ILatestResultsService, runs, feed, runs.Lifetime);
        _refresh.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelRefreshCoordinator.IsRunning))
            {
                OnPropertyChanged(nameof(IsRefreshing));
                OnPropertyChanged(nameof(CanStart));
                CommandManager.InvalidateRequerySuggested();
            }
        };
        _refresh.Outcome += OnRefreshOutcome;
        // 模型页 Step 1 只做“拉取当前配置”；全部拉取在配置管理页。
        RefreshModelsCommand = new AsyncCommand(RefreshModelsAsync, () => Profile is { IsBusy: false } && !IsRefreshing);
        SetDefaultCommand = new AsyncCommand(SetDefaultAsync, () => Profile is { IsBusy: false } && EffectiveModelId.Length > 0 && !IsRefreshing);
        UseDefaultCommand = new RelayCommand(UseDefault, () => Profile is { DefaultModelId.Length: > 0 });
        StartProbeCommand = new AsyncCommand(StartProbeAsync, () => CanStart);
        ClearModelSearchCommand = new RelayCommand(() => ModelSearch = "");
        _store.ProfileUpdated += (_, id) =>
        {
            if (Profile?.Id == id)
            {
                RebuildModels();
                OnPropertyChanged(nameof(ProfileSummary));
                OnPropertyChanged(nameof(ProbeProtocolNote));
                OnPropertyChanged(nameof(CanStart));
                SyncConnectionTarget();
            }
        };
        _store.Items.CollectionChanged += (_, _) =>
        {
            if (Profile is { } p && _store.Find(p.Id) is null)
            {
                Profile = null;
            }
        };
        _runs.RecordsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveRunWarning));
            OnPropertyChanged(nameof(CanStart));
        };
    }

    public System.Collections.ObjectModel.ObservableCollection<ProfileItem> Profiles => _store.Items;

    public ICommand RefreshModelsCommand { get; }
    public ICommand SetDefaultCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand StartProbeCommand { get; }
    public ICommand ClearModelSearchCommand { get; }

    /// <summary>本地直连连接测试（独立于 BazaarLink 检测）。</summary>
    public ConnectionTestViewModel Connection { get; }

    /// <summary>当前配置 + 模型最近一次已完成的直连测试 / 鉴别检测（含失败，来自本地历史）。</summary>
    public ModelRecentResultsViewModel Recent { get; }

    public ProfileItem? Profile
    {
        get => _profile;
        set
        {
            if (SetProperty(ref _profile, value))
            {
                _selectedModel = null;
                ManualModel = "";
                ClaimedModel = "";
                ModelSearch = "";
                Status = "";
                RebuildModels();
                if (value is { DefaultModelId.Length: > 0 })
                {
                    UseDefault();
                }

                OnPropertyChanged(nameof(ProfileSummary));
                OnPropertyChanged(nameof(ProbeProtocolNote));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(HasProfile));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasProfile => Profile is not null;

    public string ProfileSummary => Profile is { } p
        ? $"协议：{p.ProtocolDisplay}　Base URL：{p.BaseUrl}\n模型缓存：{p.ModelSummary}　默认模型：{p.DefaultModelText}"
        : "请先选择一个配置。";

    public ICollectionView? ModelsView
    {
        get => _modelsView;
        private set => SetProperty(ref _modelsView, value);
    }

    public string ModelCountText { get; private set; } = "";

    public string ModelSearch
    {
        get => _modelSearch;
        set
        {
            if (SetProperty(ref _modelSearch, value))
            {
                ModelsView?.Refresh();
                UpdateCountText();
            }
        }
    }

    public ModelEntry? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                if (value is not null && ManualModel.Length > 0)
                {
                    ManualModel = "";
                }

                OnModelChanged();
            }
        }
    }

    /// <summary>手填模型 ID。非空时优先于列表选择。</summary>
    public string ManualModel
    {
        get => _manualModel;
        set
        {
            if (SetProperty(ref _manualModel, value))
            {
                OnModelChanged();
            }
        }
    }

    public string ClaimedModel
    {
        get => _claimedModel;
        set => SetProperty(ref _claimedModel, value);
    }

    public string EffectiveModelId => ManualModel.Trim().Length > 0 ? ManualModel.Trim() : SelectedModel?.Id ?? "";

    public string EffectiveModelText => EffectiveModelId.Length > 0
        ? $"本次调用模型：{EffectiveModelId}" + (ManualModel.Trim().Length > 0 ? "（手填）" : "（来自列表）")
        : "尚未选择或填写模型。";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SubmitStatus
    {
        get => _submitStatus;
        private set => SetProperty(ref _submitStatus, value);
    }

    /// <summary>有拉取在进行（可能是配置页发起的全部拉取）。期间本页不能再发起拉取。</summary>
    public bool IsRefreshing => _refresh.IsRunning;

    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                OnPropertyChanged(nameof(CanStart));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>当前配置所选协议的 Probe 说明：三种协议都可提交，但尚未真实端到端验证。</summary>
    public string? ProbeProtocolNote => Profile is { } p ? ProtocolInfo.ProbeProtocolNote(p.Protocol) : null;

    public bool CanStart => Profile is { IsBusy: false } && EffectiveModelId.Length > 0 && !IsSubmitting && !_runs.IsSubmitting && !IsRefreshing;

    public string? ActiveRunWarning => _runs.AnyActiveRecord
        ? "本地存在未结束或结果未知的检测记录。为避免重复提交和重复计费，需先让该任务结束（恢复查询），或在“历史记录”中删除其本地记录，才能发起新检测。"
        : null;

    /// <summary>本页展示的检测记录，固定绑定到一条 LocalId，切换配置不会影响它。</summary>
    public ProbeRecordViewModel? CurrentRun
    {
        get => _currentRun;
        set
        {
            if (SetProperty(ref _currentRun, value))
            {
                OnPropertyChanged(nameof(HasCurrentRun));
            }
        }
    }

    public bool HasCurrentRun => CurrentRun is not null;

    private void OnModelChanged()
    {
        OnPropertyChanged(nameof(EffectiveModelId));
        OnPropertyChanged(nameof(EffectiveModelText));
        OnPropertyChanged(nameof(CanStart));
        SyncConnectionTarget();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SyncConnectionTarget()
    {
        Connection.SetTarget(Profile, EffectiveModelId);
        Recent.SetTarget(Profile, EffectiveModelId);
    }

    public void Dispose()
    {
        _refresh.Outcome -= OnRefreshOutcome;
        Recent.Dispose();
        Connection.Dispose();
    }

    private void RebuildModels()
    {
        var keep = SelectedModel?.Id;
        if (Profile is null)
        {
            ModelsView = null;
            _selectedModel = null;
            OnPropertyChanged(nameof(SelectedModel));
            UpdateCountText();
            OnModelChanged();
            return;
        }

        var list = Profile.Profile.Models.ToList();
        var view = CollectionViewSource.GetDefaultView(list);
        view.Filter = o => o is ModelEntry m && (ModelSearch.Trim().Length == 0
            || m.Id.Contains(ModelSearch.Trim(), StringComparison.OrdinalIgnoreCase)
            || (m.OwnedBy?.Contains(ModelSearch.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
        ModelsView = view;
        _selectedModel = keep is null ? null : list.FirstOrDefault(m => m.Id == keep);
        OnPropertyChanged(nameof(SelectedModel));
        UpdateCountText();
        OnModelChanged();
    }

    private void UpdateCountText()
    {
        if (Profile is null)
        {
            ModelCountText = "";
        }
        else if (!Profile.HasModelCache)
        {
            ModelCountText = "尚未拉取模型列表。可点击“拉取模型列表”，或直接在下方手填模型 ID。";
        }
        else
        {
            var shown = ModelsView?.Cast<object>().Count() ?? 0;
            ModelCountText = ModelCacheText.CountLine(Profile.ModelCount, shown, Profile.FetchedAtText);
        }

        OnPropertyChanged(nameof(ModelCountText));
    }

    private void UseDefault()
    {
        if (Profile is not { DefaultModelId.Length: > 0 } p)
        {
            return;
        }

        var match = p.Profile.Models.FirstOrDefault(m => m.Id == p.DefaultModelId);
        if (match is not null)
        {
            ManualModel = "";
            SelectedModel = match;
        }
        else
        {
            SelectedModel = null;
            ManualModel = p.DefaultModelId;
        }
    }

    private Task RefreshModelsAsync() => Profile is { IsBusy: false } item ? _refresh.RefreshOneAsync(item) : Task.CompletedTask;

    /// <summary>协调器处理完某个配置：只在它是当前所选配置且为单拉时更新本页状态（批量结果显示在配置管理页）。</summary>
    private void OnRefreshOutcome(object? sender, RefreshOutcome e)
    {
        if (e.IsBatch || Profile?.Id != e.ProfileId)
        {
            return;
        }

        Status = e.Outcome switch
        {
            BatchItemOutcome.Succeeded => $"“{e.ProfileName}”：{e.Detail}",
            BatchItemOutcome.Failed => $"“{e.ProfileName}”拉取失败：{e.Detail}",
            BatchItemOutcome.Skipped => $"“{e.ProfileName}”未拉取：{e.Detail}",
            _ => $"“{e.ProfileName}”拉取已取消。",
        };
    }

    private async Task SetDefaultAsync()
    {
        if (Profile is not { IsBusy: false } item)
        {
            return;
        }

        var model = EffectiveModelId;
        if (model.Length == 0 || model.Length > 256 || model.Any(char.IsControl))
        {
            Status = "模型 ID 无效：不能为空，最长 256 字符，不能包含控制字符。";
            return;
        }

        if (item.DefaultModelId == model)
        {
            Status = $"“{model}”已是默认模型。";
            return;
        }

        item.IsBusy = true;
        try
        {
            // 以当前最新的配置记录为基础，只修改默认模型；Base URL 与 Key 不变，模型缓存得以保留。
            var latest = _store.Find(item.Id)?.Profile ?? item.Profile;
            var saved = await _service.SaveProfileAsync(latest with { DefaultModelId = model }, null, _runs.Lifetime);
            _store.Upsert(saved);
            Status = $"已将“{model}”设为“{saved.Name}”的默认模型。";
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dialogs.ShowException("保存默认模型失败", ex);
        }
        finally
        {
            item.IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task StartProbeAsync()
    {
        if (!CanStart || Profile is not { } item)
        {
            return;
        }

        var profile = item.Profile;
        var modelId = EffectiveModelId;
        var claimed = ClaimedModel.Trim();
        if (modelId.Length > 256 || modelId.Any(char.IsControl) || claimed.Length > 256 || claimed.Any(char.IsControl))
        {
            SubmitStatus = "模型 ID 或声明模型无效：最长 256 字符，不能包含控制字符。";
            return;
        }

        if (_runs.AnyActiveRecord)
        {
            _dialogs.Info("暂不能发起新检测", ActiveRunWarning ?? "");
            return;
        }

        var message =
            "即将把以下内容发送给第三方服务 BazaarLink：\n\n" +
            $"接收方：POST {Endpoints.ProbeRunUrl}\n\n" +
            $"• 中转站 Base URL：{profile.BaseUrl}\n" +
            $"• {ProtocolInfo.ProbeProtocolConfirmLine(profile.Protocol)}\n" +
            $"• 完整 API Key：配置“{profile.Name}”保存的 Key（版本 {profile.KeyRevision}，此处不显示）\n" +
            $"• 调用模型：{modelId}\n" +
            $"• 声明模型：{(claimed.Length > 0 ? claimed : "未填写")}\n\n" +
            "请注意：\n" +
            "1. BazaarLink 会使用你的 Key 调用中转站，会消耗该 Key 的额度并可能产生费用。\n" +
            "2. 检测报告可能被公开，本程序无法确认报告是否私密。\n" +
            "3. 服务方对 Key 和报告的保留期限、处理方式尚未确认。\n" +
            "4. 提交后无法由本程序取消；关闭程序或停止查询不会停止远端检测。\n" +
            "5. 提交只发送一次，不会自动重试。\n\n" +
            "建议使用低额度、可随时撤销的测试专用 Key。\n\n" +
            "只有确认发送才会发送。确认发送吗？";
        if (!_dialogs.Confirm("确认发送完整 API Key 给 BazaarLink", message))
        {
            SubmitStatus = "已取消，未发送任何请求。";
            return;
        }

        var consent = new OutboundConsent(
            profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.Probe, Endpoints.ProbeRunUrl, modelId, DateTimeOffset.UtcNow,
            Protocol: profile.Protocol);
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);

        IsSubmitting = true;
        SubmitStatus = "提交中…请勿关闭程序。提交只会发送一次。";
        try
        {
            var vm = await _runs.SubmitAsync(profile.Id, modelId, claimed.Length > 0 ? claimed : null, consent);
            CurrentRun = vm;
            SubmitStatus = vm is null ? "提交未返回记录。" : vm.State switch
            {
                LocalRunState.Running => "已提交，远端检测进行中；本地每 5 秒查询一次进度。",
                LocalRunState.SubmissionUnknown => "提交结果未知。请勿重复提交；远端可能已开始检测并产生费用。",
                LocalRunState.Failed => "提交失败：" + (vm.Error ?? "请求未完成。"),
                _ => $"当前状态：{vm.StateText}",
            };
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CurrentRun = _runs.LatestFor(profile.Id, modelId, startedAt) ?? CurrentRun;
            SubmitStatus = "提交未成功：" + UserMessages.Describe(ex);
            _dialogs.ShowException("提交检测失败", ex);
        }
        finally
        {
            IsSubmitting = false;
            OnPropertyChanged(nameof(ActiveRunWarning));
        }
    }
}
