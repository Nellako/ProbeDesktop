using System.Net;
using System.Text;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Contract tests for the identity-inspection module (IIdentityInspectionService) that replaced the capability
/// module. Fake HTTP only: no socket is ever opened and no real credential is used.
/// Scope under test: src/ProbeDesktop.Core/IdentityInspectionContracts.cs, IdentityInspectionService.cs,
/// docs/decisions-identity-inspection.md.
///
/// Host model: Core owns no timer and no GET. The host (App) calls TrySubmitNext, then polls the persisted Probe
/// record through its shared coordinator, then calls RecordIdentityInspectionResult. These tests play that host
/// role with the public DesktopService.PollProbeAsync as the only GET path.
/// </summary>
public static class IdentityInspectionTests
{
    private const string Key = Program.Key1;
    private const string Key2 = Program.Key2;
    private const string RelayBase = "https://relay.example.com/v1";
    private const string Relay2 = "https://relay2.example.com/v1";
    private const string ServiceHost = "bazaarlink.ai";
    private const string StartPath = "/api/probe/run";

    private static DesktopService New(string dir, HttpMessageHandler handler, ISecretProtector? protector = null)
        => new(dir, new HttpClient(handler, disposeHandler: false), protector ?? new ReversibleTestProtector());

    private static OutboundConsent ModelConsent(RelayProfile profile)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow, profile.Protocol);

    private static string Header(RecordedRequest request, string name)
        => request.Headers.TryGetValue(name, out string[]? value) ? string.Join(",", value) : "";

    private static async Task<RelayProfile> SeedAsync(DesktopService service, FakeHttpHandler handler, string name,
        string baseUrl, string[] models, ApiProtocol protocol = ApiProtocol.ChatCompletions, string key = Key)
    {
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson(models));
        RelayProfile profile = await service.SaveProfileAsync(
            Fixtures.NewProfile(name, baseUrl) with { Protocol = protocol, DefaultModelId = "" }, key);
        return await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
    }

    private static IdentityInspectionConsent ConsentFor(IdentityInspectionSnapshot snapshot, int interval)
        => new(snapshot.Targets.Where(t => t.Selected).Select(t => t.Target).ToArray(), interval, DateTimeOffset.UtcNow);

    /// <summary>Selects every cached model of every profile, then enables with a fresh exact-scope consent.</summary>
    private static async Task<IdentityInspectionSnapshot> EnableAsync(DesktopService service, int interval = 30)
    {
        IdentityInspectionSnapshot snapshot = await SelectAllAsync(service);
        return await service.SaveIdentityInspectionSettingsAsync(
            new IdentityInspectionSettings { Enabled = true, IntervalMinutes = interval }, ConsentFor(snapshot, interval));
    }

    private static async Task<IdentityInspectionSnapshot> SelectAllAsync(DesktopService service)
    {
        IdentityInspectionSnapshot snapshot = await service.GetIdentityInspectionSnapshotAsync();
        // Targets are one entry per cached model; selection is per profile with its model id list.
        return await service.SaveIdentityInspectionSelectionsAsync(snapshot.Targets
            .GroupBy(t => t.Target.ProfileId)
            .Select(g => new IdentityInspectionSelection(g.Key, g.Any(), g.Select(t => t.Target.ModelId).ToArray()))
            .ToArray());
    }

    private static string RunIdOnly(string runId) => $"{{\"runId\":\"{runId}\"}}";

    /// <summary>Completed payload with a valid item and a clean identity verdict (adapter requires both).</summary>
    private static string CompletedJson(string runId, string verdict = "clean_match")
        => $$$"""
        {"runId":"{{{runId}}}","status":"completed",
         "identityAssessment":{"status":"match","verdict":{"status":"{{{verdict}}}","trueFamily":"gpt","trueModel":"gpt-4o"}},
         "items":[{"probeId":"p1","status":"done","passed":true}],
         "completedAt":1700000000000}
        """;

    private static int ProbePosts(FakeHttpHandler handler)
        => handler.Requests.Count(r => r.Method == "POST" && r.Uri.Host.Equals(ServiceHost, StringComparison.OrdinalIgnoreCase)
            && r.Uri.AbsolutePath == StartPath);

    private static int ProbeGets(FakeHttpHandler handler)
        => handler.Requests.Count(r => r.Method == "GET" && r.Uri.Host.Equals(ServiceHost, StringComparison.OrdinalIgnoreCase));

    private static string RequestModel(RecordedRequest request)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(request.Body);
        return document.RootElement.TryGetProperty("modelId", out System.Text.Json.JsonElement model) ? model.GetString() ?? "" : "";
    }

    private static string RequestBaseUrl(RecordedRequest request)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(request.Body);
        return document.RootElement.TryGetProperty("baseUrl", out System.Text.Json.JsonElement value) ? value.GetString() ?? "" : "";
    }

    // V01 -------------------------------------------------------------------
    // Defaults: disabled, 30 minutes, no selections, nothing authorized, no timer, no requests.
    public static async Task V01_DefaultsAndNoAutomaticRun()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);

        IdentityInspectionSnapshot blank = await service.GetIdentityInspectionSnapshotAsync();
        Check.False(blank.Settings.Enabled, "identity inspection defaults to disabled");
        Check.Equal(30, blank.Settings.IntervalMinutes, "identity inspection default interval is 30 minutes");
        Check.Equal(0, blank.Targets.Count, "no targets before any profile exists");
        Check.Null(blank.LatestRound, "no round recorded yet");

        RelayProfile cached = await SeedAsync(service, handler, "Primary", RelayBase, ["m1", "m2"]);
        RelayProfile noCache = await service.SaveProfileAsync(Fixtures.NewProfile("Empty", Relay2), Key2);

        IdentityInspectionSnapshot snapshot = await service.GetIdentityInspectionSnapshotAsync();
        Check.False(snapshot.Settings.Enabled, "caching models never enables inspection");
        Check.Equal(2, snapshot.Targets.Count, "targets are one entry per cached model; an empty cache adds none");
        IdentityInspectionTargetStatus[] withModels = snapshot.Targets.Where(t => t.Target.ProfileId == cached.Id).ToArray();
        Check.Equal(2, withModels.Length, "the cached profile exposes one target per cached model");
        Check.True(withModels.All(t => !t.Selected), "nothing is selected before the user picks models");
        Check.True(withModels.All(t => !t.Authorized), "nothing is authorized before explicit consent");
        Check.True(withModels.All(t => t.Target.KeyRevision == cached.KeyRevision), "targets pin the key revision");
        Check.True(withModels.All(t => t.Target.Protocol == ApiProtocol.ChatCompletions), "targets pin the protocol");
        Check.Equal(0, snapshot.Targets.Count(t => t.Target.ProfileId == noCache.Id),
            "a profile with no cached model contributes no selectable target");

        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "IdentityInspectionDisabled", "an automatic round is refused while disabled");
        Check.Equal(before, handler.Count, "a refused round sends nothing");

        // Manual one-off runs are allowed while the automatic switch is off, but need exact per-run consent.
        // With no selection there is no scope to confirm, so the round is refused before any request.
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger.Manual),
            "ConsentRequired", "manual round without per-run consent is refused");
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(
                IdentityInspectionTrigger.Manual, new IdentityInspectionConsent([], 30, DateTimeOffset.UtcNow)),
            "IdentityNotAuthorized", "manual round with an empty selection is refused even with consent");
        Check.Equal(before, handler.Count, "manual refusal sends nothing");
    }

    // V02 -------------------------------------------------------------------
    // Enabling and interval changes require a fresh, exact-scope consent. No network request is ever sent.
    public static async Task V02_ConsentGatingAndExactScope()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "Primary", RelayBase, ["m1", "m2"]);
        IdentityInspectionSnapshot snapshot = await SelectAllAsync(service);
        IdentityInspectionTarget[] scope = snapshot.Targets.Select(t => t.Target).ToArray();
        Check.Equal(2, scope.Length, "two cached models are selected");

        int before = handler.Count;
        IdentityInspectionConsent good = new(scope, 30, DateTimeOffset.UtcNow);
        var cases = new (string Label, IdentityInspectionSettings Settings, IdentityInspectionConsent? Consent)[]
        {
            ("enable without consent", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 }, null),
            ("empty scope", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 }, good with { Scope = [] }),
            ("subset of the target list", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 },
                good with { Scope = [scope[0]] }),
            ("duplicate scope entries", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 },
                good with { Scope = [scope[0], scope[1], scope[0]] }),
            ("stale consent", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 },
                good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }),
            ("future consent", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 },
                good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(5) }),
            ("interval mismatch", new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 45 }, good),
        };
        foreach (var (label, settings, consent) in cases)
            await Check.ThrowsClientAsync(() => service.SaveIdentityInspectionSettingsAsync(settings, consent),
                "ConsentRequired", label + " is refused");

        foreach (int interval in new[] { 9, 481, 0, -30 })
            await Check.ThrowsClientAsync(() => service.SaveIdentityInspectionIntervalAsync(interval, good),
                "InvalidInterval", "interval " + interval + " is outside 10..480");

        Check.Equal(before, handler.Count, "invalid settings never touch the network");
        Check.False((await service.GetIdentityInspectionSnapshotAsync()).Settings.Enabled, "inspection stays disabled");

        // The valid exact-scope consent enables it.
        IdentityInspectionSnapshot enabled = await service.SaveIdentityInspectionSettingsAsync(
            new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 }, good);
        Check.True(enabled.Settings.Enabled, "exact-scope consent enables inspection");
        Check.True(enabled.Targets.All(t => t.Authorized), "the whole selected scope is authorized");
        Check.Equal(before, handler.Count, "enabling sends nothing");
    }

    // V03 -------------------------------------------------------------------
    // One round freezes every selected+authorized target and submits them strictly one at a time, waiting for the
    // previous attempt to reach a terminal state before claiming the next. No parallel POST, no skipped target.
    public static async Task V03_SerialRoundWaitsForTerminalState()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        await SeedAsync(service, handler, "B", Relay2, ["n1"], ApiProtocol.ChatCompletions, Key2);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        Check.Equal(3, round.Total, "the round freezes all three selected targets");
        Check.Equal(IdentityInspectionTrigger.Automatic, round.Trigger, "round records the automatic trigger");
        Check.True(round.Attempts.All(a => a.State == IdentityInspectionAttemptState.Pending), "all attempts start pending");

        // Target 1: submit then complete through the shared poll path.
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
        IdentityInspectionStep step1 = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.NotNull(step1.Probe, "first submission produced a local Probe record");
        Check.Equal(LocalRunState.Running, step1.Probe!.State, "runId-only acknowledgment is Running, not completed");
        Check.True(step1.HasMore, "two more targets remain");
        Check.True(step1.WaitingForResult, "the host must wait for this attempt's terminal state");

        // A second TrySubmit while the first is still running must NOT start another POST.
        IdentityInspectionStep again = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.True(again.WaitingForResult, "still waiting for the in-flight attempt");
        Check.Equal(1, ProbePosts(handler), "no second POST while an attempt is in flight");

        handler.EnqueueJson(HttpStatusCode.OK, CompletedJson("run-1"));
        await service.PollProbeAsync(step1.Probe.LocalId);
        IdentityInspectionStep rec1 = await service.RecordIdentityInspectionResultAsync(round.RoundId, step1.Probe.LocalId);
        Check.Equal(IdentityInspectionAttemptState.Completed, rec1.Round.Attempts[0].State, "first attempt completed");
        Check.False(rec1.WaitingForResult, "host is released to the next target");
        Check.True(rec1.HasMore, "two targets remain");

        // Target 2: explicit terminal failure still lets the round continue.
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-2"));
        IdentityInspectionStep step2 = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal(2, ProbePosts(handler), "second target submitted exactly once");
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProbeStatusJson("run-2", "failed"));
        await service.PollProbeAsync(step2.Probe!.LocalId);
        IdentityInspectionStep rec2 = await service.RecordIdentityInspectionResultAsync(round.RoundId, step2.Probe.LocalId);
        Check.Equal(IdentityInspectionAttemptState.Completed, rec2.Round.Attempts[1].State, "a confirmed terminal failure is a completed attempt");
        Check.True(rec2.HasMore, "explicit failure does not stop the round");

        // Target 3.
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-3"));
        IdentityInspectionStep step3 = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal(3, ProbePosts(handler), "third target submitted exactly once");
        handler.EnqueueJson(HttpStatusCode.OK, CompletedJson("run-3"));
        await service.PollProbeAsync(step3.Probe!.LocalId);
        IdentityInspectionStep rec3 = await service.RecordIdentityInspectionResultAsync(round.RoundId, step3.Probe.LocalId);
        Check.False(rec3.HasMore, "no targets remain");
        Check.False(rec3.WaitingForResult, "nothing is in flight");

        IdentityInspectionStep exhausted = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.False(exhausted.HasMore, "a finished scope has nothing more to submit");
        Check.Equal(3, ProbePosts(handler), "the exhausted round never re-POSTs");

        IdentityInspectionRoundSummary ended = await service.EndIdentityInspectionRoundAsync(round.RoundId);
        Check.NotNull(ended.CompletedAt, "the round is marked complete");
        Check.Equal(3, ended.Completed, "all three attempts completed");
        Check.Equal(0, ended.Skipped, "nothing was skipped");
        Check.Equal(3, ProbeGets(handler), "each attempt used exactly one GET and never re-POSTed");
    }

    // V04 -------------------------------------------------------------------
    // Unknown / expired / interrupted results block the rest of the round; they are never claimed as remote ends.
    public static async Task V04_UnknownBlocksAndExpiredIsNotTerminal()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
        IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.NotNull(step.Probe, "first attempt submitted");

        // GET 404 -> Expired. Expired only means "cannot query"; it is not a confirmed remote end.
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        ProbeRecord expired = await service.PollProbeAsync(step.Probe!.LocalId);
        Check.Equal(LocalRunState.Expired, expired.State, "404 poll marks the local record Expired");

        IdentityInspectionStep recorded = await service.RecordIdentityInspectionResultAsync(round.RoundId, step.Probe.LocalId);
        Check.Equal(IdentityInspectionAttemptState.Unknown, recorded.Round.Attempts[0].State,
            "Expired is recorded as Unknown, never Completed");
        Check.Equal("ProbeExpired", recorded.Round.StopCode, "the round carries the ProbeExpired stop code");
        Check.False(recorded.HasMore, "an unknown result stops the remaining queue");
        Check.False(recorded.WaitingForResult, "nothing is in flight after an unknown stop");

        int posts = ProbePosts(handler);
        IdentityInspectionStep after = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.False(after.HasMore, "a stopped round offers no further target");
        Check.Equal(posts, ProbePosts(handler), "no POST is sent after an unknown stop");

        // A generic history record keeps its Expired state for display; only the identity round treats it as unknown.
        IReadOnlyList<ProbeRecord> history = await service.GetHistoryAsync();
        Check.Equal(LocalRunState.Expired, history.Single(r => r.LocalId == step.Probe.LocalId).State,
            "the underlying Probe history still shows Expired");
    }

    // V05 -------------------------------------------------------------------
    // Intent is persisted before POST and never replayed: a crash/restart does not resubmit and does not re-POST.
    public static async Task V05_IntentPersistedNeverReplayed()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        Guid roundId;
        Guid localId;
        using (var service = New(tmp.Path, handler))
        {
            await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
            await EnableAsync(service);
            IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
            roundId = round.RoundId;
            handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
            IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
            localId = step.Probe!.LocalId;
        }

        // New process on the same store: the interrupted round is closed at first load, and the running Probe
        // record is downgraded to PollingInterrupted. No POST is replayed.
        var restartHandler = new FakeHttpHandler();
        using var restarted = New(tmp.Path, restartHandler);
        IdentityInspectionSnapshot snapshot = await restarted.GetIdentityInspectionSnapshotAsync();
        Check.NotNull(snapshot.LatestRound, "the interrupted round is still visible");
        Check.NotNull(snapshot.LatestRound!.CompletedAt, "the interrupted round is closed, not resumed");
        Check.Equal("ApplicationInterrupted", snapshot.LatestRound.StopCode, "the stop code records the interruption");
        Check.Equal(IdentityInspectionAttemptState.Unknown, snapshot.LatestRound.Attempts[0].State,
            "the in-flight attempt becomes Unknown rather than completed");
        Check.Equal(0, restartHandler.Count, "restart sends nothing");

        IReadOnlyList<ProbeRecord> history = await restarted.GetHistoryAsync();
        ProbeRecord record = history.Single(r => r.LocalId == localId);
        Check.Equal(LocalRunState.PollingInterrupted, record.State, "the persisted Probe record is interrupted, not resubmitted");
        Check.Equal(ProbeRecordSource.IdentityInspection, record.Source, "the record keeps its identity-inspection source");
        Check.Equal(IdentityInspectionTrigger.Automatic, record.IdentityInspectionTrigger, "the record keeps the automatic trigger");

        // The old round cannot be extended, and no new attempt is invented.
        IdentityInspectionStep after = await restarted.TrySubmitNextIdentityInspectionTargetAsync(roundId);
        Check.False(after.HasMore, "an interrupted round offers nothing more");
        Check.Equal(0, restartHandler.Count, "a resumed attempt never re-POSTs");

        // The host may still resume the GET query for the persisted record; that is not a new submission.
        restartHandler.EnqueueJson(HttpStatusCode.OK, CompletedJson("run-1"));
        ProbeRecord resumed = await restarted.PollProbeAsync(localId);
        Check.Equal(LocalRunState.Completed, resumed.State, "GET-only resume is allowed");
        Check.Equal(1, ProbeGets(restartHandler), "exactly one GET, no POST");
        Check.Equal(0, ProbePosts(restartHandler), "resume never submits");
    }

    // V06 -------------------------------------------------------------------
    // An active or unknown pre-existing Probe record blocks a new round; the round is not queued or retried.
    public static async Task V06_ActiveRunBlocksNewRound()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "A", RelayBase, ["m1"]);
        await EnableAsync(service);

        // A manual Probe that is still running (runId-only acknowledgment) is an active task.
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-manual"));
        ProbeRecord manual = await service.StartProbeAsync(profile.Id, "m1", null, Fixtures.ProbeConsent(profile, "m1"));
        Check.Equal(LocalRunState.Running, manual.State, "the manual probe is running");

        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "ActiveRun", "an active Probe record blocks a new round");
        Check.Equal(before, handler.Count, "the blocked round sends nothing");

        // The manual record is not touched by the refusal.
        IReadOnlyList<ProbeRecord> history = await service.GetHistoryAsync();
        Check.Equal(LocalRunState.Running, history.Single(r => r.LocalId == manual.LocalId).State,
            "the existing active record is preserved");
    }

    // V07 -------------------------------------------------------------------
    // HTTP 401/403 pause the affected profile and stop the round; HTTP 429 cools the host and stops; no retry.
    public static async Task V07_HttpAuthAndRateLimitStopRound()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile first = await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        await SeedAsync(service, handler, "B", Relay2, ["n1"], ApiProtocol.ChatCompletions, Key2);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal(1, ProbePosts(handler), "the first target was attempted exactly once");
        Check.Equal("Http401", step.Code, "the POST failure code is surfaced");
        Check.True(step.Round.StopCode is "Http401", "an auth rejection stops the round");
        Check.False(step.HasMore, "nothing more is dispatched after an auth stop");

        // The affected profile is paused; other profiles are not.
        IdentityInspectionSnapshot after = await service.GetIdentityInspectionSnapshotAsync();
        IdentityInspectionTargetStatus paused = after.Targets.Single(t => t.Target.ProfileId == first.Id && t.Target.ModelId == "m1");
        Check.True(paused.Paused, "the rejected profile is paused");
        Check.True(after.Targets.Where(t => t.Target.ProfileId != first.Id).All(t => !t.Paused),
            "other profiles are not paused by one profile's 401");

        int posts = ProbePosts(handler);
        IdentityInspectionStep after2 = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.False(after2.HasMore, "a stopped round does not continue");
        Check.Equal(posts, ProbePosts(handler), "no retry or resubmission after an auth failure");

        // The round is still open (it was stopped, not ended), so a second round is refused as busy.
        int posts2 = ProbePosts(handler);
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "IdentityInspectionBusy", "an open round blocks a second round");
        Check.Equal(posts2, ProbePosts(handler), "the blocked round sends nothing");

        // After ending, the unknown submission is still an active task: a new round is refused locally.
        await service.EndIdentityInspectionRoundAsync(round.RoundId);
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "ActiveRun", "the unknown submission blocks a new round after the round ends");
        Check.Equal(posts2, ProbePosts(handler), "the blocked round sends nothing");
    }

    // V07b ------------------------------------------------------------------
    // 429 on the official host cools the host down, stops the round, and blocks a new round without a request.
    // A separate store is used because the unknown 429 submission itself also blocks (as it must).
    public static async Task V07b_RateLimitCoolsHostAndStops()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        Check.Equal(2, round.Total, "two targets frozen");
        handler.EnqueueStatus((HttpStatusCode)429, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5)));
        IdentityInspectionStep limited = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal("Http429", limited.Code, "the rate-limit code is surfaced");
        Check.True(limited.Round.StopCode is "Http429", "a rate limit stops the round");
        Check.False(limited.HasMore, "the remaining queue is not dispatched");
        Check.Equal(1, limited.Round.Attempts.Count(a => a.State == IdentityInspectionAttemptState.Unknown),
            "the rate-limited attempt is Unknown, not failed");
        Check.Equal(1, ProbePosts(handler), "the second target is never attempted");

        // The host deadline is persisted, so an immediate new round is refused without a request.
        await service.EndIdentityInspectionRoundAsync(round.RoundId);
        int posts = ProbePosts(handler);
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "ConnectionRateLimited", "the host cooldown blocks a new round");
        Check.Equal(posts, ProbePosts(handler), "the cooldown sends nothing");

        // A POST 429 applies to the same official host, so an immediate manual GET poll is also refused locally.
        Guid unknownLocalId = limited.Probe!.LocalId;
        int requests = handler.Count;
        await Check.ThrowsClientAsync(() => service.PollProbeAsync(unknownLocalId),
            "ConnectionRateLimited", "the host cooldown also blocks a manual GET poll");
        Check.Equal(requests, handler.Count, "the cooled-down host receives no GET");
    }

    // V07c ------------------------------------------------------------------
    // A GET 429 cools the official host down too: the immediate next poll is refused locally, not re-requested.
    public static async Task V07c_PollRateLimitCoolsHost()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "A", RelayBase, ["m1"]);
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
        ProbeRecord record = await service.StartProbeAsync(profile.Id, "m1", null, Fixtures.ProbeConsent(profile, "m1"));
        Check.Equal(LocalRunState.Running, record.State, "the probe is running before the poll");

        handler.EnqueueStatus((HttpStatusCode)429, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5)));
        await Check.ThrowsClientAsync(() => service.PollProbeAsync(record.LocalId),
            "Http429", "the GET rate-limit code is surfaced");

        int requests = handler.Count;
        await Check.ThrowsClientAsync(() => service.PollProbeAsync(record.LocalId),
            "ConnectionRateLimited", "the host cooldown blocks the immediate re-poll");
        Check.Equal(requests, handler.Count, "no second GET is sent during the cooldown");

        // The persisted record is not treated as a confirmed remote end.
        IReadOnlyList<ProbeRecord> history = await service.GetHistoryAsync();
        Check.Equal(LocalRunState.PollingInterrupted, history.Single(r => r.LocalId == record.LocalId).State,
            "a rate-limited poll is interrupted, not completed");
    }

    // V08 -------------------------------------------------------------------
    // Manual one-off round: allowed while the automatic switch is off, requires exact per-run consent, and does
    // not write automatic grants. Manual records never carry the automatic trigger.
    public static async Task V08_ManualRoundUsesPerRunConsentOnly()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        IdentityInspectionSnapshot snapshot = await SelectAllAsync(service);
        IdentityInspectionTarget[] scope = snapshot.Targets.Where(t => t.Selected).Select(t => t.Target).ToArray();
        Check.Equal(2, scope.Length, "two models selected");

        // Automatic switch stays off; the manual round is still refused without a per-run consent.
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger.Manual),
            "ConsentRequired", "manual round requires per-run consent");

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync(
            IdentityInspectionTrigger.Manual, new IdentityInspectionConsent(scope, 30, DateTimeOffset.UtcNow));
        Check.Equal(IdentityInspectionTrigger.Manual, round.Trigger, "the manual trigger is persisted");
        Check.NotNull(round.ManualConsentAt, "the per-run consent time is recorded");
        Check.Equal(2, round.Total, "the whole selected scope is frozen");

        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
        IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal(1, ProbePosts(handler), "a manual round submits the first target");
        Check.Equal(ProbeRecordSource.IdentityInspection, step.Probe!.Source, "the record source is identity inspection");
        Check.Equal(IdentityInspectionTrigger.Manual, step.Probe.IdentityInspectionTrigger,
            "the record keeps the manual trigger so notifications can filter it out");

        // Manual consent never creates an automatic grant: the snapshot must still report nothing authorized.
        IdentityInspectionSnapshot after = await service.GetIdentityInspectionSnapshotAsync();
        Check.True(after.Targets.Where(t => t.Selected).All(t => !t.Authorized),
            "a manual round never writes automatic authorization");
        Check.False(after.Settings.Enabled, "a manual round never enables the automatic switch");
    }

    // V09 -------------------------------------------------------------------
    // Selection persistence, cache intersection on refresh, and exact grant revocation on deselect.
    public static async Task V09_SelectionPersistenceAndCacheIntersection()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2", "m3"]);
        IdentityInspectionSnapshot snapshot = await SelectAllAsync(service);
        Check.Equal(3, snapshot.Selections.Single().SelectedModelIds.Count, "all three models selected");

        IdentityInspectionSnapshot enabled = await service.SaveIdentityInspectionSettingsAsync(
            new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 }, ConsentFor(snapshot, 30));
        Check.True(enabled.Targets.All(t => t.Authorized), "all three authorized");

        // A cache refresh that drops m3 must intersect the selection and revoke m3's grant.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
        IdentityInspectionSnapshot afterRefresh = await service.GetIdentityInspectionSnapshotAsync();
        Check.Equal(2, afterRefresh.Selections.Single().SelectedModelIds.Count, "selection intersects the new cache");
        Check.False(afterRefresh.Selections.Single().SelectedModelIds.Contains("m3"), "a removed model leaves the selection");
        Check.Equal(2, afterRefresh.Targets.Count, "only cached models are targets");
        Check.True(afterRefresh.Targets.All(t => t.Authorized), "the surviving models keep their exact grants");

        // Deselecting a model revokes its grant immediately.
        IdentityInspectionSnapshot narrowed = await service.SaveIdentityInspectionSelectionsAsync(
            [new IdentityInspectionSelection(profile.Id, true, ["m1"])]);
        IdentityInspectionTargetStatus m2 = narrowed.Targets.Single(t => t.Target.ModelId == "m2");
        Check.False(m2.Selected, "m2 is deselected");
        Check.False(m2.Authorized, "deselecting a model revokes its grant");

        // A newly cached model is selected only after the user picks it; it never auto-authorizes.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2", "m4"));
        await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
        IdentityInspectionSnapshot widened = await service.GetIdentityInspectionSnapshotAsync();
        IdentityInspectionTargetStatus m4 = widened.Targets.Single(t => t.Target.ModelId == "m4");
        Check.False(m4.Selected, "a newly cached model is not auto-selected");
        Check.False(m4.Authorized, "a newly cached model is not auto-authorized");

        // Restart keeps the saved selection and grants but never auto-runs.
        var restartHandler = new FakeHttpHandler();
        using var restarted = New(tmp.Path, restartHandler);
        IdentityInspectionSnapshot reloaded = await restarted.GetIdentityInspectionSnapshotAsync();
        Check.True(reloaded.Settings.Enabled, "the enabled preference survives restart");
        Check.Equal(1, reloaded.Selections.Single().SelectedModelIds.Count, "the selection survives restart");
        Check.Equal(0, restartHandler.Count, "restart sends nothing");
    }

    // V10 -------------------------------------------------------------------
    // A credential/address/protocol revision invalidates the target: the round skips it and does not send.
    public static async Task V10_RevisionChangeSkipsTargetWithoutSending()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "A", RelayBase, ["m1"]);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        Check.Equal(1, round.Total, "one target frozen");

        // Rotate the key: the frozen target's revision no longer matches the profile.
        await service.SaveProfileAsync((await service.GetProfilesAsync()).Single(), Key2);

        int before = handler.Count;
        IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.Equal("IdentityNotAuthorized", step.Code, "the stale target is skipped as unauthorized");
        Check.Equal(IdentityInspectionAttemptState.Skipped, step.Round.Attempts[0].State, "the stale attempt is skipped, not sent");
        Check.False(step.HasMore, "the only target is skipped, nothing remains");
        Check.Equal(before, handler.Count, "no request is sent for a changed target");
    }

    // V11 -------------------------------------------------------------------
    // Notification preferences: exactly two switches (Connection, Identity), default on, persisted, no network.
    public static async Task V11_NotificationSwitchesPersisted()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);

        NotificationPreferences defaults = await service.GetNotificationPreferencesAsync();
        Check.True(defaults.Connection, "connection notifications default on");
        Check.True(defaults.Identity, "identity notifications default on");

        NotificationPreferences saved = await service.SaveNotificationPreferencesAsync(
            new NotificationPreferences { Connection = false, Identity = true });
        Check.False(saved.Connection, "connection switch saved off");
        Check.True(saved.Identity, "identity switch stays on");
        Check.Equal(0, handler.Count, "notification preferences never touch the network");

        using var restarted = New(tmp.Path, new FakeHttpHandler());
        NotificationPreferences reloaded = await restarted.GetNotificationPreferencesAsync();
        Check.False(reloaded.Connection, "the off switch survives restart");
        Check.True(reloaded.Identity, "the on switch survives restart");
    }

    // V12 -------------------------------------------------------------------
    // Notification dedup: automatic-only, per exact target + problem type, unknown is not a recovery, and a
    // credential/address revision change never carries a stale memory onto the new target.
    public static async Task V12_NotificationDedupAndRevisionIsolation()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "A", RelayBase, ["m1"]);
        NotificationTarget target = new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, "m1");

        // Manual observations never notify.
        NotificationDecision manual = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, Automatic: false,
                NotificationObservationState.Problem, "mismatch"));
        Check.False(manual.ShouldNotify, "manual observations never notify");

        // First automatic problem notifies.
        NotificationDecision first = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Problem, "mismatch"));
        Check.True(first.ShouldNotify, "the first automatic problem notifies");

        // Same problem again is deduplicated.
        NotificationDecision repeat = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Problem, "mismatch"));
        Check.False(repeat.ShouldNotify, "the same problem on the same target does not repeat");

        // Unknown is neither a notify nor a recovery.
        NotificationDecision unknown = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Unknown, null));
        Check.False(unknown.ShouldNotify, "unknown never notifies");
        NotificationDecision stillDeduped = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Problem, "mismatch"));
        Check.False(stillDeduped.ShouldNotify, "unknown does not clear the remembered problem");

        // A different problem type on the same target notifies again.
        NotificationDecision changed = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Problem, "spoof-suspected"));
        Check.True(changed.ShouldNotify, "a changed problem type notifies again");

        // Healthy clears the memory, so a later problem notifies again.
        NotificationDecision healthy = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Healthy, null));
        Check.False(healthy.ShouldNotify, "healthy does not itself notify");
        NotificationDecision again = await service.ObserveNotificationAsync(
            new NotificationObservation(NotificationCategory.Identity, target, true, NotificationObservationState.Problem, "spoof-suspected"));
        Check.True(again.ShouldNotify, "a problem after a real recovery notifies again");

        // A stale target (old key revision) is rejected, never re-bound to the new configuration.
        await Check.ThrowsClientAsync(() => service.ObserveNotificationAsync(
                new NotificationObservation(NotificationCategory.Identity, target with { KeyRevision = 99 }, true,
                    NotificationObservationState.Problem, "mismatch")),
            "NotificationTargetChanged", "a stale key revision is rejected");
        await Check.ThrowsClientAsync(() => service.ObserveNotificationAsync(
                new NotificationObservation(NotificationCategory.Identity, target with { BaseUrl = "https://old.example.com/v1" }, true,
                    NotificationObservationState.Problem, "mismatch")),
            "NotificationTargetChanged", "a stale address is rejected");
        await Check.ThrowsClientAsync(() => service.ObserveNotificationAsync(
                new NotificationObservation(NotificationCategory.Identity, target with { Protocol = ApiProtocol.AnthropicMessages }, true,
                    NotificationObservationState.Problem, "mismatch")),
            "NotificationTargetChanged", "a stale protocol is rejected");

        // An invalid problem code is rejected before any store write.
        await Check.ThrowsClientAsync(() => service.ObserveNotificationAsync(
                new NotificationObservation(NotificationCategory.Identity, target, true,
                    NotificationObservationState.Problem, "raw response text!")),
            "InvalidNotification", "a non-token problem code is rejected");
    }

    // V13 -------------------------------------------------------------------
    // Interval is bound to the grant: changing the interval while off cannot re-enable an old grant; changing
    // it while enabled requires a fresh consent. Both rules avoid silently widening an authorization.
    public static async Task V13_IntervalBoundToGrant()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1"]);
        IdentityInspectionSnapshot snapshot = await SelectAllAsync(service);
        await service.SaveIdentityInspectionSettingsAsync(
            new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 30 }, ConsentFor(snapshot, 30));

        // Turning off and changing the interval must not let the old 30-minute grant re-enable a 60-minute plan.
        await service.SaveIdentityInspectionSettingsAsync(new IdentityInspectionSettings { Enabled = false, IntervalMinutes = 30 });
        await service.SaveIdentityInspectionIntervalAsync(60);
        IdentityInspectionSnapshot offAt60 = await service.GetIdentityInspectionSnapshotAsync();
        Check.Equal(60, offAt60.Settings.IntervalMinutes, "the new interval is saved");
        Check.True(offAt60.Targets.All(t => !t.Authorized), "the old 30-minute grant is not valid for the new interval");

        await Check.ThrowsClientAsync(() => service.SaveIdentityInspectionSettingsAsync(
                new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 60 }),
            "ConsentRequired", "re-enabling at a new interval needs a fresh consent");

        // A fresh consent at the new interval works.
        IdentityInspectionSnapshot fresh = await service.GetIdentityInspectionSnapshotAsync();
        IdentityInspectionSnapshot enabled = await service.SaveIdentityInspectionSettingsAsync(
            new IdentityInspectionSettings { Enabled = true, IntervalMinutes = 60 }, ConsentFor(fresh, 60));
        Check.True(enabled.Settings.Enabled, "fresh consent re-enables at the new interval");
        Check.True(enabled.Targets.All(t => t.Authorized), "the fresh grant covers the new interval");
    }

    // V14 -------------------------------------------------------------------
    // Legacy capability vault fields load, are ignored for automation, and are dropped on the next successful save.
    // Configuration, credentials, model cache, connection history and Probe reports survive.
    public static async Task V14_LegacyCapabilityStateMigratesWithoutAutomation()
    {
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var seedHandler = new FakeHttpHandler();
        Guid profileId;
        using (var seeding = New(tmp.Path, seedHandler, protector))
        {
            RelayProfile profile = await SeedAsync(seeding, seedHandler, "Legacy", RelayBase, ["m1"]);
            profileId = profile.Id;
            // A completed manual Probe report and a connection record must both survive the upgrade.
            seedHandler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-legacy"));
            ProbeRecord probe = await seeding.StartProbeAsync(profile.Id, "m1", null, Fixtures.ProbeConsent(profile, "m1"));
            seedHandler.EnqueueJson(HttpStatusCode.OK, CompletedJson("run-legacy", "plain_mismatch"));
            await seeding.PollProbeAsync(probe.LocalId);
            seedHandler.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
            await seeding.TestConnectionAsync(profile.Id, "m1", new OutboundConsent(
                profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ConnectionTest,
                ConnectionTestEndpoints.Recipient(profile.BaseUrl, profile.Protocol), "m1",
                DateTimeOffset.UtcNow, profile.Protocol));
        }

        string primary = Path.Combine(tmp.Path, "desktop-state.json");
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(primary))!;
        System.Text.Json.Nodes.JsonNode vault = System.Text.Json.Nodes.JsonNode.Parse(
            protector.Unprotect(Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        root["schemaVersion"] = 2; // Explicitly model the previous release, not a malformed schema-3 file.
        // Re-introduce the old capability fields exactly as a previous release would have written them.
        vault["capabilitySettings"] = System.Text.Json.Nodes.JsonNode.Parse("{\"enabled\":true,\"intervalMinutes\":30}");
        vault["capabilitySelections"] = System.Text.Json.Nodes.JsonNode.Parse(
            "[{\"profileId\":\"" + profileId + "\",\"isSelected\":true,\"selectedModelIds\":[\"m1\"]}]");
        vault["capabilityGrants"] = System.Text.Json.Nodes.JsonNode.Parse("[]");
        vault["capabilityBaselines"] = System.Text.Json.Nodes.JsonNode.Parse("[]");
        vault["capabilityHistory"] = System.Text.Json.Nodes.JsonNode.Parse("[]");
        vault["notificationMemory"] = System.Text.Json.Nodes.JsonNode.Parse(
            "[{\"category\":1,\"target\":{\"profileId\":\"" + profileId +
            "\",\"keyRevision\":1,\"baseUrl\":\"" + RelayBase + "\",\"protocol\":0,\"modelId\":\"m1\"}," +
            "\"problemType\":\"below-baseline\",\"updatedAt\":\"2026-10-30T00:00:00+00:00\"}]");
        root["protectedState"] = Convert.ToBase64String(
            protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString(StoreJson()))));
        File.WriteAllText(primary, root.ToJsonString(StoreJson()));

        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler, protector);
        IdentityInspectionSnapshot snapshot = await service.GetIdentityInspectionSnapshotAsync();
        Check.False(snapshot.Settings.Enabled, "a legacy enabled capability switch never enables identity inspection");
        Check.Equal(30, snapshot.Settings.IntervalMinutes, "identity inspection starts at its own default");
        Check.Null(snapshot.LatestRound, "no identity round is invented from capability history");
        Check.True(snapshot.Targets.All(t => !t.Selected && !t.Authorized),
            "legacy capability selection/authorization is not promoted");
        Check.Equal(profileId, snapshot.Targets.Single().Target.ProfileId, "the configuration is preserved");

        // A legacy capability notification memory (category 1) is dropped by the load migration.
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(),
            "IdentityInspectionDisabled", "no automatic round can start from legacy state");
        Check.Equal(0, handler.Count, "legacy load sends nothing");

        // The first save after the upgrade drops the legacy capability fields but keeps config/history.
        await service.SaveNotificationPreferencesAsync(new NotificationPreferences { Connection = true, Identity = true });
        System.Text.Json.Nodes.JsonNode rewrittenRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(primary))!;
        System.Text.Json.Nodes.JsonNode rewritten = System.Text.Json.Nodes.JsonNode.Parse(
            protector.Unprotect(Convert.FromBase64String(rewrittenRoot["protectedState"]!.GetValue<string>())))!;
        Check.False(rewritten.AsObject().ContainsKey("capabilitySettings"), "the legacy capability settings are dropped on save");
        Check.False(rewritten.AsObject().ContainsKey("capabilityHistory"), "the legacy capability history is dropped on save");
        Check.Equal(1, rewritten["secrets"]!.AsObject().Count, "the credential is preserved");
        Check.Equal(1, rewritten["history"]!.AsArray().Count, "the completed Probe report is preserved");
        Check.Equal(1, rewritten["connectionHistory"]!.AsArray().Count, "the connection record is preserved");
        Check.Equal(1, rewritten["notificationPreferences"] is System.Text.Json.Nodes.JsonObject ? 1 : 0,
            "notification preferences survive the upgrade");
    }

    // V15 -------------------------------------------------------------------
    // Concurrent RecordResult after the round ended does not mutate or resurrect it; a late Probe completion
    // only fills the attempt's local id, never re-opens the round.
    public static async Task V15_RecordAfterEndDoesNotResurrectRound()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "A", RelayBase, ["m1", "m2"]);
        await EnableAsync(service);

        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync();
        handler.EnqueueJson(HttpStatusCode.OK, RunIdOnly("run-1"));
        IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
        Check.NotNull(step.Probe, "first attempt submitted");

        IdentityInspectionRoundSummary ended = await service.EndIdentityInspectionRoundAsync(round.RoundId, cancelled: true);
        Check.True(ended.Cancelled, "the round is marked cancelled");
        Check.Equal(IdentityInspectionAttemptState.Unknown, ended.Attempts[0].State,
            "an in-flight attempt becomes Unknown when the round is ended");
        Check.Equal("RoundEnded", ended.Attempts[0].Code, "the ended attempt carries the RoundEnded code");
        Check.Equal(IdentityInspectionAttemptState.Skipped, ended.Attempts[1].State, "a pending attempt becomes Skipped");

        // A later RecordResult for the same record must not change the ended round or its attempts.
        handler.EnqueueJson(HttpStatusCode.OK, CompletedJson("run-1"));
        await service.PollProbeAsync(step.Probe!.LocalId);
        IdentityInspectionStep late = await service.RecordIdentityInspectionResultAsync(round.RoundId, step.Probe.LocalId);
        Check.True(late.Round.Cancelled, "the ended round stays cancelled");
        Check.NotNull(late.Round.CompletedAt, "the ended round keeps its completion time");
        Check.Equal(IdentityInspectionAttemptState.Unknown, late.Round.Attempts[0].State,
            "a late result never resurrects the ended attempt");
        Check.False(late.HasMore, "an ended round has nothing more");

        // The Probe history itself is updated (the GET is allowed), but the round is untouched.
        IReadOnlyList<ProbeRecord> history = await service.GetHistoryAsync();
        Check.Equal(LocalRunState.Completed, history.Single(r => r.LocalId == step.Probe.LocalId).State,
            "the underlying Probe record still reflects the late completion");
        Check.Equal(1, ProbePosts(handler), "no additional POST is made");
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>Same options as DesktopStore.Json, needed to rewrite the protected vault fixture.</summary>
    private static System.Text.Json.JsonSerializerOptions StoreJson()
        => new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            MaxDepth = 32,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
}
