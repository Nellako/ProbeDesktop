using System.IO;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>
/// “完整数据备份与迁移”协调器（帮助与设置页）。与不含 Key 的脱敏报告导出完全分开。
/// 规则：导出与导入都先确认敏感范围；导出密码两次输入，导入单次输入，均不裁剪、不保存；
/// 导入只允许逻辑空库，预览只显示数量；提交成功后进入“等待重启”，停止后台工作并阻止其他命令，
/// 要求用户退出后用同一数据目录重新打开，不在本进程内重新初始化（避免触发自动恢复联网）。全程不联网。
/// </summary>
public sealed class MigrationViewModel : ObservableObject
{
    private readonly IMigrationService? _service;
    private readonly IDialogService _dialogs;
    private readonly IMigrationDialogs _pickers;
    private readonly Func<string> _dataDirectory;
    private readonly Func<bool> _isLoaded;
    private readonly Func<(int Profiles, int ProbeRecords)> _localCounts;
    private readonly Func<string?> _blockingWork;
    private readonly Action _enterRestartRequired;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _canCancel;
    private bool _isRestartRequired;
    private string _busyText = "";
    private string _status = "";

    /// <param name="localCounts">当前内存中的配置数与鉴别记录数，只用于提前提示；是否为空库以 Core 在锁内的检查为准。</param>
    /// <param name="blockingWork">导出或导入前不应进行的工作（提交、巡检、模型拉取、保存等）；返回 null 表示可以开始。</param>
    /// <param name="enterRestartRequired">导入提交后由主 VM 停止全部后台工作并关闭记录窗口。</param>
    public MigrationViewModel(IMigrationService? service, IDialogService dialogs, IMigrationDialogs pickers,
        Func<string> dataDirectory, Func<bool> isLoaded, Func<(int Profiles, int ProbeRecords)> localCounts,
        Func<string?> blockingWork, Action enterRestartRequired)
    {
        _service = service;
        _dialogs = dialogs;
        _pickers = pickers;
        _dataDirectory = dataDirectory;
        _isLoaded = isLoaded;
        _localCounts = localCounts;
        _blockingWork = blockingWork;
        _enterRestartRequired = enterRestartRequired;
        ExportCommand = new AsyncCommand(ExportAsync, CanStart);
        ImportCommand = new AsyncCommand(ImportAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && CanCancel);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(), () => IsRestartRequired);
    }

    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ExitCommand { get; }

    /// <summary>由 MainWindow 设置：等待重启状态下的“退出程序”。</summary>
    public Action? ExitRequested { get; set; }

    public bool IsAvailable => _service is not null;

    /// <summary>迁移进行中（含对话框之间的等待）。期间主窗口与记录窗口的其他操作都被禁用。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsLocked));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>当前阶段能否取消。写入本机阶段不可取消。</summary>
    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (SetProperty(ref _canCancel, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    /// <summary>导入已提交：本进程不再执行任何数据命令或后台工作，必须退出后重新打开。</summary>
    public bool IsRestartRequired
    {
        get => _isRestartRequired;
        private set
        {
            if (SetProperty(ref _isRestartRequired, value))
            {
                OnPropertyChanged(nameof(IsLocked));
                OnPropertyChanged(nameof(RestartText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>忙碌或等待重启：界面其余部分应禁用。</summary>
    public bool IsLocked => IsBusy || IsRestartRequired;

    /// <summary>最近一次操作结果（卡片内显示）。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string RestartText =>
        "完整数据已导入到当前数据目录，需要重新打开程序。\n"
        + "为避免旧的内存状态写回，本次运行已停止所有后台查询、巡检计时与连接测试，其他操作均已禁用。\n"
        + "请点击“退出程序”，然后用同一数据目录重新打开：" + _dataDirectory() + "\n"
        + "重新打开后连接巡检和身份识别巡检均为关闭，外发授权需要重新确认；迁移的未结束鉴别任务须手动确认后才会查询。";

    private bool CanStart() => !IsBusy && !IsRestartRequired && _service is not null && _isLoaded();

    /// <summary>启动时 Core 已处于待重启（例如提交后 UI 通知失败）时直接进入锁定。</summary>
    public void SyncFromService()
    {
        if (_service is { MigrationRestartRequired: true } && !IsRestartRequired)
        {
            EnterRestart();
        }
    }

    // ---- 导出 ----

    private async Task ExportAsync()
    {
        if (_service is null || !CanStart())
        {
            return;
        }

        if (_blockingWork() is { } blocking)
        {
            _dialogs.Info("暂不能导出", blocking);
            return;
        }

        // 提交边界：ExportMigrationAsync 返回后迁移包已正式生成，之后任何界面异常都不能报告为“未生成”。
        var committed = false;
        string? exportedPath = null;
        IsBusy = true;
        BusyText = "正在准备导出完整数据迁移包…";
        CanCancel = false;
        try
        {
            if (!_dialogs.Confirm("导出完整数据迁移包", ExportScopeText))
            {
                Status = "已取消导出，未生成任何文件。";
                return;
            }

            var path = _pickers.PickMigrationSavePath($"ProbeDesktop-migration-{DateTime.Now:yyyyMMdd-HHmm}{MigrationDialogs.FileExtension}");
            if (path is null)
            {
                Status = "已取消导出，未生成任何文件。";
                return;
            }

            if (IsInsideDataDirectory(path))
            {
                _dialogs.Error("不能保存到这里", "迁移包不能放进程序的数据目录。请选择独立的本地文件夹。");
                Status = "未导出：保存位置不可用。";
                return;
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                _dialogs.Error("文件已存在", "所选文件已存在。为避免覆盖，程序只新建迁移包，不会覆盖任何已有文件。请换一个文件名。");
                Status = "未导出：目标文件已存在，没有覆盖。";
                return;
            }

            var password = _pickers.AskExportPassword();
            if (password is null)
            {
                Status = "已取消导出，未生成任何文件。";
                return;
            }

            var token = BeginCancellable("正在加密并写入迁移包（不联网）…");
            await _service.ExportMigrationAsync(path, password, token);
            committed = true;
            exportedPath = path;
            password = null;
            CanCancel = false;
            Status = ExportedStatus(path);
            _dialogs.Info("导出完成",
                "已在本机生成完整数据迁移包：\n" + path + "\n\n"
                + "迁移包含 API Key 与历史敏感信息，任何人拿到迁移包和密码都能读取。请妥善保管，用完后自行删除；程序不会上传或同步它。\n"
                + "本程序只在本机完成了加密写入，尚未在其他电脑上验证导入；请在目标电脑上导入后再确认数据完整。");
        }
        catch (Exception) when (committed)
        {
            // 迁移包已生成，只是之后的界面提示出错：保持“已导出”结论，不改写为失败或取消。
            Status = ExportedStatus(exportedPath!);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消导出，未生成正式的迁移包。";
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + UserMessages.Describe(ex);
            _dialogs.Error("导出失败", UserMessages.Describe(ex) + "\n\n不会覆盖已有文件；未生成可用的迁移包。");
        }
        finally
        {
            EndBusy();
        }
    }

    private static string ExportedStatus(string path) =>
        $"已导出迁移包（{Format.Time(DateTimeOffset.Now)}）：{Path.GetFileName(path)}。尚未在其他电脑验证导入。";

    private const string ExportScopeText =
        "将把本机的完整数据加密打包到一个新文件，供在另一台电脑上导入。\n\n"
        + "包含：全部配置与 API Key、历史密钥（用于脱敏）、模型缓存、鉴别记录与连接记录（含远端任务标识）、巡检选择与间隔、通知偏好。\n\n"
        + "• 迁移包使用你设置的密码加密。任何人同时拿到迁移包和密码，就能读取全部 Key 和历史。\n"
        + "• 密码丢失后迁移包无法恢复，程序不保存、也无法找回密码。\n"
        + "• 迁移包只保存到你选择的本地位置，不会上传或同步；如果你把它放进云同步文件夹，它会被同步出去。\n"
        + "• 这和“导出 JSON / 导出脱敏摘要”不同：那些报告不含 Key，这个迁移包含 Key。\n"
        + "• 导出不联网，也不改变本机数据。\n\n"
        + "确定要导出吗？";

    // ---- 导入 ----

    private async Task ImportAsync()
    {
        if (_service is null || !CanStart())
        {
            return;
        }

        // 导入同样要求没有在途的协调器（模型拉取、保存、记录读写、连接测试等），Core 的空库检查与提交后封锁只作兜底。
        if (_blockingWork() is { } blocking)
        {
            _dialogs.Info("暂不能导入", blocking);
            return;
        }

        var (profiles, records) = _localCounts();
        if (profiles > 0 || records > 0)
        {
            _dialogs.Info("只能导入到空的数据目录",
                $"当前数据目录已有 {profiles} 个配置、{records} 条鉴别记录。完整数据迁移只能导入到没有任何配置和记录的空数据目录，不会覆盖或合并已有数据。\n\n"
                + "如需导入，请在新电脑上首次打开程序后直接导入，或用 --data-dir 指定一个新的空目录。当前目录：" + _dataDirectory());
            return;
        }

        MigrationPreview? preview = null;
        // 提交边界：ImportMigrationAsync 返回（或 Core 已进入待重启）后，数据已写入，之后任何异常（含取消）都不能说“未改变”。
        var committed = false;
        IsBusy = true;
        BusyText = "正在准备导入完整数据迁移包…";
        CanCancel = false;
        try
        {
            if (!_dialogs.Confirm("导入完整数据迁移包", ImportScopeText))
            {
                Status = "已取消导入，本机数据未改变。";
                return;
            }

            var path = _pickers.PickMigrationOpenPath();
            if (path is null)
            {
                Status = "已取消导入，本机数据未改变。";
                return;
            }

            var password = _pickers.AskImportPassword();
            if (password is null)
            {
                Status = "已取消导入，本机数据未改变。";
                return;
            }

            var token = BeginCancellable("正在验证密码并解密迁移包（不联网，尚未写入本机）…");
            preview = await _service.PreviewMigrationAsync(path, password, token);

            password = null;
            CanCancel = false;
            BusyText = "等待确认导入…";
            if (!_dialogs.Confirm("确认导入", PreviewText(preview)))
            {
                Status = "已取消导入，本机数据未改变。";
                return;
            }

            // 写入阶段不可取消：Core 以一次原子提交完成，提交后不能当作未导入再次覆盖。
            BusyText = "正在写入本机数据目录，请勿关闭程序…";
            try
            {
                await _service.ImportMigrationAsync(preview, CancellationToken.None);
                committed = true;
            }
            catch (Exception) when (_service.MigrationRestartRequired)
            {
                // 提交边界已越过：数据已写入，不能报告为未导入。
                committed = true;
            }

            EnterRestart();
            _dialogs.Info("导入完成",
                "完整数据已导入当前数据目录，API Key 已用本机 Windows 账户重新加密保存。\n\n"
                + "需要退出程序并用同一数据目录重新打开。重新打开后连接巡检和身份识别巡检均为关闭，外发授权需要重新确认；迁移的未结束鉴别任务须手动确认后才会查询，不会重新提交。");
        }
        catch (Exception) when (committed || _service.MigrationRestartRequired)
        {
            // 已提交后的任何异常（包括 OperationCanceledException、提示框异常）都保持“已导入、等待重启”。
            try { EnterRestart(); } catch (Exception) { }
            Status = "已导入，等待重新打开程序。";
        }
        catch (OperationCanceledException)
        {
            Status = "已取消导入，本机数据未改变。";
        }
        catch (Exception ex)
        {
            Status = "导入失败：" + UserMessages.Describe(ex);
            _dialogs.Error("导入失败", UserMessages.Describe(ex) + "\n\n本机数据未改变。");
        }
        finally
        {
            preview?.Dispose();
            EndBusy();
        }
    }

    private const string ImportScopeText =
        "将从迁移包导入完整数据到当前数据目录。\n\n"
        + "• 只能导入到空的数据目录（没有任何配置、密钥或记录），不会覆盖或合并已有数据；不满足时会拒绝导入。\n"
        + "• 迁移包中的 API Key、历史密钥与记录会写入本机，并用当前 Windows 账户的 DPAPI 重新加密。\n"
        + "• 导入后连接巡检和身份识别巡检均为关闭，外发授权需要重新确认。\n"
        + "• 迁移的未结束鉴别任务不会自动查询，须手动确认后才读取进度，不会重新提交。\n"
        + "• 导入成功后必须退出并重新打开程序。\n"
        + "• 导入不联网。下一步选择迁移包并输入密码，先预览数量，再确认写入。\n\n"
        + "确定要继续吗？";

    private static string PreviewText(MigrationPreview p) =>
        "密码正确，迁移包已通过校验。将导入：\n\n"
        + $"• 配置：{p.ProfileCount} 个\n"
        + $"• 鉴别记录：{p.ProbeRecordCount} 条\n"
        + $"• 连接记录：{p.ConnectionRecordCount} 条\n"
        + $"• 导出时间：{Format.Time(p.ExportedAt)}\n\n"
        + (p.HasUnfinishedWork
            ? "其中有未结束或结果未知的任务或巡检轮次：旧轮次的剩余目标不会再发送；未结束的鉴别任务不会自动查询，须手动确认后才读取进度，不会重新提交，在结束前仍会阻止重复提交。\n\n"
            : "")
        + "导入后连接巡检和身份识别巡检均为关闭，外发授权需要重新确认；完成后必须重新打开程序。\n"
        + $"本次预览在 {Format.Time(p.ExpiresAt)} 前有效。\n\n"
        + "确定写入当前数据目录吗？";

    // ---- 公共 ----

    private CancellationToken BeginCancellable(string text)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BusyText = text;
        CanCancel = true;
        return _cts.Token;
    }

    private void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        BusyText = "正在取消…";
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>MainWindow 关闭时：忙碌中可取消的阶段直接取消；返回 true 表示可以继续关闭。</summary>
    public bool TryCancelForClose()
    {
        if (!IsBusy)
        {
            return true;
        }

        if (CanCancel)
        {
            Cancel();
        }

        return false;
    }

    private void EndBusy()
    {
        CanCancel = false;
        _cts?.Dispose();
        _cts = null;
        BusyText = "";
        IsBusy = false;
    }

    private void EnterRestart()
    {
        if (IsRestartRequired)
        {
            return;
        }

        IsRestartRequired = true;
        Status = "已导入，等待重新打开程序。";
        try
        {
            _enterRestartRequired();
        }
        catch (Exception)
        {
            // 停止后台工作失败也保持锁定；Core 在提交后另行拒绝后续写入。
        }
    }

    private bool IsInsideDataDirectory(string path)
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory()));
            var full = Path.GetFullPath(path);
            return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
