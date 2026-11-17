using System.Globalization;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>连接测试中的一条独立状态（本地检查 / API 响应 / 模型响应 / 标记读取）。Tone：none / ok / possible / warn / bad。</summary>
public sealed class StageLine : ObservableObject
{
    private string _state = "";
    private string _detail = "";
    private string _tone = "none";

    public StageLine(string label) => Label = label;

    public string Label { get; }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string Tone
    {
        get => _tone;
        private set => SetProperty(ref _tone, value);
    }

    public void Set(string state, string tone, string detail)
    {
        State = state;
        Tone = tone;
        Detail = detail;
    }
}

/// <summary>
/// 本地直连连接测试：只面向“当前选中的一个目标”（配置 + 模型），直接请求中转站，不经过 BazaarLink。
/// 规则：同一时间最多一个手动/自动请求；目标（配置、模型、Base URL、Key 版本）变化时取消并关闭自动测试；
/// 自动测试仅本会话有效、默认关闭、从不在启动时自动开启；到点只发一次，不补发错过的次数、不重试。
/// 所有成员在 UI 线程使用。
/// </summary>
public sealed class ConnectionTestViewModel : ObservableObject, IDisposable
{
    public const int DefaultIntervalMinutes = 60;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    private sealed record Target(Guid ProfileId, string Name, string BaseUrl, ApiProtocol Protocol, int KeyRevision, string ModelId);

    private readonly IDirectConnectionService _service;
    private readonly IDialogService _dialogs;
    private readonly CancellationToken _lifetime;
    private readonly TimeProvider _time;
    private readonly RecordFeed? _feed;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly StageLine _local = new("本地检查");
    private readonly StageLine _api = new("API 响应");
    private readonly StageLine _model = new("模型响应");
    private readonly StageLine _context = new("标记读取");

    private Target? _target;
    private CancellationTokenSource _targetCts;
    private CancellationTokenSource? _autoCts;
    private int _targetGen;
    private int _profileGen;
    private bool _inFlight;
    private bool _flightIsAuto;
    private bool _disposed;
    private bool _autoEnabled;
    private bool _isLoading;
    private int _autoRuns;
    private DateTimeOffset? _nextRunAt;
    private ConnectionTestResult? _lastResult;
    private ConnectionValidationResult? _localCheck;
    private string _intervalText = DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string _intervalError = "";
    private string _actionNote = "";
    private string _autoNote = "";
    private bool _suppressIntervalEffects;

    public ConnectionTestViewModel(IDirectConnectionService service, IDialogService dialogs, CancellationToken lifetime, TimeProvider? time = null, RecordFeed? feed = null)
    {
        _feed = feed;
        _service = service;
        _dialogs = dialogs;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _targetCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Stages = [_local, _api, _model, _context];
        CheckCommand = new AsyncCommand(CheckAsync, () => CanOperate);
        TestCommand = new AsyncCommand(TestAsync, () => CanOperate);
        ToggleAutoCommand = new AsyncCommand(ToggleAutoAsync, () => AutoEnabled || (HasTarget && HasModel && !IsLoading));
        RefreshStages();
    }

    public IReadOnlyList<StageLine> Stages { get; }
    public ICommand CheckCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ToggleAutoCommand { get; }

    public bool HasTarget => _target is not null;
    public bool HasModel => _target is { ModelId.Length: > 0 };
    public bool CanOperate => HasTarget && HasModel && !IsBusy;
    public bool IsBusy => _inFlight;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string BusyText => !_inFlight
        ? ""
        : _flightIsAuto ? "自动连接测试请求进行中，完成前不能发起其他连接测试。" : "连接测试请求进行中（最长约 45 秒），完成前不能再次发起。";

    public string TargetText => _target is { } t
        ? $"测试目标：配置“{t.Name}”（{ProtocolInfo.Display(t.Protocol)}） / 模型 {(t.ModelId.Length > 0 ? t.ModelId : "（尚未选择或填写）")}"
        : "请先在上方选择配置，并选择或手填模型。";

