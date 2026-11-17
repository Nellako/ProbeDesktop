using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>
/// 设置页的“Windows 问题通知”。两个开关（连接、身份）相互独立，默认开启，只控制是否提示，
/// 不授予任何检测或外发权限。保存是显式操作；测试按钮只发一条本地通知，不联网、不读取任何检测数据。
/// 适配器缺失时开关禁用并说明，测试按钮仍可用；所有成员在 UI 线程使用。
/// </summary>
public sealed class NotificationSettingsViewModel : ObservableObject
{
    private readonly INotificationService? _service;
    private readonly IProblemToastSink? _sink;
    private readonly CancellationTokenSource _lifetime = new();
    private NotificationPreferences _saved = new();
    private bool _connection = true;
    private bool _identity = true;
    private bool _loaded;
    private bool _saving;
    private string _status = "";

    public NotificationSettingsViewModel(INotificationService? service, IProblemToastSink? sink)
    {
        _service = service;
        _sink = sink;
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        TestCommand = new RelayCommand(TestLocal, () => true);
        _status = service is null
            ? "当前服务不支持通知偏好，开关暂不可用。本地测试通知仍可使用。"
            : "正在读取通知偏好…";
    }

    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }

    public bool IsAvailable => _service is not null && _loaded;

    public bool Connection
    {
        get => _connection;
        set => Set(ref _connection, value, nameof(Connection));
    }

    public bool Identity
    {
        get => _identity;
        set => Set(ref _identity, value, nameof(Identity));
    }

    public bool IsDirty => _connection != _saved.Connection || _identity != _saved.Identity;

    public bool CanSave => IsAvailable && IsDirty && !_saving;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string DirtyHint => !IsAvailable ? "" : IsDirty ? "有尚未保存的修改，点“保存偏好”后才会生效。" : "";

    /// <summary>启动时读取一次已保存偏好；失败时保持默认值并说明，不影响任何检测。</summary>
    public async Task LoadAsync()
    {
        if (_service is null || _loaded)
        {
            return;
        }

        try
        {
            var prefs = await _service.GetNotificationPreferencesAsync(_lifetime.Token);
            _saved = prefs;
            _loaded = true;
            _connection = prefs.Connection;
            _identity = prefs.Identity;
            Status = "通知偏好已读取。";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Status = "读取通知偏好失败，开关暂不可用：" + UserMessages.Describe(ex);
            return;
        }

        RaiseAll();
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (!SetProperty(ref field, value, name))
        {
            return;
        }

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DirtyHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task SaveAsync()
    {
        if (_service is null || !CanSave)
        {
            return;
        }

        var next = _saved with { Connection = _connection, Identity = _identity };
        _saving = true;
        OnPropertyChanged(nameof(CanSave));
        try
        {
            _saved = await _service.SaveNotificationPreferencesAsync(next, _lifetime.Token);
            Status = "通知偏好已保存。这些开关只控制是否提示，不改变检测范围或授权。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "保存通知偏好失败，仍保留界面上的修改，可再次保存：" + UserMessages.Describe(ex);
        }
        finally
        {
            _saving = false;
            RaiseAll();
        }
    }

    /// <summary>只发一条本地气泡通知：不联网、不含任何配置或检测数据。显示与否还受 Windows 通知设置影响。</summary>
    private void TestLocal()
    {
        if (_sink is not { IsReady: true })
        {
            Status = "托盘图标不可用，无法发出测试通知。检测与记录不受影响。";
            return;
        }

        var submitted = _sink.Show("测试通知", "这是一条本地测试通知，没有联网，也不包含任何检测数据。", warning: false);
        Status = submitted
            ? "已提交测试通知。如果没有看到，请检查 Windows 的通知与专注助手设置；本程序无法确认系统是否已显示。"
            : "提交测试通知失败。检测与记录不受影响。";
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(Identity));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DirtyHint));
        CommandManager.InvalidateRequerySuggested();
    }
}
