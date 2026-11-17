using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>
/// 身份识别巡检页的界面状态。应用生命周期单例，范围、授权、计时与连接巡检完全独立。
/// - 每轮冻结目标清单，逐个向 BazaarLink 提交官方 Probe 并等待当前目标结束后才处理下一个；
/// - 等待复用 ProbeRunCoordinator 的每记录单飞轮询，不另开查询通道；单个目标最多等待 10 分钟（本地上限）；
/// - 选择只是偏好，不授权；开启、立即运行一轮、保存间隔、确认新增范围都会列出精确范围并要求确认；
/// - 默认关闭、间隔默认 30 分钟（10–480）；每轮结束后等待完整间隔，不重叠、不补跑；
/// - 手动和定时共用 Core 的最终提交闸门；所有成员在 UI 线程使用。
/// 分为 Snapshot / Selection / Commands / Rounds 几个 partial 文件。
/// </summary>
public sealed partial class IdentityInspectionViewModel : ObservableObject, IInspectionNodeHost, IDisposable
{
    private readonly IIdentityInspectionService? _service;
    private readonly IDialogService _dialogs;
    private readonly ProfileStore _store;
    private readonly ProbeRunCoordinator _runs;
    private readonly Action<Guid>? _openReport;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InspectionScheduler _scheduler;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly InspectionSelectionState _selection = new();
    private readonly Dictionary<Guid, InspectionProfileNode> _nodeIndex = [];
    private readonly Dictionary<Guid, IdentityAttemptRow> _rowIndex = [];
    private CancellationTokenSource? _roundCts;
    private IdentityInspectionSnapshot? _snapshot;
    private IdentityInspectionRoundSummary? _round;
    private Guid? _stopRoundId;
    private string _stopNote = "";
    private bool _enabled;
    private bool _busy;
    private bool _startupDone;
    private bool _loaded;
    private bool _disposed;
    private int _savedInterval = InspectionScheduler.DefaultIntervalMinutes;
    private int _pendingCount;
    private string _intervalText = InspectionScheduler.DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string _intervalError = "";
    private string _status = "正在读取身份识别巡检设置…";
    private string _scopeSummary = "";
    private string _pendingText = "";