    public string RecipientText
    {
        get
        {
            if (_target is not { } t)
            {
                return "";
            }

            try
            {
                return "接收方（仅中转站，不经过 BazaarLink）：POST " + ConnectionTestEndpoints.Recipient(t.BaseUrl, t.Protocol);
            }
            catch (Exception)
            {
                return "该配置的 Base URL 无法生成连接测试地址，请先到“配置管理”检查。";
            }
        }
    }

    /// <summary>卡片内的固定说明，随所选协议变化（请求路径、认证方式、输入规模与输出上限参数）。</summary>
    public string ExplainText => _target is { } t
        ? $"确认模型可用必须实际发送一次请求：“测试连接”直接向中转站发送{ProtocolInfo.ConnectionInputNote}（{ProtocolInfo.Display(t.Protocol)}，{ProtocolInfo.TokenLimitNote(t.Protocol)}，超时约 45 秒），携带该配置的 API Key（{ProtocolInfo.AuthHeaderText(t.Protocol)}），不经过 BazaarLink。连接状态与标记读取分别展示；标记校验仅检查本次读回标记并按格式回答，不证明模型真实身份或能力，标记不匹配也不等于断连或降智。可能产生费用，实际计费含输入、可能的隐藏推理和最低计费，以中转站为准。"
        : "确认模型可用必须实际发送一次请求。选择配置后，这里会按所选协议说明请求路径、认证方式、约256 token合成输入（含三个随机标记，非256k压力测试）和输出上限参数（64 token）。测试直接发给你的中转站，不经过 BazaarLink，可能产生费用。";

    public string ResultMeta { get; private set; } = "";
    public string HiddenResultNote { get; private set; } = "";

    public string ActionNote
    {
        get => _actionNote;
        private set => SetProperty(ref _actionNote, value);
    }

