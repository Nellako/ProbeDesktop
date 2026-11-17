using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProbeDesktop.Core;

/// <summary>Single-user local core. Injected clients/protectors are trusted test or host dependencies.</summary>
public sealed partial class DesktopService : IDesktopService, IDirectConnectionService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SubmissionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DesktopStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _submissionGate;
    private bool _disposed;
    public string DataDirectory => _store.DirectoryPath;

    public DesktopService(string dataDirectory, HttpClient? httpClient = null, ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _store = new DesktopStore(dataDirectory, protector ?? new WindowsDpapiSecretProtector());
        _http = httpClient ?? OutboundSecurity.CreateHttpClient();
        _ownsHttp = httpClient is null;
        _submissionGate = SubmissionGates.GetOrAdd(DataDirectory.TrimEnd(Path.DirectorySeparatorChar), _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<RelayProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await _store.LoadAsync(cancellationToken).ConfigureAwait(false)).Profiles.ToArray(); }
        finally { _store.Gate.Release(); }
    }

    public async Task<RelayProfile> SaveProfileAsync(RelayProfile profile, string? replacementKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        string name = Required(profile.Name, 100, "档案名称");
        string baseUrl = OutboundSecurity.NormalizeBaseUrl(profile.BaseUrl);
        ModelApiEndpoints.ValidateProtocol(profile.Protocol);
        string model = OptionalInput(profile.DefaultModelId, 256, "默认模型");
        string? key = string.IsNullOrWhiteSpace(replacementKey) ? null : replacementKey.Trim();
        if (key is not null && (key.Length > 8192 || key.Any(char.IsControl) || key.Any(char.IsWhiteSpace)))
            throw new ClientOperationException("InvalidKey", "密钥最长 8192 字符，不能包含空白或控制字符。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            RelayProfile? existing = state.Profiles.Find(p => p.Id == profile.Id);
            if (existing is null && key is null)
                throw new ClientOperationException("KeyRequired", "新建档案必须输入 API Key；编辑时留空才表示保留原密钥。");
            Guid id = existing?.Id ?? Guid.NewGuid(); // Never trust a caller-supplied new identifier or secret reference.
            SecretEntry? oldSecret = existing is null ? null : state.Vault.Secrets.GetValueOrDefault(existing.Id);
            bool keyChanged = key is not null && !string.Equals(key, oldSecret?.Key, StringComparison.Ordinal);
            int revision = oldSecret is null ? 1 : keyChanged ? checked(oldSecret.Revision + 1) : oldSecret.Revision;
            string reference = oldSecret?.Reference ?? Guid.NewGuid().ToString("N");
            if (key is not null) state.Vault.KnownSecrets.Add(key);
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            if (sanitizer.CleanAddress(baseUrl) != baseUrl || sanitizer.Text(baseUrl, 2048).Contains("[已脱敏]", StringComparison.Ordinal))
                throw new ClientOperationException("SecretInAddress", "地址中不能嵌入密钥或令牌，请将凭据只填入 API Key。");
            foreach (RelayProfile other in state.Profiles.Where(p => p.Id != id))
                if (sanitizer.CleanAddress(other.BaseUrl) != other.BaseUrl)
                    throw new ClientOperationException("SecretInAddress", "该密钥与已有地址内容冲突；请先移除地址中的凭据。");
            bool invalidateModels = existing is null || keyChanged || existing.BaseUrl != baseUrl || existing.Protocol != profile.Protocol;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RelayProfile saved = sanitizer.Profile(new RelayProfile
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                SecretRef = reference,
                KeyRevision = revision,
                Protocol = profile.Protocol,
                DefaultModelId = model,
                Models = invalidateModels ? [] : existing!.Models,
                ModelsFetchedAt = invalidateModels ? null : existing!.ModelsFetchedAt,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            });
            if (existing is null) state.Profiles.Add(saved);
            else state.Profiles[state.Profiles.IndexOf(existing)] = saved;
            state.Vault.Secrets[id] = new SecretEntry(key ?? oldSecret!.Key, revision, reference);
            SanitizeState(state, sanitizer);
            if (_connectionSession?.ProfileId == id) _connectionSession = null;
            if (invalidateModels)
            {
                state.Vault.InspectionGrants.RemoveAll(g => g.Target.ProfileId == id);
                state.Vault.InspectionSelections = state.Vault.InspectionSelections.Select(s => s.ProfileId == id ? s with { SelectedModelIds = [] } : s).ToList();
                state.Vault.ConnectionResults.Remove(id);
                state.Vault.ConnectionSettings.Remove(id);
            }
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return state.Profiles.Single(p => p.Id == id);
        }
        finally { _store.Gate.Release(); }
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Profiles.RemoveAll(p => p.Id == profileId) == 0) return;
            state.Vault.Secrets.Remove(profileId);
            state.Vault.InspectionGrants.RemoveAll(g => g.Target.ProfileId == profileId);
            state.Vault.InspectionSelections.RemoveAll(s => s.ProfileId == profileId);
            state.Vault.ConnectionSettings.Remove(profileId);
            state.Vault.ConnectionResults.Remove(profileId);
            if (_connectionSession?.ProfileId == profileId) _connectionSession = null;
            // Historical redaction secrets and probe history intentionally survive profile deletion, encrypted.
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }

    public async Task<RelayProfile> RefreshModelsAsync(Guid profileId, OutboundConsent consent, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Keep the profile revision stable through dispatch and cache commit. SaveProfile does not make DNS requests.
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            RelayProfile profile = FindProfile(state, profileId);
            OutboundSecurity.RequireConsent(profile, consent, OutboundPurpose.ModelList, null);
            if (consent.AllowModelPagination)
                throw new ClientOperationException("ModelPaginationUnsupported", "本版本尚不支持分页授权，请关闭分页选项；不会发送请求或覆盖缓存。");
            string key = GetKey(state, profile);
            using HttpRequestMessage request = new(HttpMethod.Get, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol));
            ModelApiEndpoints.Authenticate(request, profile.Protocol, key);
            using CancellationTokenSource operation = OperationTimeout(cancellationToken);
            using HttpResponseMessage response = await SendAsync(request, operation.Token).ConfigureAwait(false);
            using JsonDocument document = await OutboundSecurity.ReadJsonAsync(response, operation.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() > 10_000)
                throw new ClientOperationException("ModelSchema", "模型列表格式不兼容；已保留上次成功缓存。");
            if (profile.Protocol == ApiProtocol.AnthropicMessages && document.RootElement.TryGetProperty("has_more", out JsonElement more))
            {
                if (more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ClientOperationException("ModelSchema", "模型列表分页字段不兼容；已保留上次成功缓存。");
                if (more.GetBoolean())
                    throw new ClientOperationException("ModelListPartial", "Anthropic 模型列表还有后续分页，本版本不自动分页；本次列表不完整，已保留上次成功缓存。");
            }
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            SortedDictionary<string, ModelEntry> models = new(StringComparer.Ordinal);
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || ProbeResponseAdapter.String(item, "id") is not string id ||
                    string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) continue;
                string safeId = sanitizer.Identifier(id);
                if (safeId != id) continue; // Never turn a secret-bearing model ID into a selectable outbound identifier.
                models.TryAdd(id, new ModelEntry(id, sanitizer.Optional(ProbeResponseAdapter.String(item, "owned_by"), 256)));
            }
            if (data.GetArrayLength() > 0 && models.Count == 0)
                throw new ClientOperationException("ModelSchema", "模型列表中没有有效模型标识；已保留上次成功缓存。");
            if (models.Count > 2000)
                throw new ClientOperationException("TooManyModels", "模型数量超过安全限制；已保留上次成功缓存。");
            RelayProfile updated = profile with
            {
                Models = models.Values.ToList(),
                ModelsFetchedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                DefaultModelId = models.ContainsKey(profile.DefaultModelId) ? profile.DefaultModelId : ""
            };
            state.Profiles[state.Profiles.IndexOf(profile)] = updated;
            state.Vault.InspectionSelections = state.Vault.InspectionSelections.Select(s => s.ProfileId == profileId
                ? s with { SelectedModelIds = s.SelectedModelIds.Where(models.ContainsKey).ToArray() } : s).ToList();
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ClientOperationException("Timeout", "获取模型列表超时；已保留上次成功缓存，不会自动重试。"); }
        catch (HttpRequestException)
        { throw new ClientOperationException("Network", "无法连接模型服务，请检查网络、TLS 证书和公网地址；已保留缓存。"); }
        finally { _store.Gate.Release(); }
    }

    public async Task<IReadOnlyList<ProbeRecord>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            return state.Vault.History.Select(sanitizer.Record).OrderByDescending(r => r.CreatedAt).ToArray();
        }
        finally { _store.Gate.Release(); }
    }

    public Task<ProbeRecord> StartProbeAsync(Guid profileId, string modelId, string? claimedModel, OutboundConsent consent, CancellationToken cancellationToken = default) =>
        StartProbeCoreAsync(profileId, modelId, claimedModel, consent, null, null, cancellationToken);

    private async Task<ProbeRecord> StartProbeCoreAsync(Guid profileId, string modelId, string? claimedModel, OutboundConsent consent, Guid? identityAttemptId, Guid? identityRoundId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        modelId = Required(modelId, 256, "模型标识");
        claimedModel = string.IsNullOrWhiteSpace(claimedModel) ? null : Required(claimedModel, 256, "声明模型");
        if (!await _submissionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ClientOperationException("SubmissionBusy", "已有提交正在进行，请勿重复点击或重新提交。");
        ProbeRecord? record = null;
        try
        {
            string key;
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                RelayProfile profile = FindProfile(state, profileId);
                OutboundSecurity.RequireConsent(profile, consent, OutboundPurpose.Probe, modelId);
                if (identityAttemptId is Guid attemptId)
                {
                    var round = RequireIdentityRound(state, identityRoundId ?? Guid.Empty);
                    var attempt = round.Attempts.FirstOrDefault(a => a.AttemptId == attemptId);
                    if (round.CompletedAt is not null || round.StopCode is not null || attempt is null || attempt.State != IdentityInspectionAttemptState.IntentPersisted || attempt.Target.ModelId != modelId || attempt.Target.ProfileId != profileId || state.Vault.History.Any(r => r.IdentityInspectionAttemptId == attemptId))
                        throw new ClientOperationException("IdentityIntentBlocked", "身份巡检意图无效或已尝试。");
                    RequireIdentityTarget(state, attempt.Target, round);
                }
                _ = ProbeProtocolMapping.UpstreamFormat(profile.Protocol);
                RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
                if (state.Vault.History.Any(r => IsActive(r.State)))
                    throw new ClientOperationException("ActiveRun", "已有未结束或结果未知的任务，请先恢复查询或明确删除本地记录后再发起新任务。");
                SensitiveText sanitizer = new(state.Vault.KnownSecrets);
                if (sanitizer.Identifier(modelId) != modelId || (claimedModel is not null && sanitizer.Identifier(claimedModel) != claimedModel))
                    throw new ClientOperationException("SecretInModel", "模型字段不能包含密钥或令牌。");
                key = GetKey(state, profile);
                record = new ProbeRecord
                {
                    ProfileId = profile.Id,
                    Protocol = profile.Protocol,
                    KeyRevision = profile.KeyRevision,
                    ProfileName = profile.Name,
                    BaseUrl = profile.BaseUrl,
                    ModelId = modelId,
                    ClaimedModel = claimedModel,
                    Source = identityAttemptId is null ? ProbeRecordSource.Manual : ProbeRecordSource.IdentityInspection,
                    IdentityInspectionAttemptId = identityAttemptId,
                    IdentityInspectionRoundId = identityRoundId,
                    IdentityInspectionTrigger = identityRoundId is Guid inspectionId ? RequireIdentityRound(state, inspectionId).Trigger : null,
                    ConsentAt = consent.AcceptedAt,
                    State = LocalRunState.Submitting
                };
                state.Vault.History.Add(record);
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _store.Gate.Release(); }

            bool dispatched = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using HttpRequestMessage request = new(HttpMethod.Post, OutboundSecurity.ServiceOrigin + OutboundSecurity.StartPath);
                Dictionary<string, object?> payload = new()
                {
                    ["baseUrl"] = record.BaseUrl,
                    ["apiKey"] = key,
                    ["modelId"] = modelId,
                    ["upstreamFormat"] = ProbeProtocolMapping.UpstreamFormat(record.Protocol),
                    ["sync"] = false
                };
                if (claimedModel is not null) payload["claimedModel"] = claimedModel;
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
                try
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    // No resubmission, including on timeout, cancellation, redirects, malformed JSON or 5xx.
                    using CancellationTokenSource operation = OperationTimeout(cancellationToken);
                    dispatched = true;
                    using HttpResponseMessage response = await SendAsync(request, operation.Token).ConfigureAwait(false);
                    using JsonDocument document = await OutboundSecurity.ReadJsonAsync(response, operation.Token).ConfigureAwait(false);
                    return await UpdateRecordAsync(record.LocalId,
                        (current, sanitizer) => ProbeResponseAdapter.Apply(current, document.RootElement, sanitizer, isSubmission: true),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(body); }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ClientOperationException or IOException)
            {
                string error = NetworkMessage(ex, "提交");
                ProbeRecord failed = await UpdateRecordAsync(record.LocalId, (current, _) => current with
                {
                    State = dispatched ? LocalRunState.SubmissionUnknown : LocalRunState.Failed,
                    Error = dispatched ? error + " 提交结果未知，请勿重新提交；若已有任务标识，仅恢复查询。" : "请求尚未发送，已取消。"
                }, CancellationToken.None).ConfigureAwait(false);
                if (ex is ClientOperationException operation && operation.Code.StartsWith("Http", StringComparison.Ordinal))
                {
                    if (operation.Code == "Http429") await SaveProbeRateLimitAsync(operation.RetryAfter).ConfigureAwait(false);
                    if (identityAttemptId is not null && operation.Code is "Http401" or "Http403") await PauseIdentityProfileAsync(profileId).ConfigureAwait(false);
                    throw new ClientOperationException(operation.Code, failed.Error!, operation.RetryAfter);
                }
                return failed;
            }
        }
        finally { _submissionGate.Release(); }
    }

    public async Task<ProbeRecord> PollProbeAsync(Guid localId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ProbeRecord record;
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            record = FindRecord(state, localId);
            if (IsTerminal(record.State)) return record;
            RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
            if (!OutboundSecurity.IsSafeRunId(record.RemoteRunId))
                throw new ClientOperationException("NoRunId", "没有可安全查询的任务标识，无法恢复；不会重新提交。");
        }
        finally { _store.Gate.Release(); }
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                OutboundSecurity.ServiceOrigin + OutboundSecurity.StartPath + "/" + Uri.EscapeDataString(record.RemoteRunId!));
            // Deliberately no relay Authorization, key lookup, or profile lookup. Deleted profiles remain pollable.
            using CancellationTokenSource operation = OperationTimeout(cancellationToken);
            using HttpResponseMessage response = await SendAsync(request, operation.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return await UpdateRecordAsync(localId, (current, _) => current with
                {
                    State = LocalRunState.Expired,
                    Error = "服务已无法查询此任务，可能已超过保留期；不会重新提交。",
                    LastPolledAt = DateTimeOffset.UtcNow
                }, CancellationToken.None).ConfigureAwait(false);
            using JsonDocument document = await OutboundSecurity.ReadJsonAsync(response, operation.Token).ConfigureAwait(false);
            return await UpdateRecordAsync(localId, (current, sanitizer) =>
                ProbeResponseAdapter.Apply(current, document.RootElement, sanitizer, isSubmission: false) with
                { LastPolledAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ClientOperationException or IOException)
        {
            ProbeRecord interrupted = await UpdateRecordAsync(localId, (current, _) => current with
            {
                State = LocalRunState.PollingInterrupted,
                Error = NetworkMessage(ex, "查询") + " 可稍后恢复查询，不会重新提交。",
                LastPolledAt = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);
            if (ex is ClientOperationException operation && operation.Code.StartsWith("Http", StringComparison.Ordinal))
            {
                if (operation.Code == "Http429") await SaveProbeRateLimitAsync(operation.RetryAfter).ConfigureAwait(false);
                if (record.Source == ProbeRecordSource.IdentityInspection && operation.Code is "Http401" or "Http403") await PauseIdentityProfileAsync(record.ProfileId).ConfigureAwait(false);
                throw new ClientOperationException(operation.Code, interrupted.Error!, operation.RetryAfter);
            }
            return interrupted;
        }
    }

    public async Task DeleteRecordAsync(Guid localId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            ProbeRecord? record = state.Vault.History.Find(r => r.LocalId == localId);
            if (record is null) return;
            if (record.State == LocalRunState.Submitting)
                throw new ClientOperationException("SubmissionBusy", "提交期间不能删除本地记录，请等待本次提交结束。");
            state.Vault.History.Remove(record);
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }

    public async Task ExportRecordAsync(Guid localId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        byte[] bytes;
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            ProbeRecord source = FindRecord(state, localId);
            IEnumerable<string> redactions = state.Vault.KnownSecrets;
            if (source.RemoteRunId is { } id) redactions = redactions.Append(id);
            if (source.ReportUrl is { } report) redactions = redactions.Append(report);
            ProbeRecord record = new SensitiveText(redactions).Record(source);
            // Explicit export DTO: no remote identifier, report URL, base URL, headers, raw JSON or operational attributes.
            var export = new
            {
                schemaVersion = 1,
                service = "BazaarLink Probe",
                exportedAt = DateTimeOffset.UtcNow,
                record.ProfileName,
                record.ModelId,
                record.Protocol,
                record.ClaimedModel,
                state = record.State.ToString(),
                record.CreatedAt,
                record.CompletedAt,
                record.IdentityStatus,
                record.VerdictStatus,
                record.InferredFamily,
                record.InferredModel,
                record.Confidence,
                record.Reasoning,
                record.Warnings,
                record.Items,
                record.TotalInputTokens,
                record.TotalOutputTokens,
                record.Error
            };
            bytes = JsonSerializer.SerializeToUtf8Bytes(export, DesktopStore.Json);
        }
        finally { _store.Gate.Release(); }
        string path = Path.GetFullPath(destinationPath);
        bool created = false;
        try
        {
            if (IsWithinStore(path))
                throw new ClientOperationException("ExportPath", "导出文件不能放入应用私有数据目录，请选择独立的本地 JSON 文件。");
            await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
            created = true;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ClientOperationException("ExportWrite", "无法创建导出文件。请选择有权限且尚不存在的本地文件名；不会覆盖原文件。"); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            // A partial new export contains only redacted fields. Never remove a path that may belong to another writer.
            _ = created;
        }
    }

    private bool IsWithinStore(string path)
    {
        string relative = Path.GetRelativePath(DataDirectory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.ConnectionClose = true;
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // The default production handler never redirects. Also fail closed if a supplied host transport did redirect.
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri != request.RequestUri)
        {
            response.Dispose();
            throw new ClientOperationException("RedirectRejected", "传输发生了未批准的地址跳转，已拒绝解释响应。");
        }
        return response;
    }

    private async Task<ProbeRecord> UpdateRecordAsync(Guid id, Func<ProbeRecord, SensitiveText, ProbeRecord> update, CancellationToken cancellationToken)
    {
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            ProbeRecord current = FindRecord(state, id);
            // A stale concurrent polling result must never revert a terminal result.
            if (IsTerminal(current.State)) return current;
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            ProbeRecord updated = sanitizer.Record(update(current, sanitizer));
            if (IsTerminal(updated.State) && updated.CompletedAt is null)
                updated = updated with { CompletedAt = DateTimeOffset.UtcNow };
            state.Vault.History[state.Vault.History.IndexOf(current)] = updated;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _store.Gate.Release(); }
    }

    private static void SanitizeState(StoreState state, SensitiveText sanitizer)
    {
        state.Profiles = state.Profiles.Select(sanitizer.Profile).ToList();
        state.Vault.History = state.Vault.History.Select(sanitizer.Record).ToList();
        ConvergeIdentityInspectionState(state);
        state.Vault.NotificationMemory.RemoveAll(m => sanitizer.Identifier(m.Target.ModelId) != m.Target.ModelId || sanitizer.CleanAddress(m.Target.BaseUrl) != m.Target.BaseUrl || sanitizer.Text(m.ProblemType ?? "", 80) != (m.ProblemType ?? ""));
        state.Vault.ConnectionHistory = state.Vault.ConnectionHistory.Select(r => SanitizeConnection(r, sanitizer)).ToList();
        state.Vault.LatestInspectionRound = SanitizeRound(state.Vault.LatestInspectionRound, sanitizer);
        // Never transform an authorization into a different outbound identifier after secret rotation.
        state.Vault.InspectionGrants = state.Vault.InspectionGrants.Where(g =>
            sanitizer.CleanAddress(g.Target.BaseUrl) == g.Target.BaseUrl &&
            g.Target.Models.All(m => sanitizer.Identifier(m) == m)).ToList();
        state.Vault.InspectionSelections = state.Vault.InspectionSelections.Select(s => s with
        {
            SelectedModelIds = s.SelectedModelIds.Where(m => sanitizer.Identifier(m) == m).ToArray()
        }).ToList();
        state.Vault.ConnectionResults = state.Vault.ConnectionResults.ToDictionary(p => p.Key, p => SanitizeConnection(p.Value, sanitizer));
        state.Vault.ConnectionSettings = state.Vault.ConnectionSettings.ToDictionary(p => p.Key, p => p.Value with
        {
            ModelId = sanitizer.Identifier(p.Value.ModelId),
            BaseUrl = sanitizer.CleanAddress(p.Value.BaseUrl),
            Enabled = false,
            ConsentAt = null
        });
    }
    private static RelayProfile FindProfile(StoreState state, Guid id) => state.Profiles.Find(p => p.Id == id)
        ?? throw new ClientOperationException("ProfileNotFound", "档案已不存在，请重新选择。");
    private static ProbeRecord FindRecord(StoreState state, Guid id) => state.Vault.History.Find(r => r.LocalId == id)
        ?? throw new ClientOperationException("RecordNotFound", "本地记录已不存在。");
    private static string GetKey(StoreState state, RelayProfile profile) => state.Vault.Secrets.TryGetValue(profile.Id, out SecretEntry? secret) &&
        secret.Revision == profile.KeyRevision && secret.Reference == profile.SecretRef ? secret.Key
        : throw new ClientOperationException("SecretMissing", "无法读取当前档案密钥，请重新输入；不会发送请求。");
    private static bool IsActive(LocalRunState state) => state is LocalRunState.Submitting or LocalRunState.SubmissionUnknown or
        LocalRunState.Running or LocalRunState.PollingInterrupted or LocalRunState.IncompatibleResponse;
    private static CancellationTokenSource OperationTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(90));
        return linked;
    }
    private static bool IsTerminal(LocalRunState state) => state is LocalRunState.Completed or LocalRunState.Failed or LocalRunState.Aborted or LocalRunState.Expired;
    private static string Required(string? value, int maximum, string label)
    {
        string text = OptionalInput(value, maximum, label);
        if (text.Length == 0) throw new ClientOperationException("Validation", label + "不能为空。");
        return text;
    }
    private static string OptionalInput(string? value, int maximum, string label)
    {
        string text = value?.Trim() ?? "";
        if (text.Length > maximum || text.Any(char.IsControl))
            throw new ClientOperationException("Validation", $"{label}最长 {maximum} 字符，不能包含控制字符。");
        return text;
    }
    private static string NetworkMessage(Exception exception, string operation) => exception switch
    {
        ClientOperationException known => known.Message,
        OperationCanceledException => operation + "超时或已取消。",
        _ => operation + "遇到网络或 TLS 错误。"
    };
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _store.RequireWritable();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _migrationPreview?.Dispose();
        if (_ownsHttp) _http.Dispose();
        // Shared semaphores outlive service instances and must not be disposed here.
    }
}
