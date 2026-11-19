using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>最新一轮里某个模型的结果行。Tone：none / ok / possible / bad；ContextTone：none / ok / possible / bad。</summary>
public sealed record RoundResultRow(
    string ProfileName,
    string ModelId,
    string ResultText,
    string Tone,
    string ContextText,
    string ContextTone,
    string TimeText,
    string Detail);

/// <summary>
/// 全局连接巡检的界面状态。应用生命周期单例，与模型页当前选中的配置无关。
/// - 选择：配置勾选 + 可展开的子模型勾选，保存稳定 ID（配置 GUID + 模型 ID 字符串）；首次为空；
///   取消配置勾选不清子选择；模型缓存更新后只保留仍存在的已选模型，新增模型不选；
/// - 间隔：默认 30，10–480 整数；可在开启/进行中编辑并显式保存，不打断当前轮，保存时等待计时重新开始；
/// - 授权：选择本身不授权；开启、立即巡检、保存间隔（有已授权范围时）才会弹确认并携带精确范围；空选择不发请求、不扩权；
/// - 定时：委托给纯调度类 InspectionScheduler（等待计时与轮取消分离）。
/// 所有成员在 UI 线程使用。分为 Snapshot / Selection / Commands / Interval / Rounds 几个 partial 文件。
/// </summary>
public sealed partial class GlobalInspectionViewModel : ObservableObject, IInspectionNodeHost, IDisposable
{
    private readonly ProblemNotificationCoordinator? _notifier;
    private readonly IGlobalInspectionService? _service;
    private readonly IDialogService _dialogs;
    private readonly ProfileStore? _store;
    private readonly RecordFeed? _feed;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InspectionScheduler _scheduler;
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly InspectionSelectionState _selection = new();
    private readonly Dictionary<Guid, InspectionProfileNode> _nodeIndex = [];
    private CancellationTokenSource? _manualCts;
    private InspectionSnapshot? _snapshot;
    private InspectionRoundSummary? _round;
    private ObservableCollection<RoundResultRow> _roundResults = [];
    private bool _enabled;
    private bool _busy;
    private bool _startupDone;
    private bool _loaded;
    private bool _disposed;
    private bool _selectionDirty;
    private int _draftRevision;
    private int _snapshotEpoch;
    private int _savedRetries;
    private int _maxRetries;
    private bool _refetching;
    private bool _refetchAgain;
    private bool _resultsLoading;
    private bool _resultsAgain;
    private int _lastNotifiedDone = -1;
    private int _savedInterval = InspectionScheduler.DefaultIntervalMinutes;
    private int _pendingCount;
    private string _intervalText = InspectionScheduler.DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string _intervalError = "";
    private string _status = "正在读取巡检设置…";
    private string _scopeSummary = "";
    private string _pendingText = "";
    private string _resultsNote = "";