    public bool AutoEnabled
    {
        get => _autoEnabled;
        private set
        {
            if (SetProperty(ref _autoEnabled, value))
            {
                OnPropertyChanged(nameof(AutoToggleText));
                OnPropertyChanged(nameof(AutoStateText));
                OnPropertyChanged(nameof(IntervalEditable));
                OnPropertyChanged(nameof(NextRunText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IntervalEditable => !AutoEnabled;
    public string AutoToggleText => AutoEnabled ? "关闭自动测试" : "开启自动测试…";
    public string AutoStateText => AutoEnabled ? "自动测试：已开启（仅本次运行）" : "自动测试：关闭";

    public string AutoNote
    {
        get => _autoNote;
        private set => SetProperty(ref _autoNote, value);
    }

    public string NextRunText => AutoEnabled && _nextRunAt is { } at
        ? $"下次计划：{Format.Time(at)}（仅在本程序运行期间；睡眠恢复后若已到点会进行一次，不补发错过的次数）"
        : "";

    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (!SetProperty(ref _intervalText, value ?? ""))
            {
                return;
            }

            var valid = TryParseInterval(out var minutes);
            IntervalError = valid ? "" : $"间隔需为 {MinIntervalMinutes}–{MaxIntervalMinutes} 的整数分钟。";
            if (_suppressIntervalEffects)
            {
                return;
            }

            if (AutoEnabled)
            {
                StopAuto(_target, "间隔已修改，自动测试已关闭。请确认新间隔后重新开启（需要再次授权）。", persist: true);
            }
        }
    }

    /// <summary>间隔输入框失焦时调用：有效且自动测试关闭时保存间隔数值（Enabled 恒为 false，不会自动开启）。</summary>
    public void CommitInterval()
    {
        if (!AutoEnabled && _target is { } t && TryParseInterval(out var minutes))
        {
            _ = SaveOffAsync(t, minutes);
        }
    }

    public string IntervalError
    {
        get => _intervalError;
        private set => SetProperty(ref _intervalError, value);
    }

    // ───────────────────────── 目标同步 ─────────────────────────

    /// <summary>由检测页在配置或模型变化时调用。目标有实质变化（配置/模型/Base URL/Key 版本）时取消在途请求并关闭自动测试。</summary>
    public void SetTarget(ProfileItem? profile, string modelId)
    {
        if (_disposed)
        {
            return;
        }

        var next = profile is null
            ? null
            : new Target(profile.Id, profile.Name, profile.BaseUrl, profile.Protocol, profile.KeyRevision, modelId.Trim());
        var old = _target;
        if (Same(old, next))
        {
            if (old is not null && next is not null && old.Name != next.Name)
            {
                _target = old with { Name = next.Name };
                OnPropertyChanged(nameof(TargetText));
            }

            return;
        }

        if (AutoEnabled)
        {
            StopAuto(old, "测试目标已变化，自动测试已关闭；不会自动对新目标开启。", persist: true);
        }

        // 取消在途请求并换一个新的目标令牌；旧请求的结果将被丢弃。
        var oldCts = _targetCts;
        _targetGen++;
        _targetCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
        oldCts.Dispose();

        var profileChanged = old?.ProfileId != next?.ProfileId;
        _target = next;
        _localCheck = null;
        ActionNote = "";
        if (profileChanged)
        {
            _lastResult = null;
            _profileGen++;
            IsLoading = false;
            AutoNote = "";
            SetIntervalSilently(DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture));
            if (next is not null)
            {
                _ = LoadProfileDataAsync(_profileGen, next.ProfileId);
            }
        }

        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(RecipientText));
        OnPropertyChanged(nameof(ExplainText));
        RefreshStages();
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool Same(Target? a, Target? b)
        => a is null || b is null
            ? a is null && b is null
            : a.ProfileId == b.ProfileId && a.Protocol == b.Protocol && a.KeyRevision == b.KeyRevision && a.ModelId == b.ModelId
              && string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.Ordinal);

    private async Task LoadProfileDataAsync(int profileGen, Guid profileId)
    {
        IsLoading = true;
        try
        {
            var settings = await _service.GetConnectionSettingsAsync(profileId, _lifetime);
            var last = await _service.GetLastConnectionResultAsync(profileId, _lifetime);
            if (_disposed || profileGen != _profileGen)
            {
                return;
            }

            var minutes = settings.IntervalMinutes is >= MinIntervalMinutes and <= MaxIntervalMinutes
                ? settings.IntervalMinutes
                : DefaultIntervalMinutes;
            if (!AutoEnabled)
            {
                SetIntervalSilently(minutes.ToString(CultureInfo.InvariantCulture));
            }

            _lastResult ??= last;
            RefreshStages();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!_disposed && profileGen == _profileGen)
            {
                ActionNote = "读取该配置已保存的连接测试结果失败；仍可手动测试。";
            }
        }
        finally
        {
            if (profileGen == _profileGen)
            {
                IsLoading = false;
            }
        }
    }

    private void SetIntervalSilently(string text)
    {
        _suppressIntervalEffects = true;
        try { IntervalText = text; }
        finally { _suppressIntervalEffects = false; }
    }