    public IdentityInspectionViewModel(IIdentityInspectionService? service, IDialogService dialogs, ProfileStore store,
        ProbeRunCoordinator runs, Action<Guid>? openReport = null, TimeProvider? time = null)
    {
        _service = service;
        _dialogs = dialogs;
        _store = store;
        _runs = runs;
        _openReport = openReport;
        _time = time ?? TimeProvider.System;
        _scheduler = new InspectionScheduler(RunScheduledRoundAsync, _time);
        _scheduler.NextStartChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NextStartText));
            OnPropertyChanged(nameof(StateText));
        };
        // 关闭始终可用（即使一轮正在进行）；开启与立即运行需要空闲。
        ToggleCommand = new AsyncCommand(ToggleAsync, () => IsAvailable && (Enabled || !_busy));
        RunOnceCommand = new AsyncCommand(RunOnceAsync, () => IsAvailable && !_busy);
        SaveIntervalCommand = new AsyncCommand(SaveIntervalAsync, () => IsAvailable && IntervalUnsaved && IntervalError.Length == 0);
        ConfirmScopeCommand = new AsyncCommand(ConfirmScopeAsync, () => IsAvailable && CanConfirmScope);
        _store.ProfileUpdated += OnStoreProfileUpdated;
        _store.Items.CollectionChanged += OnStoreItemsChanged;
        _runs.RecordsChanged += OnRunsChanged;
    }

    public ICommand ToggleCommand { get; }
    public ICommand RunOnceCommand { get; }
    public ICommand SaveIntervalCommand { get; }
    public ICommand ConfirmScopeCommand { get; }

    public ObservableCollection<InspectionProfileNode> ProfileNodes { get; } = [];

    /// <summary>最新一轮的各目标状态（就地更新）。</summary>
    public ObservableCollection<IdentityAttemptRow> AttemptRows { get; } = [];

    public bool IsAvailable => _service is not null;
    public bool HasProfiles => ProfileNodes.Count > 0;
    public bool HasAttempts => AttemptRows.Count > 0;

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
                OnPropertyChanged(nameof(CurrentStateHint));
                OnPropertyChanged(nameof(ShowProgress));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ToggleText => Enabled ? "关闭定时身份识别巡检" : "开启定时身份识别巡检…";

    public string StateText => !Enabled
        ? "定时身份识别巡检：关闭"
        : _scheduler.IsRunning
            ? $"定时身份识别巡检：已开启，每轮结束后等待 {_savedInterval} 分钟"
            : $"定时身份识别巡检：已保存为开启（每轮结束后等待 {_savedInterval} 分钟），本次运行尚未在计时";

    public string NextStartText => Enabled && _scheduler.NextStartAt is { } n
        ? $"下一轮最早开始于 {n.ToLocalTime():HH:mm}（每轮结束后等待整个间隔；保存新间隔会从保存时重新计时）"
        : "";

    public string CurrentStateHint => _busy ? "正在检测，期间不能再次开始，也不会补跑。" : "";

    // ---- 间隔 ----

    /// <summary>间隔输入。任何时候都可编辑，只有点“保存间隔”才生效。</summary>
    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (SetProperty(ref _intervalText, value ?? ""))
            {
                UpdateIntervalState();
            }
        }
    }

    public string IntervalError
    {
        get => _intervalError;
        private set => SetProperty(ref _intervalError, value);
    }

    public bool IntervalUnsaved => IntervalError.Length == 0 && InspectionScheduler.TryParseInterval(IntervalText, out var m) && m != _savedInterval;

    public string IntervalHint => IntervalError.Length > 0
        ? IntervalError
        : IntervalUnsaved
            ? $"尚未保存：当前生效间隔仍为 {_savedInterval} 分钟。点“保存间隔”后才会生效。"
            : $"当前生效间隔：{_savedInterval} 分钟（可设 {InspectionScheduler.MinIntervalMinutes}–{InspectionScheduler.MaxIntervalMinutes}，默认 {InspectionScheduler.DefaultIntervalMinutes}）。";

    private void UpdateIntervalState()
    {
        IntervalError = InspectionScheduler.TryParseInterval(IntervalText, out _)
            ? ""
            : $"间隔需为 {InspectionScheduler.MinIntervalMinutes}–{InspectionScheduler.MaxIntervalMinutes} 的整数分钟。";
        OnPropertyChanged(nameof(IntervalUnsaved));
        OnPropertyChanged(nameof(IntervalHint));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- 范围概览 ----

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

    /// <summary>所选范围有未确认目标时可确认，不依赖巡检开关。</summary>
    public bool CanConfirmScope => _pendingCount > 0;

    // ---- 最新一轮 ----

    public bool HasRound => _round is not null;

    public bool ShowProgress => _busy && _round is { Attempts.Count: > 0 };

    public double ProgressMaximum => Math.Max(1, _round?.Total ?? 1);

    public double ProgressValue => _round is null
        ? 0
        : _round.Attempts.Count(a => a.State is IdentityInspectionAttemptState.Completed
            or IdentityInspectionAttemptState.Skipped or IdentityInspectionAttemptState.Unknown);

    public string RoundStatusText
    {
        get
        {
            if (_round is not { } r)
            {
                return "还没有身份识别巡检记录。点“立即巡检一轮”或开启定时后，这里显示最新一轮的进度和结果。";
            }

            var kind = IdentityInspectionText.Kind(r.Trigger) + "身份识别巡检";

            var state = r.CompletedAt is { } done
                ? (r.Cancelled ? $"已取消（{Format.Time(done)}）" : $"已结束（{Format.Time(done)}）")
                : _busy ? "进行中" : "未完成（可能被中断）";
            return $"{kind} · 开始于 {Format.Time(r.StartedAt)} · {state}";
        }
    }

    public string RoundCountsText
    {
        get
        {
            if (_round is not { } r)
            {
                return "";
            }

            var problems = AttemptRows.Count(row => row.Tone is "bad" or "warn");
            return $"目标共 {r.Total} 个：已完成 {r.Completed}，已跳过 {r.Skipped}，需要关注 {problems}。身份判断是服务基于样本的概率性结论，不是模型真实身份的确定性证明。";
        }
    }

    /// <summary>本轮提前结束的原因（本地等待上限、查询暂停、限流，或 Core 报告的停止代码）。</summary>
    public string RoundStopText
    {
        get
        {
            if (_round is not { } r)
            {
                return "";
            }

            var parts = new List<string>();
            if (_stopRoundId == r.RoundId && _stopNote.Length > 0)
            {
                parts.Add(_stopNote);
            }

            else if (!string.IsNullOrEmpty(r.StopCode) && !r.Cancelled)
            {
                parts.Add(IdentityInspectionText.StopCodeText(r.StopCode));
            }

            return string.Join("\n", parts);
        }
    }

    private void OnRoundChanged()
    {
        OnPropertyChanged(nameof(HasRound));
        OnPropertyChanged(nameof(RoundStatusText));
        OnPropertyChanged(nameof(RoundCountsText));
        OnPropertyChanged(nameof(RoundStopText));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
    }

    /// <summary>查看相关鉴别报告：交给主窗口在“模型鉴别记录”窗口里定位该条记录；只读本地数据。</summary>
    private void OpenReport(Guid probeLocalId) => _openReport?.Invoke(probeLocalId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.ProfileUpdated -= OnStoreProfileUpdated;
        _store.Items.CollectionChanged -= OnStoreItemsChanged;
        _runs.RecordsChanged -= OnRunsChanged;
        _scheduler.Dispose();
        try { _roundCts?.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Cancel();
    }
}
