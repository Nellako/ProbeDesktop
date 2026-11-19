namespace ProbeDesktop.Core;

internal sealed record IdentityInspectionGrant(IdentityInspectionTarget Target, DateTimeOffset AcceptedAt, int IntervalMinutes, bool Paused = false);

public sealed partial class DesktopService : IIdentityInspectionService
{
    internal const int CurrentIdentityInspectionSettingsVersion = 1;

    private static IdentityInspectionTarget IdentityTargetFor(RelayProfile p, string model) => new(p.Id, p.KeyRevision, p.BaseUrl, p.Protocol, model);
    private static bool IdentityProfileMatches(IdentityInspectionTarget t, RelayProfile p) => t.ProfileId == p.Id && t.KeyRevision == p.KeyRevision && t.BaseUrl == p.BaseUrl && t.Protocol == p.Protocol;
    private static IdentityInspectionSelection IdentitySelectionFor(StoreState s, Guid id) => s.Vault.IdentityInspectionSelections.Find(x => x.ProfileId == id) ?? new(id, false, []);
    private static bool IdentitySelected(StoreState s, IdentityInspectionTarget t)
    {
        var selection = IdentitySelectionFor(s, t.ProfileId);
        return selection.IsSelected && selection.SelectedModelIds.Contains(t.ModelId, StringComparer.Ordinal);
    }
    internal static void ConvergeIdentityInspectionState(StoreState state)
    {
        SensitiveText sanitizer = new(state.Vault.KnownSecrets);
        state.Vault.IdentityInspectionSelections = state.Vault.IdentityInspectionSelections.Where(s => state.Profiles.Any(p => p.Id == s.ProfileId)).Select(s => s with
        {
            SelectedModelIds = s.SelectedModelIds.Where(m => state.Profiles.Single(p => p.Id == s.ProfileId).Models.Any(c => c.Id == m) && sanitizer.Identifier(m) == m).Distinct(StringComparer.Ordinal).ToArray()
        }).ToList();
        state.Vault.IdentityInspectionGrants.RemoveAll(g => !state.Profiles.Any(p => IdentityProfileMatches(g.Target, p) && p.Models.Any(m => m.Id == g.Target.ModelId)) || sanitizer.Identifier(g.Target.ModelId) != g.Target.ModelId || sanitizer.CleanAddress(g.Target.BaseUrl) != g.Target.BaseUrl);
        state.Vault.NotificationMemory.RemoveAll(m => !Enum.IsDefined(m.Category) || !state.Profiles.Any(p => NotificationProfileMatches(m.Target, p)) || sanitizer.Identifier(m.Target.ModelId) != m.Target.ModelId || sanitizer.CleanAddress(m.Target.BaseUrl) != m.Target.BaseUrl || sanitizer.Text(m.ProblemType ?? "", 80) != (m.ProblemType ?? ""));
        if (state.Vault.LatestIdentityInspectionRound is { } round)
            state.Vault.LatestIdentityInspectionRound = round with { Attempts = round.Attempts.Select(a => a with { Target = a.Target with { BaseUrl = sanitizer.CleanAddress(a.Target.BaseUrl), ModelId = sanitizer.Identifier(a.Target.ModelId) } }).ToArray() };
    }
    private static IdentityInspectionSnapshot IdentityView(StoreState state)
    {
        ConvergeIdentityInspectionState(state);
        SensitiveText sanitizer = new(state.Vault.KnownSecrets);
        var vault = state.Vault;
        bool confirmed = vault.IdentityInspectionSettingsVersion == CurrentIdentityInspectionSettingsVersion &&
            !vault.IdentityInspectionUpgradePending && vault.IdentityInspectionSettingsRevision != Guid.Empty;
        var retryNotBefore = vault.InspectionHostDeadlines.GetValueOrDefault(InspectionHost(OutboundSecurity.ServiceOrigin));
        return new(vault.IdentityInspectionSettings, state.Profiles.Select(p => IdentitySelectionFor(state, p.Id)).ToArray(), state.Profiles.SelectMany(p => p.Models.Select(m =>
        {
            var target = IdentityTargetFor(p, m.Id);
            var grant = confirmed ? vault.IdentityInspectionGrants.Find(g => g.Target == target && g.IntervalMinutes == vault.IdentityInspectionSettings.IntervalMinutes) : null;
            return new IdentityInspectionTargetStatus(target, sanitizer.Text(p.Name, 100), IdentitySelected(state, target), grant is { Paused: false }, grant?.Paused ?? false);
        })).ToArray(), vault.LatestIdentityInspectionRound)
        {
            SettingsRevision = vault.IdentityInspectionSettingsRevision,
            LastCompletedAt = vault.LastIdentityInspectionCompletedAt,
            NeedsUpgradeConfirmation = vault.IdentityInspectionUpgradePending,
            RetryNotBefore = retryNotBefore > DateTimeOffset.UtcNow ? retryNotBefore : null
        };
    }
    public async Task<IdentityInspectionSnapshot> GetIdentityInspectionSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return IdentityView(await _store.LoadAsync(cancellationToken).ConfigureAwait(false)); }
        finally { _store.Gate.Release(); }
    }
    // Legacy split saves no longer change the confirmed settings, even when the switch is off.
    public Task<IdentityInspectionSnapshot> SaveIdentityInspectionSelectionsAsync(IReadOnlyList<IdentityInspectionSelection> selections, CancellationToken cancellationToken = default) =>
        Task.FromException<IdentityInspectionSnapshot>(new ClientOperationException("ConsentRequired", "请统一确认身份巡检设置。"));

    public Task<IdentityInspectionSnapshot> SaveIdentityInspectionIntervalAsync(int intervalMinutes, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default) =>
        Task.FromException<IdentityInspectionSnapshot>(new ClientOperationException("ConsentRequired", "请统一确认身份巡检设置。"));

    public async Task<IdentityInspectionSnapshot> ConfirmIdentityInspectionSettingsAsync(IReadOnlyList<IdentityInspectionSelection> selections, IdentityInspectionConsent consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(consent);
        ThrowIfDisposed();
        RequireIdentityInterval(consent.IntervalMinutes);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (selections.Any(s => s is null || s.SelectedModelIds is null) || selections.Select(s => s.ProfileId).Distinct().Count() != selections.Count)
                throw new ClientOperationException("InvalidSelection", "身份巡检范围无效或含重复配置。");

            var sanitizer = new SensitiveText(state.Vault.KnownSecrets);
            List<IdentityInspectionSelection> saved = [];
            List<IdentityInspectionTarget> selected = [];
            foreach (var selection in selections)
            {
                var profile = state.Profiles.Find(p => p.Id == selection.ProfileId) ??
                    throw new ClientOperationException("InvalidSelection", "配置已删除，请重新选择巡检范围。");
                var models = selection.SelectedModelIds.Distinct(StringComparer.Ordinal).ToArray();
                if (models.Any(m => m is null || !profile.Models.Any(c => c.Id == m) || sanitizer.Identifier(m) != m))
                    throw new ClientOperationException("InvalidSelection", "只能选择当前缓存模型。");
                saved.Add(selection with { SelectedModelIds = models });
                if (selection.IsSelected)
                    selected.AddRange(models.Select(m => IdentityTargetFor(profile, m)));
            }

            RequireExactIdentityConsent(consent, selected, consent.IntervalMinutes, state.Vault.IdentityInspectionSettingsRevision,
                "请重新确认当前选中目标的精确范围、间隔及第三方身份披露。");
            foreach (var target in selected)
            {
                if (sanitizer.CleanAddress(target.BaseUrl) != target.BaseUrl)
                    throw new ClientOperationException("InvalidSelection", "目标地址含敏感信息，不能用于身份巡检。");
                var validation = ValidateConnection(state, target.ProfileId, target.ModelId);
                if (!validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
            }

            var vault = state.Vault;
            vault.IdentityInspectionSelections = saved;
            vault.IdentityInspectionSettings = vault.IdentityInspectionSettings with { IntervalMinutes = consent.IntervalMinutes };
            vault.IdentityInspectionGrants = selected.Select(t => new IdentityInspectionGrant(t, consent.AcceptedAt, consent.IntervalMinutes)).ToList();
            vault.IdentityInspectionSettingsRevision = Guid.NewGuid();
            vault.IdentityInspectionSettingsVersion = CurrentIdentityInspectionSettingsVersion;
            vault.IdentityInspectionUpgradePending = false;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return IdentityView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private static void RequireIdentityInterval(int interval)
    {
        if (interval is < 10 or > 480) throw new ClientOperationException("InvalidInterval", "身份巡检间隔必须为 10 至 480 分钟的整数。");
    }
    private static void RequireExactIdentityConsent(IdentityInspectionConsent? consent, IReadOnlyCollection<IdentityInspectionTarget> targets, int interval, Guid revision, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (consent is null || consent.Scope is null || consent.IntervalMinutes != interval || consent.SettingsRevision != revision ||
            consent.AcceptedAt < now.AddMinutes(-10) || consent.AcceptedAt > now.AddSeconds(30) ||
            consent.Scope.Count != targets.Count || !consent.Scope.ToHashSet().SetEquals(targets))
            throw new ClientOperationException("ConsentRequired", message);
    }
    public async Task<IdentityInspectionSnapshot> SaveIdentityInspectionSettingsAsync(IdentityInspectionSettings settings, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        if (consent is not null) throw new ClientOperationException("ConsentRequired", "请通过统一确认保存身份巡检范围与间隔。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            state.Vault.IdentityInspectionSettings = state.Vault.IdentityInspectionSettings with { Enabled = settings.Enabled };
            // Stop this round permanently, but leave any dispatched run and its read-only recovery intact.
            if (!settings.Enabled && state.Vault.LatestIdentityInspectionRound is { CompletedAt: null, StopCode: null } round)
                state.Vault.LatestIdentityInspectionRound = round with { StopCode = "IdentityInspectionDisabled" };
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return IdentityView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private static IdentityInspectionRoundSummary RequireIdentityRound(StoreState state, Guid id)
    {
        var round = state.Vault.LatestIdentityInspectionRound;
        if (round is null || round.RoundId != id) throw new ClientOperationException("IdentityRoundNotFound", "身份巡检轮次已不存在。");
        return round;
    }

    private static void InvalidateIdentityTargets(StoreState state, Guid profileId, IReadOnlyDictionary<string, ModelEntry>? remainingModels = null)
    {
        if (state.Vault.LatestIdentityInspectionRound is not { CompletedAt: null } round) return;
        state.Vault.LatestIdentityInspectionRound = round with
        {
            Attempts = round.Attempts.Select(a => a.Target.ProfileId == profileId &&
                (remainingModels is null || !remainingModels.ContainsKey(a.Target.ModelId)) &&
                a.State is IdentityInspectionAttemptState.Pending or IdentityInspectionAttemptState.IntentPersisted
                ? a with { Code = "IdentityTargetInvalidated" } : a).ToArray()
        };
    }
    private static void RequireIdentityTarget(StoreState state, IdentityInspectionTarget target, IdentityInspectionRoundSummary round)
    {
        if (round.Trigger == IdentityInspectionTrigger.Automatic && !state.Vault.IdentityInspectionSettings.Enabled) throw new ClientOperationException("IdentityInspectionDisabled", "身份巡检已关闭。");
        if (round.PausedProfileIds.Contains(target.ProfileId) || state.Vault.IdentityInspectionGrants.Any(g => g.Target == target && g.Paused))
            throw new ClientOperationException("IdentityProfilePaused", "此配置因认证被拒绝而暂停。");
        var profile = FindProfile(state, target.ProfileId);
        // Consent and selection were frozen at Begin; changing either only affects later rounds.
        if (!IdentityProfileMatches(target, profile) || !profile.Models.Any(m => m.Id == target.ModelId) ||
            (round.Trigger == IdentityInspectionTrigger.Manual && round.ManualConsentAt is null))
            throw new ClientOperationException("IdentityNotAuthorized", "配置或缓存模型已变化，未发送请求。");
        var validation = ValidateConnection(state, profile.Id, target.ModelId);
        if (!validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
        RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
    }

    // Used at POST dispatch, after the attempt and its single local submission record have been persisted.
    private static void RequireIdentityDispatch(StoreState state, Guid roundId, Guid attemptId, ProbeRecord record)
    {
        var round = RequireIdentityRound(state, roundId);
        var attempt = round.Attempts.FirstOrDefault(a => a.AttemptId == attemptId);
        if (round.CompletedAt is not null || round.StopCode is not null || attempt is null ||
            attempt.State != IdentityInspectionAttemptState.IntentPersisted || attempt.Code is "IdentityTargetInvalidated" or "IdentityCacheInvalidated" ||
            attempt.Target != new IdentityInspectionTarget(record.ProfileId, record.KeyRevision, record.BaseUrl, record.Protocol, record.ModelId) ||
            !state.Vault.History.Any(r => r.LocalId == record.LocalId && r.State == LocalRunState.Submitting &&
                r.Source == ProbeRecordSource.IdentityInspection && r.IdentityInspectionRoundId == roundId && r.IdentityInspectionAttemptId == attemptId))
            throw new ClientOperationException(round.StopCode ?? "IdentityIntentBlocked", "身份巡检已停止或提交意图无效，未发送请求。");
        RequireIdentityTarget(state, attempt.Target, round);
        if (state.Vault.History.Any(r => r.LocalId != record.LocalId && (IsActive(r.State) || r.IdentityInspectionAttemptId == attemptId)))
            throw new ClientOperationException("ActiveRun", "已有活动或未知任务，未发送请求。");
    }

    private static IdentityInspectionRoundSummary ReplaceIdentityAttempt(IdentityInspectionRoundSummary round, IdentityInspectionAttempt attempt) => round with { Attempts = round.Attempts.Select(a => a.AttemptId == attempt.AttemptId ? attempt : a).ToArray() };
    private static bool HasIdentityPending(IdentityInspectionRoundSummary round) => round.CompletedAt is null && round.StopCode is null && round.Attempts.Any(a => a.State == IdentityInspectionAttemptState.Pending);

    public Task<IdentityInspectionRoundSummary> BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger trigger = IdentityInspectionTrigger.Automatic, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default) =>
        BeginIdentityInspectionRoundCoreAsync(trigger, consent, requireDue: false, cancellationToken);

    public Task<IdentityInspectionRoundSummary> BeginDueIdentityInspectionRoundAsync(CancellationToken cancellationToken = default) =>
        BeginIdentityInspectionRoundCoreAsync(IdentityInspectionTrigger.Automatic, null, requireDue: true, cancellationToken);

    private async Task<IdentityInspectionRoundSummary> BeginIdentityInspectionRoundCoreAsync(IdentityInspectionTrigger trigger, IdentityInspectionConsent? consent, bool requireDue, CancellationToken cancellationToken)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null }) throw new ClientOperationException("IdentityInspectionBusy", "已有巡检轮次，不会排队。");
            if (!Enum.IsDefined(trigger)) throw new ClientOperationException("InvalidTrigger", "身份巡检触发类型无效。");
            var snapshot = IdentityView(state);
            if (trigger == IdentityInspectionTrigger.Automatic && !snapshot.Settings.Enabled) throw new ClientOperationException("IdentityInspectionDisabled", "身份巡检已关闭。");
            int interval = snapshot.Settings.IntervalMinutes;
            if (requireDue && snapshot.LastCompletedAt is { } completedAt && DateTimeOffset.UtcNow - completedAt < TimeSpan.FromMinutes(interval))
                throw new ClientOperationException("InspectionNotDue", "尚未到下一轮身份巡检时间。");
            RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
            if (state.Vault.History.Any(r => IsActive(r.State))) throw new ClientOperationException("ActiveRun", "已有活动或未知任务，请先恢复查询。");
            var targets = snapshot.Targets.Where(t => t.Selected && t.Authorized).Select(t => t.Target).Distinct().ToArray();
            if (trigger == IdentityInspectionTrigger.Manual)
                RequireExactIdentityConsent(consent, targets, interval, snapshot.SettingsRevision, "请确认本轮全部已授权目标向第三方披露。");
            if (targets.Length == 0) throw new ClientOperationException("IdentityNotAuthorized", "没有勾选、已缓存且已授权的身份目标。");
            var round = new IdentityInspectionRoundSummary
            {
                Trigger = trigger,
                ManualConsentAt = trigger == IdentityInspectionTrigger.Manual ? consent!.AcceptedAt : null,
                IntervalMinutes = interval,
                Attempts = targets.Select(t => new IdentityInspectionAttempt { Target = t }).ToArray()
            };
            state.Vault.LatestIdentityInspectionRound = round;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return round;
        }
        finally { _store.Gate.Release(); }
    }
    public async Task<IdentityInspectionStep> TrySubmitNextIdentityInspectionTargetAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); IdentityInspectionAttempt attempt;
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); var round = RequireIdentityRound(state, roundId);
            var waiting = round.Attempts.FirstOrDefault(a => a.State is IdentityInspectionAttemptState.IntentPersisted or IdentityInspectionAttemptState.Submitted);
            if (waiting is not null) return new(round, waiting.ProbeLocalId is Guid id ? state.Vault.History.Find(r => r.LocalId == id) : null, false, true, "AwaitingResult");
            if (!HasIdentityPending(round)) return new(round, null, false, false, round.StopCode);
            attempt = round.Attempts.First(a => a.State == IdentityInspectionAttemptState.Pending);
            try
            {
                if (attempt.Code is "IdentityTargetInvalidated" or "IdentityCacheInvalidated") throw new ClientOperationException("IdentityNotAuthorized", "本轮采用的目标已失效，未发送请求。");
                RequireIdentityTarget(state, attempt.Target, round);
                if (state.Vault.History.Any(r => IsActive(r.State))) throw new ClientOperationException("ActiveRun", "已有活动或未知任务。");
            }
            catch (ClientOperationException ex)
            {
                round = ReplaceIdentityAttempt(round, attempt with { State = IdentityInspectionAttemptState.Skipped, Code = ex.Code });
                if (ex.Code is "ActiveRun" or "ConnectionRateLimited" or "IdentityInspectionDisabled") round = round with { StopCode = round.StopCode ?? ex.Code };
                state.Vault.LatestIdentityInspectionRound = round; await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return new(round, null, HasIdentityPending(round), false, ex.Code);
            }
            attempt = attempt with { State = IdentityInspectionAttemptState.IntentPersisted };
            state.Vault.LatestIdentityInspectionRound = ReplaceIdentityAttempt(round, attempt);
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
        ProbeRecord? probe = null; string? code = null;
        try
        {
            var t = attempt.Target;
            var outbound = new OutboundConsent(t.ProfileId, t.KeyRevision, t.BaseUrl, OutboundPurpose.Probe, OutboundSecurity.ServiceOrigin + OutboundSecurity.StartPath, t.ModelId, DateTimeOffset.UtcNow, t.Protocol);
            probe = await StartProbeCoreAsync(t.ProfileId, t.ModelId, null, outbound, attempt.AttemptId, roundId, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientOperationException ex) { code = ex.Code; }
        catch (OperationCanceledException) { code = "Cancelled"; }
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false); var round = RequireIdentityRound(state, roundId);
            probe ??= state.Vault.History.Find(r => r.IdentityInspectionAttemptId == attempt.AttemptId);
            if (round.CompletedAt is not null)
            {
                var endedAttempt = round.Attempts.Single(a => a.AttemptId == attempt.AttemptId);
                round = ReplaceIdentityAttempt(round, endedAttempt with { ProbeLocalId = probe?.LocalId });
                state.Vault.LatestIdentityInspectionRound = round;
                await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                return new(round, probe, false, false, round.StopCode ?? "RoundEnded");
            }
            if (probe?.State == LocalRunState.Expired) code = "ProbeExpired";
            attempt = attempt with
            {
                ProbeLocalId = probe?.LocalId,
                State = probe is null ? IdentityInspectionAttemptState.Skipped :
                    IsIdentityConfirmedTerminal(probe) ? IdentityInspectionAttemptState.Completed :
                    probe.State == LocalRunState.Running ? IdentityInspectionAttemptState.Submitted : IdentityInspectionAttemptState.Unknown,
                Code = code
            };
            round = ReplaceIdentityAttempt(round, attempt);
            if (attempt.State == IdentityInspectionAttemptState.Unknown || code is "ActiveRun" or "SubmissionBusy" or "Http429" or "ConnectionRateLimited" or "IdentityInspectionDisabled" or "Cancelled")
                round = round with { StopCode = round.StopCode ?? code ?? "UnknownResult" };
            state.Vault.LatestIdentityInspectionRound = round; await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            return new(round, probe, HasIdentityPending(round), attempt.State == IdentityInspectionAttemptState.Submitted, code);
        }
        finally { _store.Gate.Release(); }
    }
    // Expired only confirms that querying is unavailable, not that the remote work ended.
    private static bool IsIdentityConfirmedTerminal(ProbeRecord probe) => OutboundSecurity.IsSafeRunId(probe.RemoteRunId) &&
        probe.State is LocalRunState.Completed or LocalRunState.Failed or LocalRunState.Aborted;

    private static bool IsIdentityNormalCompletion(IdentityInspectionRoundSummary round) =>
        !round.Cancelled && round.StopCode is null &&
        round.Attempts.Any(a => a.State == IdentityInspectionAttemptState.Completed) &&
        round.Attempts.All(a => a.State is IdentityInspectionAttemptState.Completed or IdentityInspectionAttemptState.Skipped);

    internal static DateTimeOffset? TrustedLegacyIdentityInspectionCompletedAt(Vault vault)
    {
        if (vault.LatestIdentityInspectionRound is not { CompletedAt: { } completedAt } round ||
            completedAt < round.StartedAt || completedAt > DateTimeOffset.UtcNow || !IsIdentityNormalCompletion(round) ||
            round.Attempts.Any(a => a.State == IdentityInspectionAttemptState.Skipped && a.Code == "RoundEnded"))
            return null;

        // Old clients could classify a locally failed pre-dispatch record as Completed. Require evidence of
        // at least one actual terminal remote run linked to this summary before seeding the independent clock.
        return round.Attempts.Any(a => a.State == IdentityInspectionAttemptState.Completed &&
            vault.History.Any(r => r.LocalId == a.ProbeLocalId && r.Source == ProbeRecordSource.IdentityInspection &&
                r.IdentityInspectionRoundId == round.RoundId && r.IdentityInspectionAttemptId == a.AttemptId &&
                a.Target == new IdentityInspectionTarget(r.ProfileId, r.KeyRevision, r.BaseUrl, r.Protocol, r.ModelId) &&
                IsIdentityConfirmedTerminal(r))) ? completedAt : null;
    }
    public async Task<IdentityInspectionStep> RecordIdentityInspectionResultAsync(Guid roundId, Guid probeLocalId, bool localPollingLimitReached = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); var round = RequireIdentityRound(state, roundId);
            var attempt = round.Attempts.FirstOrDefault(a => a.ProbeLocalId == probeLocalId) ?? throw new ClientOperationException("IdentityResultMismatch", "结果不属于当前轮次。");
            var probe = FindRecord(state, probeLocalId);
            if (probe.IdentityInspectionRoundId != roundId || probe.IdentityInspectionAttemptId != attempt.AttemptId || probe.Source != ProbeRecordSource.IdentityInspection) throw new ClientOperationException("IdentityResultMismatch", "结果来源不匹配。");
            if (round.CompletedAt is not null) return new(round, probe, false, false, round.StopCode ?? "RoundEnded");
            bool terminal = IsIdentityConfirmedTerminal(probe);
            if (probe.State == LocalRunState.Expired)
            {
                attempt = attempt with { State = IdentityInspectionAttemptState.Unknown, Code = "ProbeExpired" };
                round = round with { StopCode = round.StopCode ?? attempt.Code };
            }
            else if (terminal) attempt = attempt with { State = IdentityInspectionAttemptState.Completed };
            else if (localPollingLimitReached || probe.State != LocalRunState.Running)
            {
                attempt = attempt with { State = IdentityInspectionAttemptState.Unknown, Code = localPollingLimitReached ? "LocalPollingLimit" : "UnknownResult" };
                round = round with { StopCode = round.StopCode ?? attempt.Code };
            }
            round = ReplaceIdentityAttempt(round, attempt);
            state.Vault.LatestIdentityInspectionRound = round; await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new(round, probe, HasIdentityPending(round), !terminal && attempt.State == IdentityInspectionAttemptState.Submitted, attempt.Code);
        }
        finally { _store.Gate.Release(); }
    }
    public async Task<IdentityInspectionRoundSummary> EndIdentityInspectionRoundAsync(Guid roundId, bool cancelled = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); var round = RequireIdentityRound(state, roundId);
            if (round.CompletedAt is not null) return round;
            var completedAt = DateTimeOffset.UtcNow;
            round = round with { CompletedAt = completedAt, Cancelled = cancelled };
            // Decide before cleanup: converting Pending to Skipped when forcibly ending is not normal completion.
            if (IsIdentityNormalCompletion(round) && state.Vault.LastIdentityInspectionCompletedAt.GetValueOrDefault() < completedAt)
                state.Vault.LastIdentityInspectionCompletedAt = completedAt;
            round = round with { Attempts = round.Attempts.Select(a => a.State == IdentityInspectionAttemptState.Pending ? a with { State = IdentityInspectionAttemptState.Skipped, Code = "RoundEnded" } : a.State is IdentityInspectionAttemptState.IntentPersisted or IdentityInspectionAttemptState.Submitted ? a with { State = IdentityInspectionAttemptState.Unknown, Code = "RoundEnded" } : a).ToArray() };
            state.Vault.LatestIdentityInspectionRound = round; await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return round;
        }
        finally { _store.Gate.Release(); }
    }
    private async Task SaveProbeRateLimitAsync(TimeSpan? delay)
    {
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            string host = InspectionHost(OutboundSecurity.ServiceOrigin); var deadline = RetryDeadline(delay ?? TimeSpan.FromSeconds(60));
            if (deadline > state.Vault.InspectionHostDeadlines.GetValueOrDefault(host)) state.Vault.InspectionHostDeadlines[host] = deadline;
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null } round) state.Vault.LatestIdentityInspectionRound = round with { StopCode = round.StopCode ?? "Http429" };
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }
    private async Task PauseIdentityProfileAsync(ProbeRecord record)
    {
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            state.Vault.IdentityInspectionGrants = state.Vault.IdentityInspectionGrants.Select(g =>
                g.Target.ProfileId == record.ProfileId && g.Target.KeyRevision == record.KeyRevision && g.Target.BaseUrl == record.BaseUrl && g.Target.Protocol == record.Protocol
                    ? g with { Paused = true } : g).ToList();
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null } round && round.RoundId == record.IdentityInspectionRoundId)
                state.Vault.LatestIdentityInspectionRound = round with { PausedProfileIds = round.PausedProfileIds.Append(record.ProfileId).Distinct().ToArray() };
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }
}
