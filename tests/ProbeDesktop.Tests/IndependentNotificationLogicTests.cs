using ProbeDesktop.App.Logic;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Independent verification of the pure automatic-observation mapping (src/ProbeDesktop.App/Logic/ProblemObservations.cs).
/// No WPF, no network, no Core service: only record -> observation mapping rules.
/// Contract: docs/decisions-identity-inspection.md and the class-level comment of ProblemObservations.
/// Capability observation logic was removed together with the capability module; identity observations now come
/// only from identity-inspection Probe records.
/// </summary>
public static class IndependentNotificationLogicTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string BaseUrl = "https://relay.example.com/v1";
    private const string ModelId = "model-a";

    private static InspectionSnapshot InspectionSnapshotOf(int keyRevision = 1, ApiProtocol protocol = ApiProtocol.ChatCompletions,
        string baseUrl = BaseUrl)
    {
        InspectionTarget target = new(ProfileId, keyRevision, baseUrl, protocol, [ModelId]);
        return new InspectionSnapshot(
            new InspectionSettings(),
            [new InspectionTargetStatus(target, "P", InspectionAuthorizationState.Authorized, DateTimeOffset.UtcNow, null)],
            null);
    }

    private static ConnectionTestResult Connection(ConnectionTestStage stage, string code = "", int? http = null,
        int keyRevision = 1, ApiProtocol protocol = ApiProtocol.ChatCompletions, Guid? roundId = null, DateTimeOffset? completedAt = null,
        string? baseUrl = null, ConnectionRecordSource source = ConnectionRecordSource.Automatic, string? modelId = null,
        bool inFlight = false, DateTimeOffset? startedAt = null)
        => new()
        {
            ProfileId = ProfileId,
            ProfileName = "P",
            KeyRevision = keyRevision,
            BaseUrl = baseUrl ?? BaseUrl,
            Protocol = protocol,
            Source = source,
            ModelId = modelId ?? ModelId,
            Stage = stage,
            Code = code,
            HttpStatusCode = http,
            RoundId = roundId,
            StartedAt = startedAt ?? completedAt ?? DateTimeOffset.UtcNow,
            CompletedAt = inFlight ? null : completedAt ?? DateTimeOffset.UtcNow,
        };

    private static ProbeRecord AutomaticProbe(LocalRunState state, string? verdict = null, string? identityStatus = null,
        string? remoteRunId = "run-1", int keyRevision = 1, ProbeRecordSource source = ProbeRecordSource.IdentityInspection,
        string? baseUrl = null, ApiProtocol protocol = ApiProtocol.ChatCompletions, bool warning = false,
        IdentityInspectionTrigger? trigger = IdentityInspectionTrigger.Automatic)
        => new()
        {
            Source = source,
            IdentityInspectionTrigger = trigger,
            ProfileId = ProfileId,
            ProfileName = "P",
            KeyRevision = keyRevision,
            BaseUrl = baseUrl ?? BaseUrl,
            Protocol = protocol,
            ModelId = ModelId,
            State = state,
            VerdictStatus = verdict,
            IdentityStatus = identityStatus,
            RemoteRunId = remoteRunId,
            Warnings = warning ? [new ProbeWarning("latency", "medium", "slow")] : [],
            CompletedAt = state == LocalRunState.Completed ? DateTimeOffset.UtcNow : null,
        };

    // N15 -------------------------------------------------------------------
    // Problem codes are short, stable, non-sensitive tokens: nothing from a response body can leak into a code.
    public static async Task N15_SafeProblemCodes()
    {
        await Task.Yield();

        Check.Equal("abc123_-", ProblemObservations.SafeCode("abc123_-", "fb"), "letters, digits, underscore and dash survive");
        Check.Equal("abc123", ProblemObservations.SafeCode("abc 123", "fb"), "spaces and punctuation are stripped");
        Check.Equal("httpevilcom", ProblemObservations.SafeCode("http://evil.com/", "fb"), "URL punctuation and slash are stripped");
        Check.Equal("fallback", ProblemObservations.SafeCode("密钥错误", "fallback"), "non-ASCII text cannot become a code");
        Check.Equal("fallback", ProblemObservations.SafeCode("!!!", "fallback"), "all-filtered input uses the fallback");
        Check.Equal("fallback", ProblemObservations.SafeCode(null, "fallback"), "null uses the fallback");
        Check.Equal("fallback", ProblemObservations.SafeCode("", "fallback"), "empty uses the fallback");
        Check.Equal(ProblemObservations.MaxCodeLength,
            ProblemObservations.SafeCode(new string('a', 200), "fb").Length, "code length is capped");
        Check.Equal(string.Concat(Enumerable.Repeat("ab", 16)),
            ProblemObservations.SafeCode(string.Concat(Enumerable.Repeat("ab", 100)), "fb"), "cap keeps the first 32 characters");
    }

    // N16 -------------------------------------------------------------------
    // Connection classes map to four disjoint outcomes; only a real model response may clear an earlier problem.
    public static async Task N16_ConnectionObservationClasses()
    {
        await Task.Yield();

        var success = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.ModelResponded));
        Check.Equal(NotificationObservationState.Healthy, success.State, "confirmed model response is healthy");
        Check.Null(success.Problem, "healthy carries no problem code");

        var failedHttp = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.Failed, http: 429));
        Check.Equal(NotificationObservationState.Problem, failedHttp.State, "HTTP failure is a problem");
        Check.Equal("http-429", failedHttp.Problem, "HTTP status becomes a stable code, not a message");

        var failedCode = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.Failed, code: "Timeout/Abort"));
        Check.Equal("failed-TimeoutAbort", failedCode.Problem, "non-HTTP failure keeps its sanitized code");

        var failedEmpty = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.Failed));
        Check.Equal("failed-error", failedEmpty.Problem, "missing code falls back to failed-error");

        var possible = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.PossibleSuccess));
        Check.Equal(NotificationObservationState.Problem, possible.State, "possible success is reported, not silently healthy");
        Check.Equal(ProblemObservations.PossibleSuccess, possible.Problem, "possible success has its own code");

        var unknown = ProblemObservations.ClassifyConnection(Connection(ConnectionTestStage.Unknown));
        Check.Equal(NotificationObservationState.Problem, unknown.State, "unknown result is a stable problem");
        Check.Equal(ProblemObservations.UnknownResult, unknown.Problem, "unknown result has its own code");

        foreach (ConnectionTestStage middle in new[] { ConnectionTestStage.LocalValidationPassed, ConnectionTestStage.ApiReached })
        {
            var incomplete = ProblemObservations.ClassifyConnection(Connection(middle));
            Check.Equal(NotificationObservationState.Unknown, incomplete.State, middle + ": in-flight stage is not final");
            Check.Null(incomplete.Problem, middle + ": in-flight stage produces no problem code");
        }

        // A possible success or an unknown must never be treated as a recovery (Healthy).
        Check.True(problem(possible) && problem(unknown) && problem(failedHttp), "possible/unknown/failed all stay problems");
        static bool problem((NotificationObservationState State, string? Problem) value) => value.State == NotificationObservationState.Problem;
    }

    // N17 -------------------------------------------------------------------
    // Identity Probe records: only a real match clears; warnings/mismatch/spoof are problems; ambiguity is unknown.
    public static async Task N17_IdentityObservationClasses()
    {
        await Task.Yield();

        var clean = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, "clean_match"), waiting: false);
        Check.True(clean.Final, "completed is final");
        Check.Equal(NotificationObservationState.Healthy, clean.State, "clean match is the only healthy identity result");
        Check.Null(clean.Problem, "clean match has no problem code");

        // A clean_match whose report carries warning items is NOT a recovery: it is identity-warning.
        var warning = ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.Completed, "clean_match", warning: true), waiting: false);
        Check.True(warning.Final, "a warned completed run is final");
        Check.Equal(NotificationObservationState.Problem, warning.State, "clean_match with warnings is not healthy");
        Check.Equal(ProblemObservations.IdentityWarning, warning.Problem, "clean_match with warnings uses identity-warning");

        var warnedLegacy = ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.Completed, null, "match", warning: true), waiting: false);
        Check.Equal(ProblemObservations.IdentityWarning, warnedLegacy.Problem, "legacy match with warnings is identity-warning");

        // Mismatch and spoof outrank the warning code.
        Check.Equal("mismatch", ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch", warning: true), waiting: false).Problem,
            "mismatch outranks warnings");
        Check.Equal("spoof-suspected", ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.Completed, "spoof_suspected", warning: true), waiting: false).Problem,
            "spoof outranks warnings");
        Check.Equal(ProblemObservations.IdentityWarning, ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.Completed, "", warning: true), waiting: false).Problem,
            "a warning also outranks the unknown-verdict fallback");
        Check.Equal("鉴别报告含警示项",
            ProblemObservations.Describe(NotificationCategory.Identity, ProblemObservations.IdentityWarning),
            "warning text is fixed and non-sensitive");

        foreach (string verdict in new[] { "plain_mismatch", "clean_match_submodel_mismatch" })
        {
            var mismatch = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, verdict), waiting: false);
            Check.Equal(NotificationObservationState.Problem, mismatch.State, verdict + ": is a problem");
            Check.Equal("mismatch", mismatch.Problem, verdict + ": uses the mismatch code");
        }

        var spoof = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, "spoof_suspected"), waiting: false);
        Check.Equal("spoof-suspected", spoof.Problem, "spoof verdict is flagged");

        foreach (string? verdict in new[] { null, "", "unclear", "clean_match_partial" })
        {
            var uncertain = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, verdict), waiting: false);
            Check.Equal(NotificationObservationState.Problem, uncertain.State, "verdict <" + (verdict ?? "null") + "> is not a recovery");
            Check.Equal(ProblemObservations.UnknownResult, uncertain.Problem, "ambiguous verdict is unknown-result");
        }

        var legacyMatch = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, null, "match"), waiting: false);
        Check.Equal(NotificationObservationState.Healthy, legacyMatch.State, "legacy top-level match with no verdict is still a match");
        var legacyMismatch = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Completed, null, "mismatch"), waiting: false);
        Check.Equal("mismatch", legacyMismatch.Problem, "legacy mismatch is reported");

        foreach (LocalRunState state in new[] { LocalRunState.Failed, LocalRunState.Aborted })
        {
            var terminal = ProblemObservations.ClassifyProbe(AutomaticProbe(state), waiting: false);
            Check.True(terminal.Final, state + ": is final");
            Check.Equal(NotificationObservationState.Problem, terminal.State, state + ": is a problem");
        }

        var expired = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Expired), waiting: false);
        Check.Equal(ProblemObservations.UnknownResult, expired.Problem, "expired run is unknown, not a mismatch claim");

        var submitting = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Submitting), waiting: true);
        Check.False(submitting.Final, "submitting is not final and must not notify");
        Check.Equal(NotificationObservationState.Unknown, submitting.State, "submitting is unknown");

        var waitingRunning = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Running), waiting: true);
        Check.False(waitingRunning.Final, "a pollable run that is still being polled is not final");

        var stoppedRunning = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Running), waiting: false);
        Check.True(stoppedRunning.Final, "a pollable run whose polling stopped becomes final");
        Check.Equal(ProblemObservations.UnknownResult, stoppedRunning.Problem, "stopped polling is unknown");

        var noRunId = ProblemObservations.ClassifyProbe(AutomaticProbe(LocalRunState.Running, remoteRunId: null), waiting: true);
        Check.True(noRunId.Final, "no remote id can never be polled, so it is final");
        Check.Equal(NotificationObservationState.Problem, noRunId.State, "unpollable run is a problem");

        var interrupted = ProblemObservations.ClassifyProbe(
            AutomaticProbe(LocalRunState.PollingInterrupted, remoteRunId: null), waiting: true);
        Check.True(interrupted.Final, "interrupted without a run id is final");
        Check.Equal(ProblemObservations.UnknownResult, interrupted.Problem, "interrupted without a run id is unknown");
    }

    // N18 -------------------------------------------------------------------
    // Only identity-inspection automatic records produce identity observations; target fields are exact.
    public static async Task N18_AutomaticIdentityFiltering()
    {
        await Task.Yield();

        Check.Null(ProblemObservations.FromAutomaticProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch", source: ProbeRecordSource.Manual), false),
            "manual probe is not an automatic identity observation");
        Check.Null(ProblemObservations.FromAutomaticProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch", trigger: IdentityInspectionTrigger.Manual), false),
            "manual identity round is not an automatic identity observation");
        Check.Null(ProblemObservations.FromAutomaticProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch", source: ProbeRecordSource.CapabilityAutomatic), false),
            "legacy capability-automatic history never becomes an identity observation");

        Check.False(ProblemObservations.IsAutomaticIdentity(
            AutomaticProbe(LocalRunState.Completed, "clean_match", source: ProbeRecordSource.CapabilityAutomatic)),
            "legacy capability source is not an automatic identity record");
        Check.True(ProblemObservations.IsAutomaticIdentity(
            AutomaticProbe(LocalRunState.Running)), "identity-inspection automatic record is recognized");

        ObservedItem? mapped = ProblemObservations.FromAutomaticProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch"), false);
        Check.NotNull(mapped, "a current automatic mismatch is reported");
        Check.Equal(NotificationCategory.Identity, mapped!.Observation.Category, "identity category");
        Check.True(mapped.Observation.Automatic, "observation is marked automatic");
        Check.Equal(new NotificationTarget(ProfileId, 1, BaseUrl, ApiProtocol.ChatCompletions, ModelId),
            mapped.Observation.Target, "target is taken from the record's exact fields");
        Check.Contains(mapped.Label, "model-a", "label names the model");
        Check.Null(mapped.Hint, "identity observations carry no hint");

        // A record whose credential revision/address/protocol differs is still reported against its own exact
        // target; Core re-validates it and rejects a stale target instead of re-binding it to the new config.
        ObservedItem? stale = ProblemObservations.FromAutomaticProbe(
            AutomaticProbe(LocalRunState.Completed, "plain_mismatch", keyRevision: 2, baseUrl: "https://old.example.com/v1",
                protocol: ApiProtocol.AnthropicMessages), false);
        Check.NotNull(stale, "mapping is purely record-driven");
        Check.Equal(2, stale!.Observation.Target.KeyRevision, "record's own key revision is preserved");
        Check.Equal("https://old.example.com/v1", stale.Observation.Target.BaseUrl, "record's own address is preserved");
        Check.Equal(ApiProtocol.AnthropicMessages, stale.Observation.Target.Protocol, "record's own protocol is preserved");
        Check.NotContains(stale.Label, "https://", "label never contains an address");
    }

    // N19 -------------------------------------------------------------------
    // Round mapping: exact round id, automatic only, completed only, exact key revision/address/protocol.
    public static async Task N19_RoundMappingAndComposing()
    {
        await Task.Yield();

        Guid round = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        // Distinct models are distinct exact targets; the 503 target completes later so it stays first in the result.
        DateTimeOffset t0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var connectionItems = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: t0.AddSeconds(10)),
            Connection(ConnectionTestStage.ModelResponded, roundId: other),
            Connection(ConnectionTestStage.Failed, roundId: round, keyRevision: 9),
            Connection(ConnectionTestStage.Failed, roundId: round, protocol: ApiProtocol.AnthropicMessages),
            Connection(ConnectionTestStage.ApiReached, roundId: round, modelId: "model-b", completedAt: t0),
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, source: ConnectionRecordSource.Manual),
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, source: ConnectionRecordSource.RoundManual),
        ], InspectionSnapshotOf());
        Check.Equal(2, connectionItems.Count, "only automatic records of the same round/revision/protocol are mapped");
        Check.True(connectionItems.All(i => i.Observation.ProblemType != "http-500"),
            "manual and immediate-round connection records in the same round are skipped");
        Check.Equal(NotificationCategory.Connection, connectionItems[0].Observation.Category, "connection category");
        Check.Equal("http-503", connectionItems[0].Observation.ProblemType, "problem code preserved");
        Check.Equal(NotificationObservationState.Unknown, connectionItems[1].Observation.State, "in-flight connection is unknown");
        Check.Contains(connectionItems[0].Label, "P", "label uses the current profile name");

        // ---- exact Base URL matching: a record taken against the old address is never re-bound ----
        const string OldBase = "https://old-relay.example.com/v1";
        Check.Equal(0, ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, baseUrl: OldBase),
        ], InspectionSnapshotOf()).Count, "connection: same profile/key revision but different Base URL is not observed");

        Check.Equal(1, ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round),
        ], InspectionSnapshotOf()).Count, "connection: exact current address is still observed");

        Check.Equal("P / model-a：请求失败（HTTP 503）",
            ProblemObservations.Compose(NotificationCategory.Connection, [("P / model-a", "http-503", null)]),
            "single problem is rendered with label and fixed description");
        Check.Contains(ProblemObservations.Compose(NotificationCategory.Identity, [
            ("A / m1", "mismatch", null),
            ("B / m2", "mismatch", null),
        ]), "共 2 个目标", "multiple targets are merged into one text");
        string many = ProblemObservations.Compose(NotificationCategory.Identity, [
            ("A / 1", "mismatch", null), ("B / 2", "mismatch", null),
            ("C / 3", "spoof-suspected", null), ("D / 4", "unknown-result", null),
        ]);
        Check.Contains(many, "共 4 个目标", "count is stated for more than three targets");
        Check.Contains(many, "等", "only the first three are listed");
        Check.NotContains(many, "D / 4", "the fourth target is not listed");
        Check.NotContains(many, BaseUrl, "no address appears in a notification");

        Check.Equal("连接巡检发现问题", ProblemObservations.Title(NotificationCategory.Connection), "connection title");
        Check.Equal("身份识别巡检发现问题", ProblemObservations.Title(NotificationCategory.Identity), "identity title");
        Check.Equal("鉴权失败（HTTP 401），该配置已暂停",
            ProblemObservations.Describe(NotificationCategory.Connection, "http-401"), "401 text is fixed and non-sensitive");
        Check.Equal("鉴别结果未知：查询已暂停或被限流，或结果不完整；远端任务不视为已取消",
            ProblemObservations.Describe(NotificationCategory.Identity, ProblemObservations.UnknownResult),
            "identity unknown text is fixed and states the remote task is not treated as cancelled");
        Check.Equal("出现问题", ProblemObservations.Describe(NotificationCategory.Identity, null), "missing code has a neutral text");
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    // N20 -------------------------------------------------------------------
    // Connection retries: up to 3 attempts per exact target in one round; notifications use only the final completed attempt.
    public static async Task N20_RetryFinalAttemptNotifications()
    {
        await Task.Yield();
        Guid round = Guid.NewGuid();

        // Failed first, then green: no problem is reported and the target is Healthy (can clear an older problem).
        var recovered = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.ModelResponded, roundId: round, completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
        ], InspectionSnapshotOf());
        Check.Equal(1, recovered.Count, "two attempts of one target produce one observation");
        Check.Equal(NotificationObservationState.Healthy, recovered[0].Observation.State, "fail then success is healthy");
        Check.Null(recovered[0].Observation.ProblemType, "fail then success carries no problem type");

        // Input order must not matter (history is newest first, but selection is by completion time).
        var oldestFirst = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, completedAt: T0.AddSeconds(30)),
        ], InspectionSnapshotOf());
        Check.Equal(1, oldestFirst.Count, "three attempts of one target produce one observation");
        Check.Equal(NotificationObservationState.Healthy, oldestFirst[0].Observation.State, "third attempt success is healthy");

        // Exhausted: only the last attempt's own classification is reported; earlier codes never leak.
        var exhausted = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.PossibleSuccess, roundId: round, completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(30)),
        ], InspectionSnapshotOf());
        Check.Equal(1, exhausted.Count, "exhausted retries produce one observation");
        Check.Equal("http-503", exhausted[0].Observation.ProblemType, "only the final failure is reported");

        // The original classification of the last attempt is preserved, not forced into a failure.
        var possible = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.PossibleSuccess, roundId: round, completedAt: T0.AddSeconds(20)),
        ], InspectionSnapshotOf());
        Check.Equal(ProblemObservations.PossibleSuccess, possible[0].Observation.ProblemType, "final possible success stays possible success");
        var unknown = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 500, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.Unknown, roundId: round, completedAt: T0.AddSeconds(20)),
        ], InspectionSnapshotOf());
        Check.Equal(ProblemObservations.UnknownResult, unknown[0].Observation.ProblemType, "final unknown stays unknown");

        // 401 stops retries: a single final record is reported with its own code.
        var auth = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 401, roundId: round, completedAt: T0),
        ], InspectionSnapshotOf());
        Check.Equal("http-401", auth[0].Observation.ProblemType, "paused-config failure is reported once");

        // Targets are independent: another model keeps its own final result.
        var twoTargets = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.Failed, http: 429, roundId: round, modelId: "model-b", completedAt: T0.AddSeconds(15)),
        ], InspectionSnapshotOf());
        Check.Equal(2, twoTargets.Count, "two exact targets give two observations");
        Check.Equal(NotificationObservationState.Healthy, twoTargets.Single(i => i.Observation.Target.ModelId == ModelId).Observation.State, "model-a recovered");
        Check.Equal("http-429", twoTargets.Single(i => i.Observation.Target.ModelId == "model-b").Observation.ProblemType, "model-b keeps its own final problem");

        // A later attempt against a different credential revision is a different exact target and is never merged.
        var revisions = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, keyRevision: 2, completedAt: T0.AddSeconds(20)),
        ], InspectionSnapshotOf());
        Check.Equal(1, revisions.Count, "the stale revision target is dropped, not replaced by the other one");
        Check.Equal("http-503", revisions[0].Observation.ProblemType, "current revision keeps its own result");

        // Manual / immediate-round records never count as a retry result for the automatic target.
        var manualLater = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, completedAt: T0.AddSeconds(20), source: ConnectionRecordSource.Manual),
        ], InspectionSnapshotOf());
        Check.Equal("http-503", manualLater[0].Observation.ProblemType, "manual success does not mask an automatic failure");

        // An in-flight attempt is not a result: the earlier completed failure is not replaced by it either.
        var inFlight = ProblemObservations.FromConnectionRound(round, [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ApiReached, roundId: round, inFlight: true, startedAt: T0.AddSeconds(20)),
        ], InspectionSnapshotOf());
        Check.Equal(1, inFlight.Count, "unfinished attempts are ignored");
        Check.Equal("http-503", inFlight[0].Observation.ProblemType, "only completed attempts are considered");
    }

    // N21 -------------------------------------------------------------------
    // Latest-round display: one row per exact target = its last attempt; full history is untouched.
    public static async Task N21_RoundDisplayShowsFinalAttemptPerTarget()
    {
        await Task.Yield();
        Guid round = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Check.Equal(3, ConnectionRoundDisplay.MaxAttempts, "attempt ceiling is three");

        List<ConnectionTestResult> history =
        [
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.PossibleSuccess, roundId: round, completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, completedAt: T0.AddSeconds(30)),
            Connection(ConnectionTestStage.Failed, http: 500, roundId: other, completedAt: T0.AddSeconds(40)),
        ];
        var rows = ConnectionRoundDisplay.LatestPerTarget(history, round);
        Check.Equal(1, rows.Count, "three attempts show as one row and other rounds are excluded");
        Check.Equal(ConnectionTestStage.ModelResponded, rows[0].Record.Stage, "the row shows the last attempt");
        Check.Equal(3, rows[0].Attempts, "attempt count is reported");
        Check.Contains(ConnectionRoundDisplay.AttemptNote(rows[0]), "共尝试 3 次", "completed multi-attempt note");
        Check.Equal(4, history.Count, "full connection history is not modified");

        // Only one attempt: no note.
        var single = ConnectionRoundDisplay.LatestPerTarget([
            Connection(ConnectionTestStage.Failed, http: 401, roundId: round, completedAt: T0),
        ], round);
        Check.Equal("", ConnectionRoundDisplay.AttemptNote(single[0]), "a single attempt needs no note");

        // A running retry is shown as running, never replaced by the earlier failure.
        var running = ConnectionRoundDisplay.LatestPerTarget([
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ApiReached, roundId: round, inFlight: true, startedAt: T0.AddSeconds(20)),
        ], round);
        Check.Equal(1, running.Count, "one row for a retrying target");
        Check.True(running[0].Record.CompletedAt is null, "the unfinished latest attempt is the one displayed");
        Check.Equal(2, running[0].Attempts, "running retry counts as attempt two");
        Check.Contains(ConnectionRoundDisplay.AttemptNote(running[0]), "第 2 次尝试进行中", "running retry note");

        // Different models / revisions are different rows, newest start first.
        var multi = ConnectionRoundDisplay.LatestPerTarget([
            Connection(ConnectionTestStage.Failed, http: 503, roundId: round, completedAt: T0.AddSeconds(10)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, modelId: "model-b", completedAt: T0.AddSeconds(20)),
            Connection(ConnectionTestStage.ModelResponded, roundId: round, keyRevision: 2, completedAt: T0.AddSeconds(30)),
        ], round);
        Check.Equal(3, multi.Count, "model and credential revision each form their own target");
        Check.Equal(2, multi[0].Record.KeyRevision, "rows are ordered by start time, newest first");
    }

    // N22 -------------------------------------------------------------------
    // Confirmation text discloses the real behavior: up to 3 attempts, 3x request/cost estimate, extra-billing caveat, exceptions.
    public static async Task N22_ScopeTextDisclosesRetries()
    {
        await Task.Yield();
        InspectionTarget target = new(ProfileId, 1, BaseUrl, ApiProtocol.ChatCompletions, ["model-a", "model-b"]);
        string text = ConnectionScopeText.Build([target], 30, _ => "P");

        Check.Contains(text, "最多 3 次", "states the per-model per-round attempt ceiling");
        Check.Contains(text, "首次加最多 2 次重试", "states how many retries follow the first attempt");
        Check.Contains(text, "约 4 次请求", "best-case request estimate stays one attempt per model");
        Check.Contains(text, "约 12 次请求", "worst-case request estimate is three times the single-attempt volume");
        Check.Contains(text, "请求次数的 3 倍", "states the 3x request-count multiplier");
        Check.Contains(text, "可能增加费用", "states that up to 3 requests may raise the cost");
        Check.Contains(text, "按中转站计价", "states that the actual cost follows the relay's pricing");
        Check.NotContains(text, "费用估算也按", "no 3x cost-estimate promise");
        Check.NotContains(text, "费用上限为", "no cost-ceiling promise");
        Check.Contains(text, "额外计费", "warns that repeating after an unknown result may bill again");
        Check.Contains(text, "401/403", "401/403 exception is disclosed");
        Check.Contains(text, "429", "429 exception is disclosed");
        Check.Contains(text, "最后一次尝试", "states that statistics and notifications use the final attempt");
        Check.Contains(text, "手动单次连接测试不自动重试", "manual single test is explicitly excluded");
        Check.Contains(text, "身份识别巡检另行确认且不自动重试", "identity inspection is explicitly excluded");
        Check.Contains(text, "POST https://relay.example.com/v1/chat/completions", "the exact receiving endpoint is still listed");
        Check.Contains(text, "model-b", "every selected model is still listed");
        Check.NotContains(text, "每个模型每轮直连发送一次短请求", "the old one-request-per-round wording is gone");
    }
}