    private bool TryParseInterval(out int minutes)
        => int.TryParse(IntervalText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
           && minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes;

    // ───────────────────────── 飞行位（同一时间最多一个请求） ─────────────────────────

    private bool BeginFlight(bool auto)
    {
        if (_inFlight)
        {
            return false;
        }

        _inFlight = true;
        _flightIsAuto = auto;
        FlightChanged();
        return true;
    }

    private void EndFlight()
    {
        _inFlight = false;
        FlightChanged();
    }

    private void FlightChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyText));
        OnPropertyChanged(nameof(CanOperate));
        CommandManager.InvalidateRequerySuggested();
    }

    // ───────────────────────── 本地检查 ─────────────────────────

    private async Task CheckAsync()
    {
        if (_target is not { } t || !CanOperate)
        {
            return;
        }

        var gen = _targetGen;
        try
        {
            var result = await _service.ValidateConnectionAsync(t.ProfileId, t.ModelId, _targetCts.Token);
            if (gen != _targetGen || _disposed)
            {
                return;
            }

            _localCheck = result;
            ActionNote = result.Passed
                ? "本地检查通过：未联网，不能说明 API 可达或模型可用。要确认模型可用，必须点“测试连接”。"
                : "本地检查未通过：未联网。";
            RefreshStages();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ClientOperationException ex)
        {
            if (gen == _targetGen && !_disposed)
            {
                _localCheck = new ConnectionValidationResult(false, ex.Code, ex.Message);
                RefreshStages();
            }
        }
    }

    // ───────────────────────── 手动测试连接 ─────────────────────────

    private async Task TestAsync()
    {
        if (_target is not { } t || !CanOperate)
        {
            return;
        }

        var gen = _targetGen;
        ConnectionValidationResult local;
        try
        {
            local = await _service.ValidateConnectionAsync(t.ProfileId, t.ModelId, _targetCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ClientOperationException ex)
        {
            local = new ConnectionValidationResult(false, ex.Code, ex.Message);
        }

        if (gen != _targetGen || _disposed)
        {
            return;
        }

        _localCheck = local;
        RefreshStages();
        if (!local.Passed)
        {
            ActionNote = "本地检查未通过，未发送任何请求。";
            return;
        }

        string recipient;
        try
        {
            recipient = ConnectionTestEndpoints.Recipient(t.BaseUrl, t.Protocol);
        }
        catch (Exception)
        {
            ActionNote = "Base URL 无法生成连接测试地址，未发送任何请求。";
            return;
        }

        var message =
            "即将由本程序直接向中转站发送一次模型连接测试请求：\n\n" +
            $"POST {recipient}\n\n" +
            $"配置：{t.Name}\n" +
            $"协议：{ProtocolInfo.Display(t.Protocol)}\n" +
            $"模型：{t.ModelId}\n" +
            $"请求头会携带该配置保存的 API Key（{ProtocolInfo.AuthHeaderText(t.Protocol)}，Key 版本 {t.KeyRevision}，此处不显示）。\n\n" +
            $"• 内容为{ProtocolInfo.ConnectionInputNote}，{ProtocolInfo.TokenLimitNote(t.Protocol)}，超时约 45 秒；实际计费包含输入、可能的隐藏推理和中转站的最低计费，以中转站为准。这是真实模型调用，可能产生费用。\n" +
            "• 标记读取单独校验，仅检查本次按格式返回三个标记，不证明模型真实身份或能力；不匹配不等于断连或降智，也不因此重试。\n" +
            "• 只发送给上述中转站，不经过 BazaarLink 或其他第三方。\n" +
            "• 不自动补 /v1，不跟随重定向，不自动重试，不改用其他协议。\n" +
            "• 连接结果只说明本次是否收到兼容的模型响应。\n\n" +
            "选“否”不会联网，仍可使用其他功能。确认发送吗？";
        if (!_dialogs.Confirm("确认向中转站发送连接测试", message))
        {
            if (gen == _targetGen)
            {
                ActionNote = "已取消，未发送任何请求。";
            }

            return;
        }

        if (gen != _targetGen || _disposed)
        {
            return;
        }

        if (!BeginFlight(auto: false))
        {
            ActionNote = "已有连接测试请求在进行，未重复发送。";
            return;
        }

        var consent = new OutboundConsent(
            t.ProfileId, t.KeyRevision, t.BaseUrl, OutboundPurpose.ConnectionTest, recipient, t.ModelId, DateTimeOffset.UtcNow,
            Protocol: t.Protocol);
        ActionNote = "正在向中转站发送连接测试…请勿重复点击。";
        try
        {
            var result = await _service.TestConnectionAsync(t.ProfileId, t.ModelId, consent, _targetCts.Token);
            if (gen == _targetGen && !_disposed)
            {
                ApplyResult(result);
                ActionNote = DescribeOutcome(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && gen != _targetGen)
            {
                // 目标已切换：旧请求的结果不再展示在新目标上。
            }
            else if (!_disposed)
            {
                ActionNote = "连接测试已取消（未确认模型响应）；若请求已发出，不能断定没有产生费用。";
            }
        }
        catch (ClientOperationException ex)
        {
            if (gen == _targetGen && !_disposed)
            {
                ActionNote = "未发送或未完成：" + UserMessages.FormatClient(ex);
            }
        }
        catch (Exception ex)
        {
            if (gen == _targetGen && !_disposed)
            {
                ActionNote = "连接测试失败：" + UserMessages.Describe(ex) + " 若请求已发出，不能断定没有产生费用。";
            }
        }
        finally
        {
            EndFlight();
            // Core 在请求结束（成功、失败、取消）时都会写入完成记录：通知模型页等订阅者重新读取。
            _feed?.NotifyConnection();
        }
    }

    private static string DescribeOutcome(ConnectionTestResult r) => r.Stage switch
    {
        ConnectionTestStage.ModelResponded when r.ContextCheck == ConnectionContextCheck.Mismatch
            => "连接成功（收到有效模型响应），但标记读取不匹配：未按要求返回三个标记；不等于断连或降智，详见下方状态。",
        ConnectionTestStage.ModelResponded
            => $"连接成功（收到有效模型响应）；标记读取：{ProtocolInfo.ContextLabel(r.ContextCheck)}。{ProtocolInfo.ContextNote(r.ContextCheck)}",
        ConnectionTestStage.PossibleSuccess => "成功（可能）：中转站返回了成功响应，但没有确认到有效的模型消息。不能当作确认成功，也不能当作失败；不会自动重试。",
        ConnectionTestStage.Unknown => "失败：未收到完整有效响应（如超时、连接中断或响应不兼容）；不能断定没有产生费用，也不会自动重试，详见下方状态。",
        ConnectionTestStage.Failed => "失败：未确认模型可用，详见下方状态。",
        ConnectionTestStage.LocalValidationPassed => "仅本地检查通过，未发送请求。",
        ConnectionTestStage.ApiReached => "API 可达，但未确认模型响应，详见下方状态。",
        _ => "收到未识别的结果阶段，未当作成功，详见下方状态。",
    };

    // ───────────────────────── 自动测试 ─────────────────────────

    private Task ToggleAutoAsync()
    {
        if (AutoEnabled)
        {
            StopAuto(_target, "自动测试已关闭。已发出的请求无法撤回，可能已产生费用；仍可手动测试。", persist: true);
            return Task.CompletedTask;
        }

        return EnableAutoAsync();
    }

    private async Task EnableAutoAsync()
    {
        if (_target is not { ModelId.Length: > 0 } t)
        {
            return;
        }

        if (!TryParseInterval(out var minutes))
        {
            IntervalError = $"间隔需为 {MinIntervalMinutes}–{MaxIntervalMinutes} 的整数分钟。";
            AutoNote = "间隔无效，未开启。";
            return;
        }

        var gen = _targetGen;
        ConnectionValidationResult local;
        try
        {
            local = await _service.ValidateConnectionAsync(t.ProfileId, t.ModelId, _targetCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ClientOperationException ex)
        {
            local = new ConnectionValidationResult(false, ex.Code, ex.Message);
        }

        if (gen != _targetGen || _disposed)
        {
            return;
        }

        _localCheck = local;
        RefreshStages();
        if (!local.Passed)
        {
            AutoNote = "本地检查未通过，自动测试未开启。";
            return;
        }

        string recipient;
        try
        {
            recipient = ConnectionTestEndpoints.Recipient(t.BaseUrl, t.Protocol);
        }
        catch (Exception)
        {
            AutoNote = "Base URL 无法生成连接测试地址，自动测试未开启。";
            return;
        }

        var message =
            "即将开启自动连接测试。开启后，本程序运行期间会按设定间隔反复直接调用中转站的模型：\n\n" +
            $"POST {recipient}\n\n" +
            $"配置：{t.Name}\n" +
            $"协议：{ProtocolInfo.Display(t.Protocol)}\n" +
            $"模型：{t.ModelId}\n" +
            $"间隔：每 {minutes} 分钟一次（首次在开启满 {minutes} 分钟后，不会立即发送）\n\n" +
            $"• 每次发送{ProtocolInfo.ConnectionInputNote}（{ProtocolInfo.TokenLimitNote(t.Protocol)}；实际计费含输入、可能的隐藏推理和最低计费，以中转站为准），会反复计费，累计费用可能较高。标记不匹配不等于断连，也不因此重试。\n" +
            "• 只发送给上述中转站，不经过 BazaarLink；携带该配置保存的 API Key（Key 版本 " + t.KeyRevision + "）。\n" +
            "• 授权只在本次程序运行期间有效，只针对当前这一个配置与模型；切换配置、模型、Key 或 Base URL 会自动关闭，重启程序后默认关闭。\n" +
            "• 切换页面标签不会停止；关闭程序后不再发送。睡眠或休眠恢复后，若已到点会正常进行最多一次，不会补发错过的累计次数。\n" +
            "• 任一次非确认成功（失败、超时、连接中断或成功（可能））都会暂停并关闭自动测试，不会自动重试。\n" +
            "• 可随时关闭；关闭不会撤回已发出的请求，也不保证退还费用。手动测试不受影响。\n\n" +
            "确认开启并授权重复调用吗？选“否”不会开启，也不会联网。";
        if (!_dialogs.Confirm("确认开启自动连接测试", message))
        {
            if (gen == _targetGen)
            {
                AutoNote = "已取消，自动测试保持关闭，未保存任何授权。";
            }

            return;
        }

        if (gen != _targetGen || _disposed)
        {
            return;
        }

        var consent = new ConnectionScheduleConsent(
            new OutboundConsent(t.ProfileId, t.KeyRevision, t.BaseUrl, OutboundPurpose.ConnectionTest, recipient, t.ModelId, DateTimeOffset.UtcNow, Protocol: t.Protocol),
            minutes);
        var settings = new ConnectionTestSettings
        {
            ProfileId = t.ProfileId,
            Protocol = t.Protocol,
            ModelId = t.ModelId,
            IntervalMinutes = minutes,
            Enabled = true,
            KeyRevision = t.KeyRevision,
            BaseUrl = t.BaseUrl,
        };

        ConnectionTestSettings saved;
        await _saveLock.WaitAsync(_lifetime);
        try
        {
            saved = await _service.SaveConnectionSettingsAsync(settings, consent, _lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ClientOperationException ex)
        {
            if (gen == _targetGen && !_disposed)
            {
                AutoNote = "开启失败，自动测试保持关闭：" + UserMessages.FormatClient(ex);
            }

            return;
        }
        finally
        {
            _saveLock.Release();
        }

        if (_disposed || gen != _targetGen)
        {
            // 保存期间目标已变化：立刻撤销刚授予的本会话授权。
            if (saved.Enabled)
            {
                _ = SaveOffAsync(t, minutes);
            }

            return;
        }

        if (!saved.Enabled)
        {
            AutoNote = "自动测试未能开启（服务未授予会话授权）。";
            return;
        }

        _autoRuns = 0;
        _autoCts = CancellationTokenSource.CreateLinkedTokenSource(_targetCts.Token);
        AutoEnabled = true;
        AutoNote = $"已开启：每 {minutes} 分钟一次，首次在满 {minutes} 分钟后；仅本次运行有效。";
        _ = RunAutoLoopAsync(gen, t, TimeSpan.FromMinutes(minutes), _autoCts.Token);
    }

    private async Task RunAutoLoopAsync(int gen, Target t, TimeSpan interval, CancellationToken ct)
    {
        var wait = interval;
        try
        {
            while (true)
            {
                _nextRunAt = _time.GetLocalNow() + wait;
                OnPropertyChanged(nameof(NextRunText));
                await Task.Delay(wait, _time, ct);
                if (_disposed || gen != _targetGen)
                {
                    return;
                }

                _nextRunAt = null;
                OnPropertyChanged(nameof(NextRunText));
                wait = interval;
                if (!BeginFlight(auto: true))
                {
                    AutoNote = $"{Format.Time(_time.GetLocalNow())} 到点时已有连接请求在进行，本轮已跳过，不会补发。";
                    continue;
                }

                ConnectionTestResult? result = null;
                ClientOperationException? error = null;
                try
                {
                    result = await _service.TestScheduledConnectionAsync(t.ProfileId, ct);
                }
                catch (ClientOperationException ex)
                {
                    error = ex;
                }
                finally
                {
                    EndFlight();
                }

                if (_disposed || gen != _targetGen)
                {
                    return;
                }

                if (error is { Code: "ConnectionNotDue" })
                {
                    var remaining = error.RetryAfter ?? interval;
                    wait = remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining > interval ? interval : remaining;
                    AutoNote = "尚未到下一次自动测试时间（可能刚做过手动测试），顺延，不提前发送。";
                    continue;
                }

                if (error is not null)
                {
                    StopAuto(t, "自动测试已暂停并关闭：" + UserMessages.FormatClient(error) + "\n不会自动重试。请用“测试连接”手动检查。", persist: true);
                    return;
                }

                var done = result!;
                ApplyResult(done);
                _autoRuns++;
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // 只有确认的“成功”才继续下一次；成功（可能）、失败（含底层 Unknown）、其他阶段一律暂停，不自动重试。标记不匹配不视为断连。
                if (LatestResultPicker.Classify(done) == LatestResultPicker.ConnectionOutcome.Success)
                {
                    AutoNote = $"{Format.Time(done.CompletedAt ?? done.StartedAt)} 第 {_autoRuns} 次自动测试：收到模型响应（标记读取：{ProtocolInfo.ContextLabel(done.ContextCheck)}）。";
                    continue;
                }

                StopAuto(t, $"自动测试已暂停并关闭：本次{DescribeOutcome(done).TrimEnd('。')}（{UserMessages.Redact(done.Code)}）。不会自动重试，也不能断定没有产生费用。请用“测试连接”手动检查。", persist: true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && gen == _targetGen)
            {
                StopAuto(t, "自动测试已暂停并关闭：" + UserMessages.Describe(ex) + " 请用“测试连接”手动检查。", persist: true);
            }
        }
    }

    private void StopAuto(Target? target, string note, bool persist)
    {
        if (!AutoEnabled && _autoCts is null)
        {
            return;
        }

        var cts = _autoCts;
        _autoCts = null;
        _nextRunAt = null;
        AutoEnabled = false;
        AutoNote = note;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }

        if (persist && target is not null && !_disposed)
        {
            _ = SaveOffAsync(target, TryParseInterval(out var minutes) ? minutes : DefaultIntervalMinutes);
        }
    }

    /// <summary>保存 Enabled=false（同时撤销 Core 的会话授权）与间隔数值。串行执行，失败不影响本地已停止的事实。</summary>
    private async Task SaveOffAsync(Target t, int minutes)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _saveLock.WaitAsync(_lifetime);
            try
            {
                await _service.SaveConnectionSettingsAsync(new ConnectionTestSettings
                {
                    ProfileId = t.ProfileId,
                    Protocol = t.Protocol,
                    ModelId = t.ModelId,
                    IntervalMinutes = minutes,
                    Enabled = false,
                    KeyRevision = t.KeyRevision,
                    BaseUrl = t.BaseUrl,
                }, null, _lifetime);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 本地没有定时器在运行，不会再发送；保存失败时仅提示。
            if (!_disposed)
            {
                ActionNote = "未能保存“自动测试已关闭”的设置，但本程序已停止定时，不会再发送自动请求。";
            }
        }
    }

    // ───────────────────────── 展示 ─────────────────────────

    private void ApplyResult(ConnectionTestResult result)
    {
        if (_target is { } t && result.ProfileId == t.ProfileId)
        {
            _lastResult = result;
        }

        RefreshStages();
    }

    private bool Matches(ConnectionTestResult? r)
        => r is not null && _target is { } t
           && r.ProfileId == t.ProfileId && r.Protocol == t.Protocol && r.KeyRevision == t.KeyRevision && r.ModelId == t.ModelId
           && string.Equals(r.BaseUrl.TrimEnd('/'), t.BaseUrl.TrimEnd('/'), StringComparison.Ordinal);

    private void RefreshStages()
    {
        var r = Matches(_lastResult) ? _lastResult : null;
        if (_target is null)
        {
            _context.Set("—", "none", "");
        }
        else if (r is null)
        {
            _context.Set("未测试", "none", "完成连接测试后单独检查首、中、尾三个标记的按格式返回情况，不据此判断降智。");
        }
        else
        {
            _context.Set(ProtocolInfo.ContextLabel(r.ContextCheck), ProtocolInfo.ContextTone(r.ContextCheck), ProtocolInfo.ContextNote(r.ContextCheck));
        }

        HiddenResultNote = _target is not null && _lastResult is not null && r is null
            ? "该配置保存的最近一次单目标连接结果针对的是其他模型、协议、Base URL 或 Key 版本，此处已隐藏以免混淆；当前模型最近一次结果可看上方“该模型最近结果”或“连接记录”窗口。"
            : "";

        // 本地检查
        if (_target is null)
        {
            _local.Set("—", "none", "");
        }
        else if (_localCheck is { } lc)
        {
            _local.Set(lc.Passed ? "通过" : "未通过", lc.Passed ? "ok" : "bad", UserMessages.Redact(lc.Message));
        }
        else if (r is { LocalValidationPassed: true })
        {
            _local.Set("通过", "ok", "发送前的本地检查已通过。");
        }
        else
        {
            _local.Set("未检查", "none", "不联网，只检查 Base URL、已保存的 Key 与模型 ID 的本地格式。");
        }

        // API 响应 / 模型响应
        if (_target is null || r is null)
        {
            var text = _target is null ? "—" : "未测试";
            _api.Set(text, "none", _target is null ? "" : "尚未对当前目标发送连接测试。");
            _model.Set(text, "none", _target is null ? "" : "只有点“测试连接”并收到有效的模型消息，才能确认模型可用。");
            ResultMeta = "";
        }
        else
        {
            var message = UserMessages.Redact(r.Message);
            if (r.ApiReached)
            {
                var ok = r.HttpStatusCode is >= 200 and < 300;
                var code = r.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
                _api.Set(ok ? $"已到达（HTTP {code}）" : $"已响应（HTTP {code}，未成功）", ok ? "ok" : "warn",
                    ok ? "收到中转站的 HTTP 响应，只说明服务可达，不等于模型可用。" : "中转站有响应但未接受本次请求：" + message);
            }
            else if (r.Stage == ConnectionTestStage.Unknown)
            {
                _api.Set("失败（未收到完整响应）", "bad", "请求可能已发出但没有完整响应（超时、网络中断或程序中断）；不能断定没有产生费用。");
            }
            else if (r.Stage == ConnectionTestStage.Failed)
            {
                _api.Set("未到达", "bad", message);
            }
            else
            {
                _api.Set("未测试", "none", "");
            }

            var outcome = LatestResultPicker.Classify(r);
            if (outcome == LatestResultPicker.ConnectionOutcome.PossibleSuccess)
            {
                _model.Set("成功（可能）", "possible",
                    "没有确认到有效的模型消息，不能当作确认成功，也不能当作失败；不验证模型真实身份或能力。" + (message.Length > 0 ? " " + message : ""));
            }
            else if (outcome == LatestResultPicker.ConnectionOutcome.Success)
            {
                _model.Set("模型响应成功", "ok", message);
            }
            else if (r.Stage == ConnectionTestStage.Unknown)
            {
                var detail = message.Contains("产生费用", StringComparison.Ordinal)
                    ? message
                    : (message.Length > 0 ? message + " " : "") + "不能断定本次没有产生费用。";
                _model.Set("失败", "bad", detail);
            }
            else if (r.Stage == ConnectionTestStage.Failed)
            {
                _model.Set("失败", "bad", message);
            }
            else
            {
                _model.Set("未测试", "none", "");
            }

            var meta = $"结果时间：{Format.Time(r.CompletedAt ?? r.StartedAt)}　协议：{ProtocolInfo.Display(r.Protocol)}　模型：{r.ModelId}　代码：{UserMessages.Redact(r.Code)}";
            if (r.RetryNotBefore is { } retry && retry > DateTimeOffset.UtcNow)
            {
                meta += $"　服务要求 {Format.Time(retry)} 前不再发送";
            }

            ResultMeta = meta;
        }

        OnPropertyChanged(nameof(ResultMeta));
        OnPropertyChanged(nameof(HiddenResultNote));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var auto = _autoCts;
        _autoCts = null;
        try { auto?.Cancel(); } catch (ObjectDisposedException) { }
        auto?.Dispose();
        try { _targetCts.Cancel(); } catch (ObjectDisposedException) { }
        _targetCts.Dispose();
    }
}
