using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopService _service;
    private readonly IDialogService _dialogs;
    private readonly ProbeRunCoordinator _runs;
    private readonly AutomaticProbeNotifier _probeNotifier;
    private string _statusText = "正在读取本地数据…";
    private bool _isLoaded;
    private string? _loadError;
    private int _selectedPage;
    private bool _disposed;
    private bool _backgroundStopped;

    /// <param name="sink">Windows 托盘通知出口（复用现有托盘）。为 null 时不显示任何通知，检测不受影响。</param>
    public MainViewModel(IDesktopService service, IDialogService dialogs, bool isCustomDataDirectory,
        IProblemToastSink? sink = null, IMigrationDialogs? migrationDialogs = null)
    {
        _service = service;
        _dialogs = dialogs;
        _runs = new ProbeRunCoordinator(service, dialogs);
        Store = new ProfileStore();
        Feed = new RecordFeed();
        Refresh = new ModelRefreshCoordinator(service, Store, dialogs, _runs.Lifetime);
        Profiles = new ProfilesViewModel(service, Store, dialogs, Refresh, _runs.Lifetime);
        var notificationService = service as INotificationService;
        var notifier = new ProblemNotificationCoordinator(notificationService, sink);
        Probe = new ProbeViewModel(service, Store, _runs, dialogs, Refresh, Feed);
        History = new HistoryViewModel(service, _runs, dialogs);
        Inspection = new GlobalInspectionViewModel(service as IGlobalInspectionService, dialogs, Store, Feed, notifier: notifier);
        Notifications = new NotificationSettingsViewModel(notificationService, sink);
        _probeNotifier = new AutomaticProbeNotifier(_runs, notifier, _runs.Lifetime);
        // 身份识别巡检逐个提交并复用同一个轮询协调器等待结果；“查看报告”只读本地历史。
        IdentityInspection = new IdentityInspectionViewModel(service as IIdentityInspectionService, dialogs, Store, _runs,
            id => _ = OpenProbeReportAsync(id));
        ConnectionHistory = new ConnectionHistoryViewModel(service as IGlobalInspectionService, dialogs, Feed);
        OpenConnectionHistoryCommand = new RelayCommand(() => OpenWindowRequested?.Invoke("connections"));
        OpenProbeHistoryCommand = new RelayCommand(() => OpenWindowRequested?.Invoke("probe"));
        IsCustomDataDirectory = isCustomDataDirectory;
        // 完整数据迁移：只在逻辑空库导入；提交后进入等待重启，停止全部后台工作。
        Migration = new MigrationViewModel(service as IMigrationService, dialogs, migrationDialogs ?? new MigrationDialogs(),
            () => service.DataDirectory, () => IsLoaded && !HasLoadError,
            () => (Store.Items.Count, _runs.Records.Count), MigrationBlockingWork, StopForRestart);
        Migration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MigrationViewModel.IsLocked))
            {
                OnPropertyChanged(nameof(IsInteractive));
            }
        };
        ReloadCommand = new AsyncCommand(InitializeAsync, () => !Migration.IsLocked);
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
        GoToProfilesCommand = new RelayCommand(() => SelectedPage = 0);
        GoToHistoryCommand = new RelayCommand(() => SelectedPage = 5);
        _runs.RecordsChanged += (_, _) => OnPropertyChanged(nameof(RunSummary));
    }

    /// <summary>完整数据备份与迁移（帮助与设置页）。</summary>
    public MigrationViewModel Migration { get; }

    /// <summary>主导航可操作：已读取本地数据，且不在迁移中、不在等待重启。</summary>
    public bool IsInteractive => IsLoaded && !Migration.IsLocked;

    public ProfileStore Store { get; }

    /// <summary>直连连接记录变更通知（手动测试、巡检、删除、清空）。</summary>
    public RecordFeed Feed { get; }

    /// <summary>模型列表拉取的共享协调器（配置页单拉/全部拉取，模型页单拉）。</summary>
    public ModelRefreshCoordinator Refresh { get; }
    public ProfilesViewModel Profiles { get; }
    public ProbeViewModel Probe { get; }
    public HistoryViewModel History { get; }

    /// <summary>全局连接巡检，应用生命周期单例；与当前选中配置无关。</summary>
    public GlobalInspectionViewModel Inspection { get; }

    /// <summary>身份识别巡检（定时向 BazaarLink 逐个提交 Probe），应用生命周期单例；范围、授权、计时均独立于连接巡检。</summary>
    public IdentityInspectionViewModel IdentityInspection { get; }

    /// <summary>设置页的 Windows 问题通知开关。</summary>
    public NotificationSettingsViewModel Notifications { get; }

    /// <summary>连接记录窗口使用的 VM，与巡检共用同一 Core 服务，不依赖选中配置。</summary>
    public ConnectionHistoryViewModel ConnectionHistory { get; }

    /// <summary>由 MainWindow 设置：导航与托盘请求打开非模态窗口。</summary>
    public Action<string>? OpenWindowRequested { get; set; }
    public ICommand ReloadCommand { get; }
    public ICommand OpenDataDirectoryCommand { get; }
    public ICommand GoToProfilesCommand { get; }
    public ICommand GoToHistoryCommand { get; }
    public ICommand OpenConnectionHistoryCommand { get; }
    public ICommand OpenProbeHistoryCommand { get; }

    public string DataDirectory => _service.DataDirectory;
    public bool IsCustomDataDirectory { get; }
    public string DataDirectoryNote => IsCustomDataDirectory ? "（由 --data-dir 指定的测试目录）" : "（默认位置）";

    public int SelectedPage
    {
        get => _selectedPage;
        set => SetProperty(ref _selectedPage, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (SetProperty(ref _isLoaded, value))
            {
                OnPropertyChanged(nameof(IsInteractive));
            }
        }
    }

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
            {
                OnPropertyChanged(nameof(HasLoadError));
            }
        }
    }

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    public string RunSummary
    {
        get
        {
            if (_backgroundStopped)
            {
                return "已停止全部后台查询，等待重新打开程序";
            }

            var polling = _runs.Records.Count(r => r.IsAutoPolling);
            var active = _runs.Records.Count(r => r.IsActive);
            if (polling > 0)
            {
                return $"自动查询中：{polling} 个任务";
            }

            return active > 0 ? $"未结束/结果未知的记录：{active} 条（启动时不自动联网）" : "无进行中的检测";
        }
    }

    /// <summary>启动时只读取本地数据，不发起任何网络请求。</summary>
    public async Task InitializeAsync()
    {
        // 导入提交后不在本进程内重新初始化：那会恢复巡检与自动查询，并可能用旧内存状态写回。
        Migration.SyncFromService();
        if (Migration.IsLocked || _backgroundStopped)
        {
            return;
        }

        StatusText = "正在读取本地数据…";
        LoadError = null;
        try
        {
            await LoadOnceAsync();
        }
        catch (ClientOperationException ex) when (ex.Code == "StorageRecovered")
        {
            _dialogs.Info("已从备份恢复", UserMessages.FormatClient(ex));
            try
            {
                await LoadOnceAsync();
            }
            catch (Exception retry) when (retry is not OperationCanceledException)
            {
                Fail(retry);
                return;
            }
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        if (Migration.IsLocked || _backgroundStopped)
        {
            return;
        }

        IsLoaded = true;
        // 身份识别巡检发起、已有远端任务标识且未结束的鉴别记录：恢复只读查询（GET，不重新提交，也不重放旧轮次剩余目标）。
        // 先恢复查询再登记通知基线，避免把仍在等待的记录当成最终状态。
        _runs.ResumeAutomaticRecords();
        _probeNotifier.Start();
        // 启动时只恢复已授权的巡检；未授权或关闭时不发请求。
        _ = Inspection.LoadAsync();
        _ = IdentityInspection.LoadAsync();
        _ = Notifications.LoadAsync();
        StatusText = $"已读取 {Store.Items.Count} 个配置、{_runs.Records.Count} 条历史。读取本地数据时未联网。模型列表、手动 Probe 检测与手动连接测试逐次确认；此前已授权的连接巡检会在已授权范围内自动恢复并立即运行一轮；身份识别巡检只恢复计时（第一轮在一个完整间隔之后），既有鉴别任务仅恢复只读查询，不会重新提交。";
        OnPropertyChanged(nameof(RunSummary));
    }

    private async Task LoadOnceAsync()
    {
        var profiles = await _service.GetProfilesAsync(_runs.Lifetime);
        Store.Load(profiles);
        var history = await _service.GetHistoryAsync(_runs.Lifetime);
        _runs.MergeHistory(history);
        _runs.AnnotateStartupRecords();
    }

    private void Fail(Exception ex)
    {
        IsLoaded = false;
        LoadError = "无法读取本地数据：" + UserMessages.Describe(ex) + "\n未对现有文件做任何覆盖。可检查数据目录后点击“重新读取”。";
        StatusText = "本地数据读取失败。";
    }

    /// <summary>
    /// 导出或导入前不应同时进行的工作，避免迁移与正在进行的写入或回调交错；null 表示可以开始。
    /// 迁移开始后界面整体锁定，不会再产生新的用户操作；已开启的巡检计时仍由 Core 的锁与提交后封锁兜底。
    /// </summary>
    private string? MigrationBlockingWork()
    {
        const string Tail = "请等它结束后再开始完整数据迁移，以免迁移与正在写入的数据交错。";
        if (_runs.IsSubmitting)
        {
            return "正在向 BazaarLink 提交检测。" + Tail;
        }

        if (Inspection.IsBusy || IdentityInspection.IsBusy || Probe.Connection.IsBusy)
        {
            return "有连接测试或巡检正在进行。" + Tail;
        }

        if (Refresh.IsRunning || Profiles.IsSaving)
        {
            return "正在拉取模型列表或保存配置。" + Tail;
        }

        if (History.IsBusy || ConnectionHistory.IsBusy || _runs.Records.Any(r => r.IsPolling))
        {
            return "正在读取、删除或导出记录，或有鉴别进度查询在途。" + Tail;
        }

        return null;
    }

    /// <summary>
    /// 导入提交后调用（只调用一次）：停止全部后台工作——连接巡检、身份识别巡检计时与等待、自动连接测试、
    /// 鉴别记录的自动查询与在途请求、问题通知订阅，并取消共享生命周期上的模型拉取等操作。
    /// 不读取、不保存本地数据，也不重新初始化；Core 在提交后另行拒绝后续写入作为兜底。
    /// </summary>
    private void StopForRestart()
    {
        if (_backgroundStopped)
        {
            return;
        }

        _backgroundStopped = true;
        StopQuietly(Inspection.Dispose);
        StopQuietly(IdentityInspection.Dispose);
        StopQuietly(_probeNotifier.Dispose);
        StopQuietly(Probe.Dispose);
        StopQuietly(_runs.Dispose);
        StatusText = "完整数据已导入，等待重新打开程序。后台查询、巡检与连接测试均已停止。";
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(IsInteractive));
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    private static void StopQuietly(Action stop)
    {
        try
        {
            stop();
        }
        catch (Exception)
        {
            // 停止失败不影响锁定状态。
        }
    }

    /// <summary>进入等待重启状态（后台工作已停止）。MainWindow 据此关闭记录窗口。</summary>
    public event EventHandler? RestartRequired;

    /// <summary>关闭窗口前的提示文本；null 表示可以直接关闭。</summary>
    public string? ExitWarning()
    {
        if (_backgroundStopped)
        {
            return null;
        }

        if (_runs.IsSubmitting)
        {
            return "正在向 BazaarLink 提交检测。现在关闭会使本次提交结果变为“未知”，远端可能已开始检测并产生费用，且不会自动重新提交。\n\n确定要关闭吗？";
        }

        if (_runs.AnyAutoPolling)
        {
            return "仍有任务在自动查询进度。关闭程序只会停止本地查询，BazaarLink 上的远端检测不会停止，也不保证停止计费。下次启动后可在“历史记录”中手动恢复查询。\n\n确定要关闭吗？";
        }

        // 直连连接测试：自动测试仍开启，或有一次请求在途。
        if (Probe.Connection.AutoEnabled || Probe.Connection.IsBusy)
        {
            return Probe.Connection.AutoEnabled
                ? "直连自动连接测试仍处于开启状态。退出会停止本机的自动测试计时，之后不再发送；已发出的请求无法撤回，中转站侧可能已经产生费用，也不保证退还。\n\n确定要退出吗？"
                : "有一次直连连接测试请求正在进行。现在退出会使本次结果变为未知，请求可能已经发出并产生费用，且不会自动重试。\n\n确定要退出吗？";
        }

        return null;
    }

    /// <summary>身份识别巡检的“查看报告”：在鉴别记录里定位该条记录并打开记录窗口。只读本地数据，不联网、不重新提交。</summary>
    private async Task OpenProbeReportAsync(Guid probeLocalId)
    {
        try
        {
            if (await History.ShowRecordAsync(probeLocalId))
            {
                OpenWindowRequested?.Invoke("probe");
            }
            else
            {
                _dialogs.Info("查看鉴别报告", "在本地鉴别记录里没有找到这条记录，可能已被删除。");
            }
        }
        catch (Exception ex)
        {
            UiErrors.Report(ex);
        }
    }

    /// <summary>身份识别巡检仍开启或有一轮在进行时的退出提示；null 表示无需提示。</summary>
    public string? IdentityInspectionExitWarning()
    {
        if (_backgroundStopped)
        {
            return null;
        }

        if (IdentityInspection.IsBusy)
        {
            return "有一轮身份识别巡检正在进行。现在退出会取消本轮的本地等待，已提交到 BazaarLink 的任务无法撤回，也不保证停止计费，可能已经产生费用；尚未提交的目标不会再提交，未确认的提交也不会自动重试。";
        }

        return IdentityInspection.Enabled
            ? "定时身份识别巡检仍处于开启状态。退出会停止本机计时，之后不再向 BazaarLink 提交；已提交的任务无法撤回，可能已经产生费用。下次启动会在已授权范围内恢复计时，第一轮在一个完整间隔之后才开始。"
            : null;
    }

    private void OpenDataDirectory()
    {
        var dir = DataDirectory;
        if (!Directory.Exists(dir))
        {
            _dialogs.Info("数据目录", $"数据目录尚未创建：\n{dir}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
        }
        catch (Exception)
        {
            _dialogs.Info("数据目录", $"无法打开资源管理器。数据目录位于：\n{dir}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_backgroundStopped)
        {
            return;
        }

        _backgroundStopped = true;
        Inspection.Dispose();
        IdentityInspection.Dispose();
        _probeNotifier.Dispose();
        Probe.Dispose();
        _runs.Dispose();
    }
}