    public GlobalInspectionViewModel(IGlobalInspectionService? service, IDialogService dialogs,
        ProfileStore? store = null, RecordFeed? feed = null, TimeProvider? time = null,
        ProblemNotificationCoordinator? notifier = null)
    {
        _notifier = notifier;
        _service = service;
        _dialogs = dialogs;
        _store = store;
        _feed = feed;
        _time = time ?? TimeProvider.System;
        _scheduler = new InspectionScheduler(RunScheduledRoundAsync, _time);
        _scheduler.NextStartChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NextStartText));
            OnPropertyChanged(nameof(StateText));
        };
        // 关闭始终可用（即使一轮正在进行）；开启与立即巡检需要空闲。
        ToggleCommand = new AsyncCommand(ToggleAsync, () => IsAvailable && (Enabled || !_busy));
        RunOnceCommand = new AsyncCommand(RunOnceAsync, () => IsAvailable && !_busy);
        // 保存间隔、确认新增范围在开启或一轮进行中也可用，且不会打断当前轮。
        ConfirmScopeCommand = new AsyncCommand(ConfirmScopeAsync, () => IsAvailable && CanConfirmScope);
        if (_store is not null)
        {
            _store.ProfileUpdated += OnStoreProfileUpdated;
            _store.Items.CollectionChanged += OnStoreItemsChanged;
        }
    }

    public ICommand ToggleCommand { get; }
    public ICommand RunOnceCommand { get; }
    public ICommand ConfirmScopeCommand { get; }

    /// <summary>配置节点（勾选 + 可展开的子模型）。</summary>
    public ObservableCollection<InspectionProfileNode> ProfileNodes { get; } = [];

    public bool IsAvailable => _service is not null;
    public bool HasProfiles => ProfileNodes.Count > 0;

    /// <summary>当前是否开启（来自 Core 持久化设置）。</summary>
    public bool Enabled
    {
        get => _enabled;
        private set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(NextStartText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(CurrentItemText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ToggleText => Enabled ? "关闭自动巡检" : "开启自动巡检";

    public string StateText => !Enabled
        ? "自动巡检：关闭"
        : _busy ? "自动巡检：已开启 · 巡检进行中"
        : _scheduler.IsRunning ? "自动巡检：已开启" : "自动巡检：已开启 · 等待可用的已授权目标";

    public string NextStartText => Enabled && _scheduler.NextStartAt is { } n
        ? $"下一轮预约：{n.ToLocalTime():MM-dd HH:mm}"
        : "";

    // ---- 间隔 ----

    /// <summary>间隔输入。任何时候都可编辑（开启、进行中也可），只有点“保存间隔”才生效。</summary>
    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (SetProperty(ref _intervalText, value ?? ""))
            {
                _draftRevision++;
                UpdateIntervalState();
            }
        }
    }

    public string IntervalError
    {
        get => _intervalError;
        private set => SetProperty(ref _intervalError, value);
    }

    /// <summary>输入框内容是有效整数且与已保存间隔不同。</summary>
    public bool IntervalUnsaved => IntervalError.Length == 0 && InspectionScheduler.TryParseInterval(IntervalText, out var m) && m != _savedInterval;
    public int MaxRetries
    {
        get => _maxRetries;
        set { if (value is >= 0 and <= 2 && SetProperty(ref _maxRetries, value)) { _draftRevision++; UpdateIntervalState(); } }
    }
    public IReadOnlyList<int> RetryOptions { get; } = [0, 1, 2];
    public bool HasDraft => _selectionDirty || IntervalUnsaved || IntervalError.Length > 0 || MaxRetries != _savedRetries;

    public string IntervalHint => IntervalError.Length > 0
        ? IntervalError
        : $"已确认：间隔 {_savedInterval} 分钟，最大重试 {_savedRetries} 次。" + (HasDraft ? " 有未确认的更改。" : "");

    private void UpdateIntervalState()
    {
        IntervalError = InspectionScheduler.TryParseInterval(IntervalText, out _)
            ? ""
            : $"间隔需为 {InspectionScheduler.MinIntervalMinutes}–{InspectionScheduler.MaxIntervalMinutes} 的整数分钟。";
        OnPropertyChanged(nameof(IntervalUnsaved));
        OnPropertyChanged(nameof(IntervalHint));
        OnPropertyChanged(nameof(CanConfirmScope));
        UpdatePendingText();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- 选择与授权概览 ----

    /// <summary>例如“已选 2 个配置、7 个模型；其中已授权 5 个，待确认 2 个。”</summary>
    public string ScopeSummary
    {
        get => _scopeSummary;
        private set => SetProperty(ref _scopeSummary, value);
    }

    public string PendingText
    {
        get => _pendingText;
        private set => SetProperty(ref _pendingText, value);
    }

    /// <summary>所选范围有未确认模型时可确认，不依赖巡检开关。</summary>
    public bool CanConfirmScope => _snapshot is not null && IntervalError.Length == 0 && (HasDraft || _pendingCount > 0 || _snapshot.NeedsUpgradeConfirmation);

    // ---- 最新一轮 ----

    public ObservableCollection<RoundResultRow> RoundResults
    {
        get => _roundResults;
        private set => SetProperty(ref _roundResults, value);
    }

    public bool HasRound => _round is not null;

    public string RoundStatusText
    {
        get
        {
            if (_round is not { } r)
            {
                return "还没有巡检记录。开启巡检或点“立即巡检一轮”后，这里显示最新一轮的状态和各模型结果。";
            }

            var kind = r.Trigger == InspectionTrigger.Manual ? "立即巡检" : "定时巡检";
            var state = r.CompletedAt is { } done
                ? (r.Cancelled ? $"已取消（{Format.Time(done)}）" : $"已完成（{Format.Time(done)}）")
                : _busy ? "进行中" : "未完成（可能被中断）";
            return $"{kind} · 开始于 {Format.Time(r.StartedAt)} · {state}" +
                (r.IntervalMinutes is int m && r.MaxRetries is int retry ? $" · 本轮采用 {m} 分钟间隔、最多重试 {retry} 次" : "");
        }
    }

    public string RoundCountsText => _round is not { } r ? "" : FormatCounts(r);

    /// <summary>
    /// 连接巡检展示统计统一为三类终态（成功、成功（可能）、失败）加跳过；历史遗留的 UnknownCount 合并计入失败。
    /// 总数大于各项之和时（进行中、被取消或中断）单列“未处理”，不把它们算作成功。
    /// </summary>
    internal static string FormatCounts(InspectionRoundSummary r)
    {
        var failed = r.Failed + r.UnknownCount;
        var text = $"共 {r.Total} 项：成功 {r.Succeeded}，成功（可能） {r.PossibleSuccessCount}，失败 {failed}，跳过 {r.Skipped}";
        var pending = r.Total - FinishedCount(r);
        return pending > 0 ? text + $"，未处理 {pending}" : text;
    }

    /// <summary>已有结论（含跳过）的项数，用于进度变化通知与“未处理”计算。</summary>
    internal static int FinishedCount(InspectionRoundSummary r)
        => r.Succeeded + r.PossibleSuccessCount + r.Failed + r.UnknownCount + r.Skipped;

    public string CurrentItemText => _busy && _round is { CurrentProfileName.Length: > 0 } r
        ? $"正在检测：{r.CurrentProfileName}" + (r.CurrentModelId is { Length: > 0 } m ? " / " + m : "")
        : "";

    /// <summary>说明“只显示最新一轮；完整历史在连接记录窗口”，以及本轮没有记录的原因。</summary>
    public string ResultsNote
    {
        get => _resultsNote;
        private set => SetProperty(ref _resultsNote, value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_store is not null)
        {
            _store.ProfileUpdated -= OnStoreProfileUpdated;
            _store.Items.CollectionChanged -= OnStoreItemsChanged;
        }

        _scheduler.Dispose();
        try { _manualCts?.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Cancel();
    }
}
