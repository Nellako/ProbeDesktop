namespace ProbeDesktop.Core;

internal sealed record IdentityInspectionGrant(IdentityInspectionTarget Target, DateTimeOffset AcceptedAt, int IntervalMinutes, bool Paused = false);

public sealed partial class DesktopService : IIdentityInspectionService
{
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
        return new(state.Vault.IdentityInspectionSettings, state.Profiles.Select(p => IdentitySelectionFor(state, p.Id)).ToArray(), state.Profiles.SelectMany(p => p.Models.Select(m =>
        {
            var t = IdentityTargetFor(p, m.Id); var grant = state.Vault.IdentityInspectionGrants.Find(g => g.Target == t);
            return new IdentityInspectionTargetStatus(t, sanitizer.Text(p.Name, 100), IdentitySelected(state, t), grant is { Paused: false } && grant.IntervalMinutes == state.Vault.IdentityInspectionSettings.IntervalMinutes, grant?.Paused ?? false);
        })).ToArray(), state.Vault.LatestIdentityInspectionRound);
    }
    public async Task<IdentityInspectionSnapshot> GetIdentityInspectionSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return IdentityView(await _store.LoadAsync(cancellationToken).ConfigureAwait(false)); }
        finally { _store.Gate.Release(); }
    }
    public async Task<IdentityInspectionSnapshot> SaveIdentityInspectionSelectionsAsync(IReadOnlyList<IdentityInspectionSelection> selections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections); ThrowIfDisposed();
        if (selections.Any(s => s is null || s.SelectedModelIds is null) || selections.Select(s => s.ProfileId).Distinct().Count() != selections.Count) throw new ClientOperationException("InvalidSelection", "身份巡检范围无效或含重复配置。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var s in selections)
            {
                var p = FindProfile(state, s.ProfileId);
                if (s.SelectedModelIds.Any(m => !p.Models.Any(c => c.Id == m))) throw new ClientOperationException("InvalidSelection", "只能选择当前缓存模型。");
            }
            // Configuration deselection preserves model choices and their exact grants. Model deselection revokes its grant.
            state.Vault.IdentityInspectionGrants.RemoveAll(g => !selections.Any(s => s.ProfileId == g.Target.ProfileId && s.SelectedModelIds.Contains(g.Target.ModelId, StringComparer.Ordinal)));
            state.Vault.IdentityInspectionSelections = selections.Select(s => s with { SelectedModelIds = s.SelectedModelIds.Distinct(StringComparer.Ordinal).ToArray() }).ToList();
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return IdentityView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private static void RequireIdentityInterval(int interval)
    {
        if (interval is < 10 or > 480) throw new ClientOperationException("InvalidInterval", "身份巡检间隔必须为 10 至 480 分钟的整数。");
    }
    private static void ApplyIdentityConsent(StoreState state, int interval, IdentityInspectionConsent? consent)
    {
        if (consent is null || consent.Scope is null || consent.IntervalMinutes != interval || consent.AcceptedAt > DateTimeOffset.UtcNow.AddMinutes(1) || consent.AcceptedAt < DateTimeOffset.UtcNow.AddMinutes(-10)) throw new ClientOperationException("ConsentRequired", "请确认全部目标的第三方身份披露与巡检间隔。");
        var selected = IdentityView(state).Targets.Where(t => t.Selected).Select(t => t.Target).ToArray();
        if (selected.Length == 0 || selected.Length != consent.Scope.Count || consent.Scope.Distinct().Count() != consent.Scope.Count || selected.Any(t => !consent.Scope.Contains(t))) throw new ClientOperationException("ConsentRequired", "确认范围须与当前勾选缓存目标精确一致，空范围不可运行。");
        state.Vault.IdentityInspectionGrants = selected.Select(t => new IdentityInspectionGrant(t, consent.AcceptedAt, interval)).ToList();
    }
    public async Task<IdentityInspectionSnapshot> SaveIdentityInspectionSettingsAsync(IdentityInspectionSettings settings, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings); ThrowIfDisposed(); RequireIdentityInterval(settings.IntervalMinutes);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings.Enabled)
            {
                // Restoring a previously approved switch is safe only while every selected target still has its exact grant.
                if (consent is not null) ApplyIdentityConsent(state, settings.IntervalMinutes, consent);
                else if (settings.IntervalMinutes != state.Vault.IdentityInspectionSettings.IntervalMinutes || !IdentityView(state).Targets.Any(t => t.Selected) || IdentityView(state).Targets.Any(t => t.Selected && !t.Authorized)) throw new ClientOperationException("ConsentRequired", "请重新确认身份巡检范围。");
            }
            else if (consent is not null) ApplyIdentityConsent(state, settings.IntervalMinutes, consent);
            else if (settings.IntervalMinutes != state.Vault.IdentityInspectionSettings.IntervalMinutes && state.Vault.IdentityInspectionSettings.Enabled) throw new ClientOperationException("ConsentRequired", "更改启用中的间隔需要重新确认。");
            state.Vault.IdentityInspectionSettings = settings;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return IdentityView(state);
        }
        finally { _store.Gate.Release(); }
    }
    public async Task<IdentityInspectionSnapshot> SaveIdentityInspectionIntervalAsync(int intervalMinutes, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); RequireIdentityInterval(intervalMinutes);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Vault.IdentityInspectionSettings.Enabled || consent is not null) ApplyIdentityConsent(state, intervalMinutes, consent);
            state.Vault.IdentityInspectionSettings = state.Vault.IdentityInspectionSettings with { IntervalMinutes = intervalMinutes };
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return IdentityView(state);
        }
        finally { _store.Gate.Release(); }
    }
    private static IdentityInspectionRoundSummary RequireIdentityRound(StoreState state, Guid id)
    {
        var round = state.Vault.LatestIdentityInspectionRound;
        if (round is null || round.RoundId != id) throw new ClientOperationException("IdentityRoundNotFound", "身份巡检轮次已不存在。");
        return round;
    }
    private static void RequireIdentityTarget(StoreState state, IdentityInspectionTarget target, IdentityInspectionRoundSummary round)
    {
        if (round.Trigger == IdentityInspectionTrigger.Automatic && !state.Vault.IdentityInspectionSettings.Enabled) throw new ClientOperationException("IdentityInspectionDisabled", "身份巡检已关闭。");
        if (round.PausedProfileIds.Contains(target.ProfileId)) throw new ClientOperationException("IdentityProfilePaused", "本轮配置因认证拒绝已暂停。");
        var profile = FindProfile(state, target.ProfileId);
        if (!IdentityProfileMatches(target, profile) || !profile.Models.Any(m => m.Id == target.ModelId) || !IdentitySelected(state, target) || (round.Trigger == IdentityInspectionTrigger.Automatic && !state.Vault.IdentityInspectionGrants.Any(g => g.Target == target && !g.Paused)) || (round.Trigger == IdentityInspectionTrigger.Manual && round.ManualConsentAt is null)) throw new ClientOperationException("IdentityNotAuthorized", "目标已变化、未勾选、未授权或已暂停。");
        var validation = ValidateConnection(state, profile.Id, target.ModelId);
        if (!validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
        RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
    }
    private static IdentityInspectionRoundSummary ReplaceIdentityAttempt(IdentityInspectionRoundSummary round, IdentityInspectionAttempt attempt) => round with { Attempts = round.Attempts.Select(a => a.AttemptId == attempt.AttemptId ? attempt : a).ToArray() };
    private static bool HasIdentityPending(IdentityInspectionRoundSummary round) => round.CompletedAt is null && round.StopCode is null && round.Attempts.Any(a => a.State == IdentityInspectionAttemptState.Pending);
    public async Task<IdentityInspectionRoundSummary> BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger trigger = IdentityInspectionTrigger.Automatic, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null }) throw new ClientOperationException("IdentityInspectionBusy", "已有巡检轮次，不会排队。");
            if (!Enum.IsDefined(trigger)) throw new ClientOperationException("InvalidTrigger", "身份巡检触发类型无效。");
            if (trigger == IdentityInspectionTrigger.Automatic && !state.Vault.IdentityInspectionSettings.Enabled) throw new ClientOperationException("IdentityInspectionDisabled", "身份巡检已关闭。");
            RequireHostDeadline(state, OutboundSecurity.ServiceOrigin);
            if (state.Vault.History.Any(r => IsActive(r.State))) throw new ClientOperationException("ActiveRun", "已有活动或未知任务，请先恢复查询。");
            var targets = IdentityView(state).Targets.Where(t => t.Selected && (trigger == IdentityInspectionTrigger.Manual || t.Authorized)).Select(t => t.Target).Distinct().ToArray();
            if (trigger == IdentityInspectionTrigger.Manual && (consent is null || consent.Scope is null || consent.Scope.Count != targets.Length || consent.Scope.Distinct().Count() != targets.Length || targets.Any(t => !consent.Scope.Contains(t)) || consent.AcceptedAt < DateTimeOffset.UtcNow.AddMinutes(-10) || consent.AcceptedAt > DateTimeOffset.UtcNow.AddMinutes(1))) throw new ClientOperationException("ConsentRequired", "请逐次确认本轮全部目标向官方第三方披露。");
            if (targets.Length == 0) throw new ClientOperationException("IdentityNotAuthorized", "没有勾选、已缓存且已授权的身份目标。");
            var round = new IdentityInspectionRoundSummary { Trigger = trigger, ManualConsentAt = trigger == IdentityInspectionTrigger.Manual ? consent!.AcceptedAt : null, Attempts = targets.Select(t => new IdentityInspectionAttempt { Target = t }).ToArray() };
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
                RequireIdentityTarget(state, attempt.Target, round);
                if (state.Vault.History.Any(r => IsActive(r.State))) throw new ClientOperationException("ActiveRun", "已有活动或未知任务。");
            }
            catch (ClientOperationException ex)
            {
                round = ReplaceIdentityAttempt(round, attempt with { State = IdentityInspectionAttemptState.Skipped, Code = ex.Code });
                if (ex.Code is "ActiveRun" or "ConnectionRateLimited" or "IdentityInspectionDisabled") round = round with { StopCode = ex.Code };
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
            attempt = attempt with { ProbeLocalId = probe?.LocalId, State = probe is null ? IdentityInspectionAttemptState.Skipped : IsIdentityConfirmedTerminal(probe.State) ? IdentityInspectionAttemptState.Completed : probe.State == LocalRunState.Running ? IdentityInspectionAttemptState.Submitted : IdentityInspectionAttemptState.Unknown, Code = code };
            round = ReplaceIdentityAttempt(round, attempt);
            if (attempt.State == IdentityInspectionAttemptState.Unknown || code is "ActiveRun" or "SubmissionBusy" or "Http429" or "ConnectionRateLimited" or "Cancelled") round = round with { StopCode = code ?? "UnknownResult" };
            state.Vault.LatestIdentityInspectionRound = round; await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            return new(round, probe, HasIdentityPending(round), attempt.State == IdentityInspectionAttemptState.Submitted, code);
        }
        finally { _store.Gate.Release(); }
    }
    // Expired only confirms that querying is unavailable, not that the remote work ended.
    private static bool IsIdentityConfirmedTerminal(LocalRunState state) => state is LocalRunState.Completed or LocalRunState.Failed or LocalRunState.Aborted;
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
            bool terminal = IsIdentityConfirmedTerminal(probe.State);
            if (probe.State == LocalRunState.Expired)
            {
                attempt = attempt with { State = IdentityInspectionAttemptState.Unknown, Code = "ProbeExpired" };
                round = round with { StopCode = attempt.Code };
            }
            else if (terminal) attempt = attempt with { State = IdentityInspectionAttemptState.Completed };
            else if (localPollingLimitReached || probe.State != LocalRunState.Running)
            {
                attempt = attempt with { State = IdentityInspectionAttemptState.Unknown, Code = localPollingLimitReached ? "LocalPollingLimit" : "UnknownResult" };
                round = round with { StopCode = attempt.Code };
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
            round = round with { CompletedAt = DateTimeOffset.UtcNow, Cancelled = cancelled, Attempts = round.Attempts.Select(a => a.State == IdentityInspectionAttemptState.Pending ? a with { State = IdentityInspectionAttemptState.Skipped, Code = "RoundEnded" } : a.State is IdentityInspectionAttemptState.IntentPersisted or IdentityInspectionAttemptState.Submitted ? a with { State = IdentityInspectionAttemptState.Unknown, Code = "RoundEnded" } : a).ToArray() };
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
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null } round) state.Vault.LatestIdentityInspectionRound = round with { StopCode = "Http429" };
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }
    private async Task PauseIdentityProfileAsync(Guid profileId)
    {
        await _store.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            state.Vault.IdentityInspectionGrants = state.Vault.IdentityInspectionGrants.Select(g => g.Target.ProfileId == profileId ? g with { Paused = true } : g).ToList();
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null } round)
                state.Vault.LatestIdentityInspectionRound = round with { PausedProfileIds = round.PausedProfileIds.Append(profileId).Distinct().ToArray() };
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _store.Gate.Release(); }
    }
}
