using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProbeDesktop.Core;

public sealed partial class DesktopService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConnectionGates = new(StringComparer.OrdinalIgnoreCase);
    // A single explicitly approved target per service session. Never serialized.
    private ConnectionTestSettings? _connectionSession;
    private DateTimeOffset _connectionNextDue;
    private DateTimeOffset _connectionApprovedProfileUpdatedAt;

    public async Task<ConnectionValidationResult> ValidateConnectionAsync(Guid profileId, string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ValidateConnection(await _store.LoadAsync(cancellationToken).ConfigureAwait(false), profileId, modelId); }
        finally { _store.Gate.Release(); }
    }

    private static ConnectionValidationResult ValidateConnection(StoreState state, Guid profileId, string modelId)
    {
        try
        {
            RelayProfile profile = FindProfile(state, profileId);
            _ = ConnectionTestEndpoints.Recipient(profile.BaseUrl, profile.Protocol); // Syntax only; no DNS.
            _ = GetKey(state, profile);
            if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 256 || modelId.Any(char.IsControl) || modelId != modelId.Trim())
                return new(false, "InvalidModel", "请输入完整且不含首尾空白、控制字符的模型 ID（最长 256 字符）。");
            if (new SensitiveText(state.Vault.KnownSecrets).Identifier(modelId) != modelId)
                return new(false, "SecretInModel", "模型字段不能包含当前或历史密钥。 ");
            return new(true, "LocalValidationPassed", "本地配置检查通过；尚未联网，不能说明 API 可达或模型能够响应。");
        }
        catch (ClientOperationException ex) { return new(false, ex.Code, ex.Message); }
    }

    public async Task<ConnectionTestSettings> GetConnectionSettingsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            RelayProfile profile = FindProfile(state, profileId);
            ConnectionTestSettings settings = state.Vault.ConnectionSettings.GetValueOrDefault(profileId) ?? new() { ProfileId = profileId, Protocol = profile.Protocol, BaseUrl = profile.BaseUrl, KeyRevision = profile.KeyRevision };
            if (SessionMatches(settings, profile)) return _connectionSession!;
            if (_connectionSession?.ProfileId == profileId) _connectionSession = null;
            return settings with { Enabled = false, ConsentAt = null };
        }
        finally { _store.Gate.Release(); }
    }

    public async Task<ConnectionTestSettings> SaveConnectionSettingsAsync(ConnectionTestSettings settings, ConnectionScheduleConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.IntervalMinutes is < 1 or > 1440)
            throw new ClientOperationException("InvalidInterval", "连接测试间隔必须为 1 至 1440 分钟，默认 60 分钟。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            RelayProfile profile = FindProfile(state, settings.ProfileId);
            ConnectionValidationResult validation = ValidateConnection(state, profile.Id, settings.ModelId);
            if (settings.Enabled && !validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
            if (settings.Enabled)
            {
                OutboundSecurity.RequireConsent(profile, consent?.Outbound, OutboundPurpose.ConnectionTest, settings.ModelId);
                if (consent!.IntervalMinutes != settings.IntervalMinutes)
                    throw new ClientOperationException("ConsentRequired", "自动测试间隔已变化，请重新确认重复调用及可能产生的费用。");
            }
            ConnectionTestSettings saved = settings with
            {
                ModelId = new SensitiveText(state.Vault.KnownSecrets).Identifier(settings.ModelId),
                BaseUrl = profile.BaseUrl,
                Protocol = profile.Protocol,
                KeyRevision = profile.KeyRevision,
                Enabled = false,
                ConsentAt = null
            };
            state.Vault.ConnectionSettings[profile.Id] = saved;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            // A delayed disable for another profile must not revoke or reschedule the newly approved target.
            // For the same profile the UI serializes disable/load/enable, including model switches.
            if (settings.Enabled)
            {
                _connectionSession = saved with { Enabled = true, ConsentAt = consent!.Outbound.AcceptedAt };
                _connectionApprovedProfileUpdatedAt = profile.UpdatedAt;
                _connectionNextDue = DateTimeOffset.UtcNow.AddMinutes(saved.IntervalMinutes);
                return _connectionSession;
            }
            if (_connectionSession?.ProfileId == profile.Id) _connectionSession = null;
            return saved;
        }
        finally { _store.Gate.Release(); }
    }

    private bool SessionMatches(ConnectionTestSettings settings, RelayProfile profile) =>
        _connectionSession is { Enabled: true } session && session.ProfileId == profile.Id &&
        _connectionApprovedProfileUpdatedAt == profile.UpdatedAt &&
        session.BaseUrl == profile.BaseUrl && session.KeyRevision == profile.KeyRevision && session.Protocol == profile.Protocol &&
        session.Protocol == settings.Protocol &&
        session.ModelId == settings.ModelId && session.IntervalMinutes == settings.IntervalMinutes &&
        session.BaseUrl == settings.BaseUrl && session.KeyRevision == settings.KeyRevision;

    public async Task<ConnectionTestResult?> GetLastConnectionResultAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            RelayProfile profile = FindProfile(state, profileId);
            return state.Vault.ConnectionResults.TryGetValue(profileId, out ConnectionTestResult? result) &&
                result.Protocol == profile.Protocol && result.BaseUrl == profile.BaseUrl && result.KeyRevision == profile.KeyRevision
                ? SanitizeConnection(InspectionRoundResults.RepresentativeForLatest(state.Vault.ConnectionHistory, result), new SensitiveText(state.Vault.KnownSecrets)) : null;
        }
        finally { _store.Gate.Release(); }
    }

    public Task<ConnectionTestResult> TestConnectionAsync(Guid profileId, string modelId, OutboundConsent consent, CancellationToken cancellationToken = default) =>
        RunConnectionAsync(profileId, modelId, consent, scheduled: false, cancellationToken);

    public Task<ConnectionTestResult> TestScheduledConnectionAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunConnectionAsync(profileId, null, null, scheduled: true, cancellationToken);

    private async Task<ConnectionTestResult> RunConnectionAsync(Guid profileId, string? modelId, OutboundConsent? consent, bool scheduled, CancellationToken cancellationToken,
        InspectionTarget? inspectionTarget = null, Guid? roundId = null, Guid? inspectionNonce = null, ConnectionRecordSource source = ConnectionRecordSource.Manual, Action? onDispatch = null,
        int maxInspectionAttempts = 1)
    {
        ThrowIfDisposed();
        SemaphoreSlim gate = ConnectionGates.GetOrAdd(DataDirectory.TrimEnd(Path.DirectorySeparatorChar), _ => new(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ClientOperationException("ConnectionBusy", "已有连接测试正在进行；不会重复发送。");
        try
        {
            RelayProfile profile;
            string key;
            ConnectionTestResult result;
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                profile = FindProfile(state, profileId);
                if (inspectionTarget is not null)
                    RequireInspectionTarget(state, inspectionTarget, modelId!, inspectionNonce!.Value, source == ConnectionRecordSource.Automatic);
                else if (scheduled)
                {
                    ConnectionTestSettings settings = state.Vault.ConnectionSettings.GetValueOrDefault(profileId) ?? new();
                    if (!SessionMatches(settings, profile))
                    {
                        if (_connectionSession?.ProfileId == profileId) _connectionSession = null;
                        throw new ClientOperationException("ScheduleNotAuthorized", "自动测试已关闭或配置发生变化，请重新开启并确认。");
                    }
                    if (DateTimeOffset.UtcNow < _connectionNextDue)
                        throw new ClientOperationException("ConnectionNotDue", "尚未到达下一次自动测试时间；不会提前发送。", _connectionNextDue - DateTimeOffset.UtcNow);
                    modelId = settings.ModelId;
                }
                ConnectionValidationResult validation = ValidateConnection(state, profileId, modelId ?? "");
                if (!validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
                if (!scheduled && inspectionTarget is null) OutboundSecurity.RequireConsent(profile, consent, OutboundPurpose.ConnectionTest, modelId);
                if (state.Vault.ConnectionResults.TryGetValue(profileId, out ConnectionTestResult? previous) && previous.RetryNotBefore > DateTimeOffset.UtcNow)
                    throw new ClientOperationException("ConnectionRateLimited", "服务要求等待 Retry-After；当前不会发送请求。", previous.RetryNotBefore - DateTimeOffset.UtcNow);
                RequireHostDeadline(state, profile.BaseUrl);
                key = GetKey(state, profile);
                result = new()
                {
                    ProfileId = profileId,
                    ProfileName = profile.Name,
                    KeyRevision = profile.KeyRevision,
                    Protocol = profile.Protocol,
                    BaseUrl = profile.BaseUrl,
                    ModelId = modelId!,
                    RoundId = roundId,
                    Source = scheduled ? ConnectionRecordSource.Automatic : source,
                    LocalValidationPassed = true,
                    PromptVersion = ConnectionChallenge.Version,
                    ContextCheck = ConnectionContextCheck.Inconclusive,
                    Stage = ConnectionTestStage.Unknown,
                    Code = "Interrupted",
                    Message = "连接测试已准备发送；若应用中断，调用结果未知，不会自动重试。"
                };
                if (inspectionTarget is null) state.Vault.ConnectionResults[profileId] = result;
                state.Vault.ConnectionHistory.Add(result);
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _store.Gate.Release(); }
            // Network single-flight is separate from the store transaction. Edits and disable remain responsive.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool ready = true;
            try
            {
                if (inspectionTarget is null)
                {
                    await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        StoreState latest = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                        RelayProfile current = FindProfile(latest, profileId);
                        if (!SameConnectionProfile(current, profile))
                            throw new ClientOperationException("ConnectionTargetChanged", "发送前配置已变化；未发送请求。");
                        if (scheduled && !SessionMatches(latest.Vault.ConnectionSettings.GetValueOrDefault(profileId) ?? new(), current))
                            throw new ClientOperationException("ScheduleNotAuthorized", "自动测试已关闭；未发送请求。");
                        RequireHostDeadline(latest, profile.BaseUrl);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    finally { _store.Gate.Release(); }
                }
            }
            catch (Exception ex) when (ex is ClientOperationException or OperationCanceledException)
            {
                ready = false;
                result = result with
                {
                    Stage = ConnectionTestStage.Failed,
                    Code = ex is ClientOperationException known ? known.Code : "CancelledBeforeSend",
                    Message = "发送前已取消或配置、授权发生变化；未发送请求。"
                };
            }
            bool sent = false;
            if (ready) result = await SendConnectionAsync(profile, modelId!, key, result, cancellationToken, () => { sent = true; onDispatch?.Invoke(); },
                inspectionTarget, inspectionNonce).ConfigureAwait(false);
            if (inspectionTarget is not null && sent && result.Stage != ConnectionTestStage.ModelResponded && result.HttpStatusCode is not (401 or 403 or 429))
                result = result with { Message = $"{result.Message} 本轮同目标最多发 {maxInspectionAttempts} 次请求。" };
            await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                StoreState state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                result = SanitizeConnection(result with { CompletedAt = DateTimeOffset.UtcNow, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds }, new SensitiveText(state.Vault.KnownSecrets));
                int index = state.Vault.ConnectionHistory.FindIndex(r => r.RecordId == result.RecordId);
                if (inspectionTarget is not null && !sent)
                {
                    if (index >= 0)
                    {
                        state.Vault.ConnectionHistory.RemoveAt(index);
                        await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ClientOperationException(result.Code is "InspectionStopped" ? result.Code : "InspectionNotAuthorized", result.Message);
                }
                if (index >= 0) state.Vault.ConnectionHistory[index] = result; // Never resurrect a user-deleted record.
                RelayProfile? current = state.Profiles.Find(p => p.Id == profileId);
                if (current is not null && SameConnectionProfile(current, profile) &&
                    (inspectionTarget is null ? state.Vault.ConnectionResults.GetValueOrDefault(profileId)?.RecordId == result.RecordId
                     : index >= 0 && ActiveInspections.TryGetValue(DataDirectory, out var running) && !running.InvalidatedProfiles.Contains(profileId)))
                    state.Vault.ConnectionResults[profileId] = result;
                if ((scheduled && !result.ModelResponded) || result.HttpStatusCode is 401 or 403) _connectionSession = null;
                if (result.HttpStatusCode is 401 or 403)
                {
                    state.Vault.InspectionGrants = state.Vault.InspectionGrants.Select(g => SameTargetProfile(g.Target, profile) ? g with { Paused = true } : g).ToList();
                    if (ActiveInspections.TryGetValue(DataDirectory, out ActiveInspection? active))
                        active.Paused.Add((profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol));
                }
                if (result.RetryNotBefore is { } deadline)
                {
                    string host = InspectionHost(profile.BaseUrl);
                    if (deadline > state.Vault.InspectionHostDeadlines.GetValueOrDefault(host)) state.Vault.InspectionHostDeadlines[host] = deadline;
                }
                _connectionNextDue = DateTimeOffset.UtcNow.AddMinutes(state.Vault.ConnectionSettings.GetValueOrDefault(profileId)?.IntervalMinutes ?? 60);
                await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            finally { _store.Gate.Release(); }
        }
        finally { gate.Release(); }
    }

    private async Task<ConnectionTestResult> SendConnectionAsync(RelayProfile profile, string modelId, string key, ConnectionTestResult result, CancellationToken cancellationToken,
        Action? onDispatch, InspectionTarget? inspectionTarget, Guid? inspectionNonce)
    {
        bool dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new(HttpMethod.Post, ConnectionTestEndpoints.Recipient(profile.BaseUrl, profile.Protocol));
            ModelApiEndpoints.Authenticate(request, profile.Protocol, key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ConnectionChallenge challenge = ConnectionChallenge.Create();
            result = result with { PromptVersion = ConnectionChallenge.Version, ContextCheck = ConnectionContextCheck.Inconclusive };
            byte[] body = CreateConnectionBody(profile.Protocol, modelId!, challenge.Prompt);
            try
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                timeout.Token.ThrowIfCancellationRequested();
                Task<HttpResponseMessage> sending;
                if (inspectionTarget is not null)
                {
                    await _store.Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        StoreState latest = await _store.LoadAsync(timeout.Token).ConfigureAwait(false);
                        RequireInspectionTarget(latest, inspectionTarget, modelId, inspectionNonce!.Value, result.Source == ConnectionRecordSource.Automatic);
                        if (latest.Vault.ConnectionResults.TryGetValue(profile.Id, out var current) && current.RetryNotBefore > DateTimeOffset.UtcNow)
                            throw new ClientOperationException("ConnectionRateLimited", "服务要求等待后再试，未发送请求。");
                        timeout.Token.ThrowIfCancellationRequested();
                        dispatched = true;
                        onDispatch?.Invoke();
                        sending = SendAsync(request, timeout.Token);
                    }
                    finally { _store.Gate.Release(); }
                }
                else
                {
                    dispatched = true;
                    sending = SendAsync(request, timeout.Token);
                }
                using HttpResponseMessage response = await sending.ConfigureAwait(false);
                result = result with { ApiReached = true, Stage = ConnectionTestStage.ApiReached, HttpStatusCode = (int)response.StatusCode };
                if (!response.IsSuccessStatusCode)
                {
                    ClientOperationException error = OutboundSecurity.HttpError(response);
                    result = result with
                    {
                        Stage = (int)response.StatusCode >= 500 ? ConnectionTestStage.Unknown : ConnectionTestStage.Failed,
                        Code = error.Code,
                        Message = ConnectionHttpMessage((int)response.StatusCode),
                        RetryNotBefore = error.RetryAfter is { } delay ? RetryDeadline(delay) : (int)response.StatusCode == 429 ? RetryDeadline(TimeSpan.FromSeconds(60)) : null
                    };
                }
                else
                {
                    var capture = new ConnectionTextCapture();
                    ConnectionTestStage stage = await ConnectionResponseReader.ReadAsync(response, profile.Protocol, timeout.Token, capture).ConfigureAwait(false);
                    ConnectionContextCheck contextCheck = capture.Check(stage, challenge.Expected);
                    result = result with
                    {
                        Stage = stage,
                        ModelResponded = stage == ConnectionTestStage.ModelResponded,
                        ContextCheck = contextCheck,
                        Code = stage switch
                        {
                            ConnectionTestStage.ModelResponded => "ModelResponded",
                            ConnectionTestStage.PossibleSuccess => "PossibleSuccess",
                            ConnectionTestStage.Failed => "ApiError",
                            _ => "IncompatibleResponse"
                        },
                        Message = stage switch
                        {
                            ConnectionTestStage.ModelResponded => "API 已返回有效文本并完成响应；仅证明本次调用收到兼容响应，不证明模型真实身份或能力。",
                            ConnectionTestStage.PossibleSuccess => "API 已返回成功响应，但无法确认有效模型文本；可能成功，不视为模型已响应，不会重发。",
                            ConnectionTestStage.Failed => "API 返回明确业务错误；未确认模型响应，不会自动重试。",
                            _ => "API 响应不完整或不兼容；结果未知，不会切换协议或重发。"
                        }
                    };
                    if (inspectionTarget is not null && stage != ConnectionTestStage.ModelResponded)
                        result = result with { Message = stage switch
                        {
                            ConnectionTestStage.PossibleSuccess => "API 返回成功响应，但未能确认有效模型文本。",
                            ConnectionTestStage.Failed => "API 返回业务错误，未确认模型响应。",
                            _ => "API 响应不完整或格式不兼容，未确认模型响应。"
                        } };
                    result = result with { Message = result.Message + (contextCheck switch
                    {
                        ConnectionContextCheck.Passed => " 标记读取校验通过（约256 token输入）；不代表长上下文能力。",
                        ConnectionContextCheck.Mismatch => " 标记读取校验不匹配：已收到响应，但未按要求返回三个标记；不等于断连或降智。",
                        _ => " 标记读取校验无法判断。"
                    }) };
                }
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ClientOperationException or IOException)
        {
            result = result with
            {
                Stage = dispatched ? ConnectionTestStage.Unknown : ConnectionTestStage.Failed,
                Code = ex is ClientOperationException known ? known.Code : ex is OperationCanceledException ? (!dispatched && inspectionTarget is not null ? "CancelledBeforeSend" : "CancelledOrTimeout") : "Network",
                Message = !dispatched && inspectionTarget is not null ? "发送前已取消，或当前配置不可用；未发送请求。" : inspectionTarget is not null
                    ? "未确认模型响应。请求可能已被服务处理或计费。" : ex is OperationCanceledException
                    ? "连接测试超时或已取消；请求可能已被服务处理，结果未知，不会自动重试。"
                    : "连接测试遇到网络、TLS、重定向或响应格式不兼容；无法确认模型响应，不会自动重试或切换协议。"
            };
        }
        return result;
    }

    private static bool SameConnectionProfile(RelayProfile a, RelayProfile b) => a.Id == b.Id && a.KeyRevision == b.KeyRevision && a.BaseUrl == b.BaseUrl && a.Protocol == b.Protocol;

    private static DateTimeOffset RetryDeadline(TimeSpan delay) => delay >= DateTimeOffset.MaxValue - DateTimeOffset.UtcNow
        ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + delay;

    private static string ConnectionHttpMessage(int status) => status switch
    {
        401 => "API 拒绝凭据（401），请检查当前密钥；未确认模型响应。",
        403 => "API 拒绝访问（403），请检查模型权限或访问限制；不绕过验证。",
        404 => "所选协议接口或模型不存在（404）；请检查 Base URL 和模型，不会自动追加 /v1 或切换协议。",
        400 => "API 拒绝请求（400），可能不接受所选协议的 token 参数，或模型、其他参数不兼容；不会自动更改参数、切换协议或重发。",
        405 or 415 or 422 => "服务不接受本次所选协议的模型请求，请检查模型和协议兼容性；不会自动更改参数或重发。",
        429 => "API 限流（429），请等待 Retry-After 或稍后手动操作；不会自动重试本次请求。",
        >= 300 and < 400 => "API 要求重定向；为保护密钥已拒绝跳转，未确认模型响应。",
        _ => $"API 返回 HTTP {status}，未确认模型响应；请求可能已产生费用，不会自动重试。"
    };

    private static byte[] CreateConnectionBody(ApiProtocol protocol, string modelId, string prompt)
    {
        // Allow room for all three markers. This is not a billing/cost ceiling.
        if (protocol == ApiProtocol.OpenAIResponses)
            return JsonSerializer.SerializeToUtf8Bytes(new { model = modelId, input = prompt, max_output_tokens = ConnectionChallenge.OutputTokens, stream = true });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = modelId,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = ConnectionChallenge.OutputTokens,
            stream = true
        });
    }

    private static ConnectionTestResult SanitizeConnection(ConnectionTestResult result, SensitiveText sanitizer) => result with
    {
        ProfileName = sanitizer.Text(result.ProfileName, 100),
        BaseUrl = sanitizer.CleanAddress(result.BaseUrl),
        ModelId = sanitizer.Identifier(result.ModelId),
        Code = sanitizer.Text(result.Code, 64),
        Message = sanitizer.Text(result.Message, 1024)
    };
}
