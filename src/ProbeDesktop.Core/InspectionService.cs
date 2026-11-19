using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProbeDesktop.Core;

internal sealed record InspectionGrant(InspectionTarget Target, DateTimeOffset AuthorizedAt, int IntervalMinutes, bool Paused = false, int MaxAttempts = 1);

public sealed partial class DesktopService : IGlobalInspectionService, IInspectionSelectionService, IInspectionIntervalService, ILatestResultsService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RoundGates = new(StringComparer.OrdinalIgnoreCase);
    // Accessed under the directory's store gate; cancellation also covers pre-dispatch work outside that gate.
    private sealed record ActiveInspection(CancellationTokenSource Cancellation)
    {
        public HashSet<(Guid ProfileId, int KeyRevision, string BaseUrl, ApiProtocol Protocol)> Paused { get; } = [];
        public HashSet<(Guid ProfileId, string Model)> InvalidatedModels { get; } = [];
        public HashSet<Guid> InvalidatedProfiles { get; } = [];
    }
    private static readonly ConcurrentDictionary<string, ActiveInspection> ActiveInspections = new(StringComparer.OrdinalIgnoreCase);
    internal static bool HasActiveConnectionInspection(string directory) => ActiveInspections.ContainsKey(directory);

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
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? earliest = null;
        bool hasReady = false;
        var statuses = state.Profiles.Select(profile =>
        {
            InspectionTarget target = TargetFor(profile);
            InspectionGrant? grant = state.Vault.InspectionSettingsVersion == 1 && !state.Vault.InspectionUpgradePending && state.Vault.InspectionSettingsRevision != Guid.Empty
                ? state.Vault.InspectionGrants.Find(g => SameTargetProfile(g.Target, profile) && g.MaxAttempts == state.Vault.InspectionSettings.MaxRetries + 1 && g.IntervalMinutes == state.Vault.InspectionSettings.IntervalMinutes) : null;
            string[] authorized = grant is null || grant.Paused ? [] : target.Models.Where(m => grant.Target.Models.Contains(m, StringComparer.Ordinal)).ToArray();
            var selection = SelectionFor(state, profile.Id);
            var selected = selection.IsSelected ? target.Models.Where(m => selection.SelectedModelIds.Contains(m, StringComparer.Ordinal)).ToArray() : [];
            InspectionAuthorizationState authorization = target.Models.Count == 0 ? InspectionAuthorizationState.NoCachedModels :
                grant is { Paused: true } ? InspectionAuthorizationState.Paused : selected.Length != 0 && selected.All(m => authorized.Contains(m, StringComparer.Ordinal)) ? InspectionAuthorizationState.Authorized : InspectionAuthorizationState.PendingApproval;
            DateTimeOffset hostDeadline = state.Vault.InspectionHostDeadlines.GetValueOrDefault(InspectionHost(profile.BaseUrl));
            DateTimeOffset? profileDeadline = state.Vault.ConnectionResults.GetValueOrDefault(profile.Id)?.RetryNotBefore;
            var deadline = profileDeadline > hostDeadline ? profileDeadline.Value : hostDeadline;
            if (selected.Any(m => authorized.Contains(m, StringComparer.Ordinal)))
            {
                if (deadline <= now) hasReady = true;
                else if (earliest is null || deadline < earliest) earliest = deadline;
            }
            return new InspectionTargetStatus(target, sanitizer.Text(profile.Name, 100), authorization, grant?.AuthorizedAt, deadline > now ? deadline : null) { AuthorizedModels = authorized, HasModelCache = profile.ModelsFetchedAt is not null };
        }).ToArray();
        return new(state.Vault.InspectionSettings, statuses, SanitizeRound(InspectionRoundForDisplay(state), sanitizer))
        {
            Selections = state.Profiles.Select(p => SelectionFor(state, p.Id)).ToArray(),
            SettingsRevision = state.Vault.InspectionSettingsRevision,
            LastCompletedAt = state.Vault.LastInspectionCompletedAt,
            RetryNotBefore = hasReady ? null : earliest,
            NeedsUpgradeConfirmation = state.Vault.InspectionUpgradePending
        };
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
        if (consent is not null) throw new ClientOperationException("ConsentRequired", "请统一确认并保存巡检设置。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            state.Vault.InspectionSettings = state.Vault.InspectionSettings with { Enabled = settings.Enabled };
            if (!settings.Enabled) state.Vault.InspectionNonce = Guid.NewGuid();
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (!settings.Enabled && ActiveInspections.TryGetValue(DataDirectory, out ActiveInspection? active)) active.Cancellation.Cancel();
            return InspectionView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private void RequireInspectionTarget(StoreState state, InspectionTarget target, string model, Guid nonce, bool automatic)
    {
        if (state.Vault.InspectionNonce != nonce || (automatic && !state.Vault.InspectionSettings.Enabled))
            throw new ClientOperationException("InspectionStopped", "巡检已停止或授权已更新；队列不会继续发送。");
        RelayProfile profile = FindProfile(state, target.ProfileId);
        bool blockedInRound = ActiveInspections.TryGetValue(DataDirectory, out ActiveInspection? active) &&
            (active.InvalidatedProfiles.Contains(profile.Id) || active.Paused.Contains((profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol)) ||
             active.InvalidatedModels.Contains((profile.Id, model)));
        // The only caller holds a snapshot of an authorized target. Subsequent settings commits
        // cannot change it, but live configuration, cache and authentication guards still apply.
        if (!SameTargetProfile(target, profile) || !target.Models.Contains(model, StringComparer.Ordinal) ||
            !profile.Models.Any(m => m.Id == model) || blockedInRound ||
            state.Vault.InspectionGrants.Any(g => SameTargetProfile(g.Target, profile) && g.Paused))
            throw new ClientOperationException("InspectionNotAuthorized", "此配置或模型缓存已变化、未授权或因鉴权失败暂停。");
        RequireHostDeadline(state, profile.BaseUrl);
    }
    public Task<InspectionRoundSummary> RunInspectionRoundAsync(InspectionTrigger trigger = InspectionTrigger.Automatic, IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default) =>
        RunInspectionRoundCoreAsync(trigger, requireDue: false, progress, cancellationToken);

    public Task<InspectionRoundSummary> RunDueInspectionRoundAsync(IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default) =>
        RunInspectionRoundCoreAsync(InspectionTrigger.Automatic, requireDue: true, progress, cancellationToken);

    private async Task<InspectionRoundSummary> RunInspectionRoundCoreAsync(InspectionTrigger trigger, bool requireDue, IProgress<InspectionRoundSummary>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(trigger)) throw new ClientOperationException("InvalidTrigger", "未知巡检触发类型。");
        SemaphoreSlim gate = RoundGates.GetOrAdd(DataDirectory, _ => new(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new ClientOperationException("RoundBusy", "已有巡检轮次正在进行；不会排队或重复发送。");
        InspectionRoundSummary? round = null;
        bool dispatched = false;
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
                         ? t.Target.Models.Where(m => selected.SelectedModelIds.Contains(m, StringComparer.Ordinal) && t.AuthorizedModels.Contains(m, StringComparer.Ordinal)).ToArray() : []
                        }
                    };
                }).Where(t => t.Target.Models.Count > 0).ToArray()
                };
                if (trigger == InspectionTrigger.Automatic && !snapshot.Settings.Enabled)
                    throw new ClientOperationException("InspectionDisabled", "全局自动巡检已关闭。");
                if (requireDue && snapshot.LastCompletedAt is { } ended && ended.AddMinutes(snapshot.Settings.IntervalMinutes) > DateTimeOffset.UtcNow)
                    throw new ClientOperationException("InspectionNotDue", "尚未到预约时间。");
                if (!snapshot.Targets.Any(t => t.Target.Models.Any(m => t.AuthorizedModels.Contains(m, StringComparer.Ordinal))))
                    throw new ClientOperationException("InspectionNotAuthorized", "没有已授权的巡检目标；请先明确确认范围和费用。");
                if (trigger == InspectionTrigger.Automatic && snapshot.RetryNotBefore is { } retryAt && retryAt - DateTimeOffset.UtcNow is { Ticks: > 0 } retryAfter)
                    throw new ClientOperationException("ConnectionRateLimited", "所有巡检目标均在限流等待期。", retryAfter);
                nonce = state.Vault.InspectionNonce;
                ActiveInspections[DataDirectory] = new(roundCancellation);
                round = new() { Trigger = trigger, Total = snapshot.Targets.Sum(t => t.Target.Models.Count), IntervalMinutes = snapshot.Settings.IntervalMinutes, MaxRetries = snapshot.Settings.MaxRetries };
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
                        List<ConnectionTestResult> attempts = [];
                        try
                        {
                            for (int attempt = 0; attempt <= snapshot.Settings.MaxRetries; attempt++)
                            {
                                // Reuse the same pacing and both pre-send authorization checks for every attempt.
                                if (!firstRequest) await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                                firstRequest = false;
                                ConnectionTestResult result = await RunConnectionAsync(status.Target.ProfileId, model, null, false, cancellationToken,
                                    status.Target, round.RoundId, nonce, trigger == InspectionTrigger.Automatic ? ConnectionRecordSource.Automatic : ConnectionRecordSource.RoundManual,
                                    () => dispatched = true, snapshot.Settings.MaxRetries + 1).ConfigureAwait(false);
                                attempts.Add(result);
                                if (result.HttpStatusCode == 429) blockedHosts.Add(InspectionHost(status.Target.BaseUrl));
                                if (result.Stage == ConnectionTestStage.ModelResponded || result.HttpStatusCode is 401 or 403 or 429 ||
                                    result.Code is "CancelledBeforeSend" or "InspectionStopped" or "InspectionNotAuthorized" or "ConnectionTargetChanged" or "ConnectionRateLimited" or "ProfileNotFound") break;
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                        }
                        catch (OperationCanceledException)
                        { round = round with { Cancelled = true }; }
                        catch (ClientOperationException ex) when (ex.Code is "InspectionStopped")
                        { round = round with { Cancelled = true }; }
                        catch (ClientOperationException ex) when (ex.Code is "InspectionNotAuthorized" or "ProfileNotFound" or "ConnectionRateLimited" or "ConnectionBusy")
                        { /* Earlier completed attempts still count, not a second skipped target. */ }
                        // Account exactly once, even if cancellation/authorization change interrupted the next delay/check.
                        ConnectionTestResult? finalResult = InspectionRoundResults.BestCompleted(attempts);
                        round = finalResult?.Stage switch
                        {
                            ConnectionTestStage.ModelResponded => round with { Succeeded = round.Succeeded + 1 },
                            ConnectionTestStage.PossibleSuccess => round with { PossibleSuccessCount = round.PossibleSuccessCount + 1 },
                            null => round with { Skipped = round.Skipped + 1 },
                            // Unknown remains raw in each attempt, but all non-success outcomes count as failed in new rounds.
                            _ => round with { Failed = round.Failed + 1 }
                        };
                    }
                    progress?.Report(round);
                    if (round.Cancelled) break;
                }
                if (round.Cancelled) break;
            }
            if (cancellationToken.IsCancellationRequested) round = round with { Cancelled = true };
            return await FinishRoundAsync(round, dispatched, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round is not null)
        { return await FinishRoundAsync(round with { Cancelled = true }, dispatched, progress).ConfigureAwait(false); }
        finally
        {
            await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { ActiveInspections.TryRemove(DataDirectory, out _); }
            finally { _store.Gate.Release(); gate.Release(); }
        }
    }
    private async Task<InspectionRoundSummary> FinishRoundAsync(InspectionRoundSummary round, bool dispatched, IProgress<InspectionRoundSummary>? progress)
    {
        round = round with { CompletedAt = DateTimeOffset.UtcNow, CurrentModelId = null, CurrentProfileName = null };
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            round = SanitizeRound(round, new SensitiveText(state.Vault.KnownSecrets))!;
            state.Vault.LatestInspectionRound = round;
            if (!round.Cancelled && dispatched) state.Vault.LastInspectionCompletedAt = round.CompletedAt;
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
    private static InspectionRoundSummary? InspectionRoundForDisplay(StoreState state)
    {
        InspectionRoundSummary? round = state.Vault.LatestInspectionRound;
        if (round is null) return null;
        // A read-only projection: never migrate the persisted summary or rewrite individual attempts.
        InspectionRoundSummary fallback = round with { Failed = round.Failed + round.UnknownCount, UnknownCount = 0 };
        if (round.CompletedAt is null) return fallback; // Do not count the current target before its progress is committed.
        long accounted = (long)round.Succeeded + round.PossibleSuccessCount + round.Failed + round.UnknownCount;
        if (accounted <= 0 || accounted + round.Skipped > round.Total) return fallback;
        var attempts = RetainedConnectionHistory(state)
            .Where(r => r.RoundId == round.RoundId && r.Source is ConnectionRecordSource.Automatic or ConnectionRecordSource.RoundManual)
            .ToArray();
        if (attempts.Any(r => r.CompletedAt is null)) return fallback;
        var targets = attempts.GroupBy(r => (r.ProfileId, r.ModelId, r.KeyRevision, r.BaseUrl, r.Protocol)).ToArray();
        // Missing/deleted target histories must not erase known aggregate outcomes, especially after history trimming/clearing.
        if (targets.Length != accounted) return fallback;
        var best = InspectionRoundResults.FinalConnections(attempts, round.RoundId);
        var last = targets.Select(g => g.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).First());
        static (int Success, int Possible, int Failed) Counts(IEnumerable<ConnectionTestResult> results)
        {
            int success = 0, possible = 0, failed = 0;
            foreach (ConnectionTestResult result in results)
                switch (InspectionRoundResults.OutcomeRank(result))
                {
                    case 3: success++; break;
                    case 2: possible++; break;
                    default: failed++; break;
                }
            return (success, possible, failed);
        }
        var known = (fallback.Succeeded, fallback.PossibleSuccessCount, fallback.Failed);
        var bestCounts = Counts(best);
        // Legacy data has no per-round attempt count. Only reinterpret a summary that reconciles with either
        // the old last-attempt rule or the new best-attempt rule; otherwise partial/deleted history is inconclusive.
        if (known != Counts(last) && known != bestCounts) return fallback;
        return fallback with { Succeeded = bestCounts.Success, PossibleSuccessCount = bestCounts.Possible, Failed = bestCounts.Failed };
    }
    private static IEnumerable<ConnectionTestResult> RetainedConnectionHistory(StoreState state)
    {
        var pendingRound = state.Vault.LatestInspectionRound is { CompletedAt: null } active ? active.RoundId : (Guid?)null;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        int retained = 0;
        // The single in-flight round attempt is durable before dispatch but does not displace a
        // real result until it completes. An interrupted round no longer has this exemption.
        foreach (var record in state.Vault.ConnectionHistory.Where(r => r.StartedAt >= cutoff).OrderByDescending(r => r.StartedAt))
            if ((pendingRound is Guid id && record.RoundId == id && record.CompletedAt is null) || retained++ < 10_000)
                yield return record;
    }
    internal static void TrimConnectionHistory(StoreState state) => state.Vault.ConnectionHistory = RetainedConnectionHistory(state).ToList();
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
