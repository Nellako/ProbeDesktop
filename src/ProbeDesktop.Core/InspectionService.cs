using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProbeDesktop.Core;

internal sealed record InspectionGrant(InspectionTarget Target, DateTimeOffset AuthorizedAt, int IntervalMinutes, bool Paused = false, int MaxAttempts = 1);

public sealed partial class DesktopService : IGlobalInspectionService, IInspectionSelectionService, IInspectionIntervalService, ILatestResultsService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RoundGates = new(StringComparer.OrdinalIgnoreCase);
    // Accessed under the directory's store gate; cancellation also covers pre-dispatch work outside that gate.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> RoundCancellations = new(StringComparer.OrdinalIgnoreCase);

    private static InspectionTarget TargetFor(RelayProfile profile) => new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol,
        profile.Models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    private static bool SameTargetProfile(InspectionTarget target, RelayProfile profile) => target.ProfileId == profile.Id &&
        target.KeyRevision == profile.KeyRevision && target.BaseUrl == profile.BaseUrl && target.Protocol == profile.Protocol;
    private static bool SameTarget(InspectionTarget a, InspectionTarget b) => a.ProfileId == b.ProfileId && a.KeyRevision == b.KeyRevision &&
        a.BaseUrl == b.BaseUrl && a.Protocol == b.Protocol && a.Models.Count == b.Models.Count &&
        a.Models.Order(StringComparer.Ordinal).SequenceEqual(b.Models.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static string InspectionHost(string baseUrl) => new Uri(baseUrl).IdnHost.ToLowerInvariant();
    private static void RequireHostDeadline(StoreState state, string baseUrl)
    {
        DateTimeOffset deadline = state.Vault.InspectionHostDeadlines.GetValueOrDefault(InspectionHost(baseUrl));
        if (deadline > DateTimeOffset.UtcNow)
            throw new ClientOperationException("ConnectionRateLimited", "该主机处于限流等待期；不会更换密钥或路径绕过。", deadline - DateTimeOffset.UtcNow);
    }
    private static InspectionSnapshot InspectionView(StoreState state)
    {
        SensitiveText sanitizer = new(state.Vault.KnownSecrets);
        var statuses = state.Profiles.Select(profile =>
        {
            InspectionTarget target = TargetFor(profile);
            InspectionGrant? grant = state.Vault.InspectionGrants.Find(g => SameTargetProfile(g.Target, profile) && g.MaxAttempts == InspectionRoundResults.MaxAttempts && g.IntervalMinutes == state.Vault.InspectionSettings.IntervalMinutes);
            string[] authorized = grant is null || grant.Paused ? [] : target.Models.Where(m => grant.Target.Models.Contains(m, StringComparer.Ordinal)).ToArray();
            InspectionAuthorizationState authorization = target.Models.Count == 0 ? InspectionAuthorizationState.NoCachedModels :
                grant is { Paused: true } ? InspectionAuthorizationState.Paused : authorized.Length == target.Models.Count ? InspectionAuthorizationState.Authorized : InspectionAuthorizationState.PendingApproval;
            DateTimeOffset deadline = state.Vault.InspectionHostDeadlines.GetValueOrDefault(InspectionHost(profile.BaseUrl));
            return new InspectionTargetStatus(target, sanitizer.Text(profile.Name, 100), authorization, grant?.AuthorizedAt, deadline > DateTimeOffset.UtcNow ? deadline : null) { AuthorizedModels = authorized, HasModelCache = profile.ModelsFetchedAt is not null };
        }).ToArray();
        return new(state.Vault.InspectionSettings, statuses, SanitizeRound(state.Vault.LatestInspectionRound, sanitizer))
        { Selections = state.Profiles.Select(p => SelectionFor(state, p.Id)).ToArray() };
    }
    public async Task<InspectionSnapshot> GetInspectionSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return InspectionView(await _store.LoadAsync(cancellationToken).ConfigureAwait(false)); }
        finally { _store.Gate.Release(); }
    }
    public async Task<InspectionSnapshot> SaveInspectionSettingsAsync(InspectionSettings settings, InspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Enabled || consent is not null) RequireInspectionInterval(settings.IntervalMinutes);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Enabled && consent is null)
                settings = state.Vault.InspectionSettings with { Enabled = false }; // Dirty UI interval must never prevent stopping.
            if (consent is not null || settings.Enabled)
            {
                RequireInspectionInterval(settings.IntervalMinutes);
                ApplyInspectionConsent(state, settings.IntervalMinutes, consent);
            }
            state.Vault.InspectionSettings = settings with { };
            // Any explicit off or approval supersedes queued work from an older round, including manual rounds.
            state.Vault.InspectionNonce = Guid.NewGuid();
            if (RoundCancellations.TryGetValue(DataDirectory, out CancellationTokenSource? active)) active.Cancel();
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return InspectionView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private static void RequireInspectionTarget(StoreState state, InspectionTarget target, string model, Guid nonce, bool automatic)
    {
        if (state.Vault.InspectionNonce != nonce || (automatic && !state.Vault.InspectionSettings.Enabled))
            throw new ClientOperationException("InspectionStopped", "巡检已停止或授权已更新；队列不会继续发送。");
        RelayProfile profile = FindProfile(state, target.ProfileId);
        // Newly cached models await approval; unchanged authorized models remain eligible.
        InspectionTarget current = TargetFor(profile);
        InspectionGrant? grant = state.Vault.InspectionGrants.Find(g => SameTargetProfile(g.Target, profile) && g.MaxAttempts == InspectionRoundResults.MaxAttempts);
        InspectionSelection selection = SelectionFor(state, profile.Id);
        if (!selection.IsSelected || !selection.SelectedModelIds.Contains(model, StringComparer.Ordinal) || !SameTargetProfile(target, profile) || !current.Models.Contains(model, StringComparer.Ordinal) || grant is null || grant.Paused || !grant.Target.Models.Contains(model, StringComparer.Ordinal))
            throw new ClientOperationException("InspectionNotAuthorized", "此配置或模型缓存已变化、未授权或因鉴权失败暂停。");
        RequireHostDeadline(state, profile.BaseUrl);
    }
    public async Task<InspectionRoundSummary> RunInspectionRoundAsync(InspectionTrigger trigger = InspectionTrigger.Automatic, IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(trigger)) throw new ClientOperationException("InvalidTrigger", "未知巡检触发类型。");
        SemaphoreSlim gate = RoundGates.GetOrAdd(DataDirectory, _ => new(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new ClientOperationException("RoundBusy", "已有巡检轮次正在进行；不会排队或重复发送。");
        InspectionRoundSummary? round = null;
        using CancellationTokenSource roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = roundCancellation.Token;
        try
        {
            InspectionSnapshot snapshot;
            Guid nonce;
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                RequireInspectionInterval(state.Vault.InspectionSettings.IntervalMinutes);
                snapshot = InspectionView(state);
                snapshot = snapshot with
                {
                    Targets = snapshot.Targets.Select(t =>
                {
                    InspectionSelection selected = SelectionFor(state, t.Target.ProfileId);
                    return t with
                    {
                        Target = t.Target with
                        {
                            Models = selected.IsSelected
                        ? t.Target.Models.Where(m => selected.SelectedModelIds.Contains(m, StringComparer.Ordinal)).ToArray() : []
                        }
                    };
                }).Where(t => t.Target.Models.Count > 0).ToArray()
                };
                if (trigger == InspectionTrigger.Automatic && !snapshot.Settings.Enabled)
                    throw new ClientOperationException("InspectionDisabled", "全局自动巡检已关闭。");
                if (!snapshot.Targets.Any(t => t.Target.Models.Any(m => t.AuthorizedModels.Contains(m, StringComparer.Ordinal))))
                    throw new ClientOperationException("InspectionNotAuthorized", "没有已授权的巡检目标；请先明确确认范围和费用。");
                nonce = state.Vault.InspectionNonce;
                RoundCancellations[DataDirectory] = roundCancellation;
                round = new() { Trigger = trigger, Total = snapshot.Targets.Sum(t => t.Target.Models.Count) };
                state.Vault.LatestInspectionRound = round;
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _store.Gate.Release(); }
            progress?.Report(round);
            HashSet<string> blockedHosts = new(StringComparer.OrdinalIgnoreCase);
            bool firstRequest = true;
            foreach (InspectionTargetStatus status in snapshot.Targets)
            {
                IEnumerable<string?> models = status.Target.Models.Count == 0 ? new string?[] { null } : status.Target.Models.Select(m => (string?)m);
                foreach (string? model in models)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    round = round with { CurrentProfileName = status.ProfileName, CurrentModelId = model };
                    progress?.Report(round);
                    if (model is null || !status.AuthorizedModels.Contains(model, StringComparer.Ordinal) || blockedHosts.Contains(InspectionHost(status.Target.BaseUrl)))
                        round = round with { Skipped = round.Skipped + 1 };
                    else
                    {
                        ConnectionTestResult? finalResult = null;
                        try
                        {
                            for (int attempt = 0; attempt < InspectionRoundResults.MaxAttempts; attempt++)
                            {
                                // Reuse the same pacing and both pre-send authorization checks for every attempt.
                                if (!firstRequest) await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                                firstRequest = false;
                                finalResult = await RunConnectionAsync(status.Target.ProfileId, model, null, false, cancellationToken,
                                    status.Target, round.RoundId, nonce, trigger == InspectionTrigger.Automatic ? ConnectionRecordSource.Automatic : ConnectionRecordSource.RoundManual).ConfigureAwait(false);
                                if (finalResult.HttpStatusCode == 429) blockedHosts.Add(InspectionHost(status.Target.BaseUrl));
                                if (finalResult.Stage == ConnectionTestStage.ModelResponded || finalResult.HttpStatusCode is 401 or 403 or 429 ||
                                    finalResult.Code is "CancelledBeforeSend" or "InspectionStopped" or "InspectionNotAuthorized" or "ConnectionTargetChanged" or "ProfileNotFound") break;
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                        }
                        catch (OperationCanceledException)
                        { round = round with { Cancelled = true }; }
                        catch (ClientOperationException ex) when (ex.Code is "InspectionStopped")
                        { round = round with { Cancelled = true }; }
                        catch (ClientOperationException ex) when (ex.Code is "InspectionNotAuthorized" or "ProfileNotFound" or "ConnectionRateLimited" or "ConnectionBusy")
                        { /* An earlier completed attempt remains the final result, not a second skipped target. */ }
                        // Account exactly once, even if cancellation/authorization change interrupted the next delay/check.
                        round = finalResult?.Stage switch
                        {
                            ConnectionTestStage.ModelResponded => round with { Succeeded = round.Succeeded + 1 },
                            ConnectionTestStage.PossibleSuccess => round with { PossibleSuccessCount = round.PossibleSuccessCount + 1 },
                            ConnectionTestStage.Failed => round with { Failed = round.Failed + 1 },
                            null => round with { Skipped = round.Skipped + 1 },
                            _ => round with { UnknownCount = round.UnknownCount + 1 }
                        };
                    }
                    progress?.Report(round);
                    if (round.Cancelled) break;
                }
                if (round.Cancelled) break;
            }
            return await FinishRoundAsync(round, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round is not null)
        { return await FinishRoundAsync(round with { Cancelled = true }, progress).ConfigureAwait(false); }
        finally
        {
            await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { RoundCancellations.TryRemove(DataDirectory, out _); }
            finally { _store.Gate.Release(); gate.Release(); }
        }
    }
    private async Task<InspectionRoundSummary> FinishRoundAsync(InspectionRoundSummary round, IProgress<InspectionRoundSummary>? progress)
    {
        round = round with { CompletedAt = DateTimeOffset.UtcNow, CurrentModelId = null, CurrentProfileName = null };
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            round = SanitizeRound(round, new SensitiveText(state.Vault.KnownSecrets))!;
            state.Vault.LatestInspectionRound = round;
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
        progress?.Report(round);
        return round;
    }
    private static InspectionRoundSummary? SanitizeRound(InspectionRoundSummary? round, SensitiveText sanitizer) => round is null ? null : round with
    {
        CurrentProfileName = round.CurrentProfileName is null ? null : sanitizer.Text(round.CurrentProfileName, 100),
        CurrentModelId = round.CurrentModelId is null ? null : sanitizer.Identifier(round.CurrentModelId)
    };
    private static void TrimConnectionHistory(StoreState state) => state.Vault.ConnectionHistory = state.Vault.ConnectionHistory
        .Where(r => r.StartedAt >= DateTimeOffset.UtcNow.AddDays(-30)).OrderByDescending(r => r.StartedAt).Take(10_000).ToList();
    public async Task<IReadOnlyList<ConnectionTestResult>> GetConnectionHistoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            TrimConnectionHistory(state);
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            return state.Vault.ConnectionHistory.Select(r => SanitizeConnection(r, sanitizer)).ToArray();
        }
        finally { _store.Gate.Release(); }
    }
    public Task DeleteConnectionRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => EditConnectionHistoryAsync(recordId, cancellationToken);
    public Task ClearConnectionHistoryAsync(CancellationToken cancellationToken = default) => EditConnectionHistoryAsync(null, cancellationToken);
    private async Task EditConnectionHistoryAsync(Guid? recordId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            state.Vault.ConnectionHistory.RemoveAll(r => recordId is null || r.RecordId == recordId);
            state.Vault.ConnectionResults = state.Vault.ConnectionResults.Where(p => recordId is not null && p.Value.RecordId != recordId).ToDictionary();
            TrimConnectionHistory(state);
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }
    public async Task ExportConnectionHistoryAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        IReadOnlyList<ConnectionTestResult> records = await GetConnectionHistoryAsync(cancellationToken).ConfigureAwait(false);
        string path = Path.GetFullPath(destinationPath);
        if (IsWithinStore(path)) throw new ClientOperationException("ExportPath", "导出不能放入私有数据目录。");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            exportedAt = DateTimeOffset.UtcNow,
            records = records.Select(r => new
            {
                r.ProfileName,
                r.ModelId,
                r.Protocol,
                r.Source,
                r.StartedAt,
                r.CompletedAt,
                r.Stage,
                r.LocalValidationPassed,
                r.ApiReached,
                r.ModelResponded,
                r.ContextCheck,
                r.PromptVersion,
                r.Code,
                r.Message,
                r.HttpStatusCode,
                r.ElapsedMilliseconds
            })
        }, DesktopStore.Json);
        try
        {
            await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ClientOperationException("ExportWrite", "无法创建导出文件；请选择尚不存在且可写的文件名。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
