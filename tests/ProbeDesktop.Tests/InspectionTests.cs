using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Regression tests for the global connection-inspection feature (IGlobalInspectionService) and the
/// separate connection history. FakeHttpHandler only: no socket is ever opened, no real credential is used.
/// Scope of the contract under test: src/ProbeDesktop.Core/InspectionContracts.cs, InspectionService.cs
/// and docs/global-inspection.md.
/// </summary>
public static class InspectionTests
{
    private const string Key = Program.Key1;
    private const string Key2 = Program.Key2;
    private const string RelayBase = "https://relay.example.com/v1";
    private const string Relay2 = "https://relay2.example.com/v1";

    private const string ChatOk = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";
    private const string AnthropicOk = "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}";

    // Same options as DesktopStore.Json, needed to rewrite the protected vault in the retention fixture.
    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    private static DesktopService New(string dir, HttpMessageHandler handler, ISecretProtector? protector = null)
        => new(dir, new HttpClient(handler, disposeHandler: false), protector ?? new ReversibleTestProtector());

    private static OutboundConsent ModelConsent(RelayProfile profile)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow, profile.Protocol);

    private static OutboundConsent ConnectionConsent(RelayProfile profile, string modelId)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(profile.BaseUrl, profile.Protocol), modelId, DateTimeOffset.UtcNow, profile.Protocol);

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

    private static async Task<InspectionSnapshot> EnableAsync(DesktopService service, int interval = 15)
    {
        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        return await Fixtures.ConfirmInspectionAsync(service, AllSelections(snapshot), interval, enable: true);
    }

    private static InspectionSelection[] AllSelections(InspectionSnapshot snapshot) => snapshot.Targets.Select(t =>
        new InspectionSelection(t.Target.ProfileId, t.Target.Models.Count > 0, t.Target.Models)).ToArray();

    private static string RequestModel(RecordedRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(request.Body);
        return document.RootElement.TryGetProperty("model", out JsonElement model) ? model.GetString() ?? "" : "";
    }

    // I01 -------------------------------------------------------------------
    // Defaults: off, 15 minutes, nothing authorized, nothing sent.
    public static async Task I01_DefaultsAndNoAutoRun()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);

        InspectionSnapshot blank = await service.GetInspectionSnapshotAsync();
        Check.False(blank.Settings.Enabled, "inspection defaults to disabled");
        Check.Equal(30, blank.Settings.IntervalMinutes, "inspection default interval is 30 minutes");
        Check.Equal(0, blank.Settings.MaxRetries, "inspection defaults to one actual attempt");
        Check.Null(blank.LastCompletedAt, "no completion baseline before the first finished round");
        Check.Equal(0, blank.Targets.Count, "no targets before any profile exists");
        Check.Null(blank.LatestRound, "no round recorded yet");

        RelayProfile cached = await SeedAsync(service, handler, "Primary", RelayBase, ["m1", "m2"]);
        RelayProfile noCache = await service.SaveProfileAsync(Fixtures.NewProfile("Empty", Relay2), Key2);

        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        Check.False(snapshot.Settings.Enabled, "caching models must not enable inspection");
        Check.Equal(2, snapshot.Targets.Count, "every profile is listed, not only the selected one");

        InspectionTargetStatus withModels = snapshot.Targets.Single(t => t.Target.ProfileId == cached.Id);
        Check.Equal(InspectionAuthorizationState.PendingApproval, withModels.Authorization, "cached models await explicit approval");
        Check.Equal(0, withModels.AuthorizedModels.Count, "nothing authorized before approval");
        Check.Equal(2, withModels.Target.Models.Count, "target freezes the whole cached model set");
        Check.Equal(cached.KeyRevision, withModels.Target.KeyRevision, "target pins the key revision");
        Check.Equal(ApiProtocol.ChatCompletions, withModels.Target.Protocol, "target pins the protocol");
        Check.Null(withModels.AuthorizedAt, "no authorization time yet");

        InspectionTargetStatus withoutModels = snapshot.Targets.Single(t => t.Target.ProfileId == noCache.Id);
        Check.Equal(InspectionAuthorizationState.NoCachedModels, withoutModels.Authorization, "profile without cache is reported, not called");

        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "InspectionDisabled", "automatic round refused while disabled");
        Check.Equal(before, handler.Count, "disabled round sends no request");
        Check.Equal(0, (await service.GetConnectionHistoryAsync()).Count, "no connection record written while disabled");
    }

    // I02 -------------------------------------------------------------------
    // Enabling and interval changes require a fresh, exact-scope consent; invalid consent never sends.
    public static async Task I02_ConsentGatingAndExactScope()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "Primary", RelayBase, ["m1", "m2"]);
        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        InspectionTarget target = snapshot.Targets[0].Target;
        InspectionSelection[] draft = AllSelections(snapshot);

        int before = handler.Count;
        InspectionConsent good = Fixtures.InspectionApproval(snapshot, draft, 15);
        await Check.ThrowsClientAsync(() => service.SaveInspectionSettingsAsync(new() { Enabled = true, IntervalMinutes = 15 }, good),
            "ConsentRequired", "toggle cannot adopt even a valid unsubmitted consent");
        var cases = new (string Label, InspectionConsent Consent, string Code)[]
        {
            ("empty scope for a nonempty draft", good with { Scope = [] }, "ConsentRequired"),
            ("subset of models", good with { Scope = [target with { Models = ["m1"] }] }, "ConsentRequired"),
            ("extra model", good with { Scope = [target with { Models = ["m1", "m2", "m3"] }] }, "ConsentRequired"),
            ("wrong key revision", good with { Scope = [target with { KeyRevision = profile.KeyRevision + 1 }] }, "ConsentRequired"),
            ("wrong base url", good with { Scope = [target with { BaseUrl = Relay2 }] }, "ConsentRequired"),
            ("wrong protocol", good with { Scope = [target with { Protocol = ApiProtocol.AnthropicMessages }] }, "ConsentRequired"),
            ("expired consent", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-11) }, "ConsentRequired"),
            ("future consent", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(5) }, "ConsentRequired"),
            ("out of range interval", good with { IntervalMinutes = 0 }, "InvalidInterval")
        };
        foreach (var (label, consent, code) in cases)
            await Check.ThrowsClientAsync(() => service.ConfirmInspectionSettingsAsync(draft, consent), code, "rejects " + label);

        Check.Equal(before, handler.Count, "consent validation never reaches the network");
        InspectionSnapshot after = await service.GetInspectionSnapshotAsync();
        Check.False(after.Settings.Enabled, "failed attempts leave inspection disabled");
        Check.Equal(InspectionAuthorizationState.PendingApproval, after.Targets[0].Authorization, "failed attempts grant nothing");

        var dirty = await service.SaveInspectionSettingsAsync(new() { Enabled = true, IntervalMinutes = 15, MaxRetries = 2 });
        Check.Equal(30, dirty.Settings.IntervalMinutes, "toggle does not adopt draft interval");
        Check.Equal(0, dirty.Settings.MaxRetries, "toggle does not adopt draft retries");
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(), "InspectionNotAuthorized", "on switch alone still cannot send");
        Check.Equal(before, handler.Count, "toggling with no confirmed plan sends nothing");

        // A plain disable never requires consent and a dirty UI interval must not block stopping.
        InspectionSnapshot stopped = await service.SaveInspectionSettingsAsync(
            new InspectionSettings { Enabled = false, IntervalMinutes = 99 });
        Check.False(stopped.Settings.Enabled, "plain disable needs no consent");
        Check.Equal(30, stopped.Settings.IntervalMinutes, "dirty interval on a plain off keeps the stored interval");
    }

    // I03 -------------------------------------------------------------------
    // One round covers every cached model of every profile (never only the selected profile) with the
    // protocol-specific endpoint and authentication of each profile.
    public static async Task I03_BatchAllProfilesWithPerProtocolEndpoints()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile chat = await SeedAsync(service, handler, "Chat Relais", RelayBase, ["chat-a", "chat-b"]);
        RelayProfile claude = await SeedAsync(service, handler, "Claude Relais", Relay2, ["claude-a"], ApiProtocol.AnthropicMessages, Key2);

        InspectionSnapshot enabled = await EnableAsync(service);
        Check.True(enabled.Settings.Enabled, "inspection enabled after exact approval");
        Check.True(enabled.Targets.All(t => t.Authorization == InspectionAuthorizationState.Authorized), "all cached targets authorized");
        Check.Equal(2, enabled.Targets.Single(t => t.Target.ProfileId == chat.Id).AuthorizedModels.Count, "both chat models authorized");

        int seedRequests = handler.Count;
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        handler.EnqueueJson(HttpStatusCode.OK, AnthropicOk);

        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        Check.Equal(3, round.Total, "total counts every cached model in every profile");
        Check.Equal(3, round.Succeeded, "three models answered");
        Check.Equal(0, round.Failed, "no failures");
        Check.Equal(0, round.Skipped, "nothing skipped");
        Check.False(round.Cancelled, "round not cancelled");
        Check.NotNull(round.CompletedAt, "round completed");
        Check.Null(round.CurrentModelId, "progress cleared when finished");

        RecordedRequest[] attempts = handler.Requests.Skip(seedRequests).ToArray();
        Check.Equal(3, attempts.Length, "exactly one request per authorized model");
        Check.Equal("https://relay.example.com/v1/chat/completions", attempts[0].Uri.AbsoluteUri, "chat protocol endpoint");
        Check.Equal("chat-a", RequestModel(attempts[0]), "models ordered and sent verbatim");
        Check.Equal("chat-b", RequestModel(attempts[1]), "second model of the same profile");
        Check.Contains(Header(attempts[0], "Authorization"), "Bearer " + Key, "chat profile uses its Bearer key");
        Check.Equal("https://relay2.example.com/v1/messages", attempts[2].Uri.AbsoluteUri, "anthropic protocol endpoint");
        Check.Equal(Key2, Header(attempts[2], "x-api-key"), "anthropic profile uses x-api-key");
        Check.Equal("", Header(attempts[2], "Authorization"), "anthropic never sends Bearer");
        Check.True(handler.Requests.All(r => !r.Uri.Host.Equals("bazaarlink.ai", StringComparison.OrdinalIgnoreCase)),
            "inspection never sends a BazaarLink request");

        IReadOnlyList<ConnectionTestResult> history = await service.GetConnectionHistoryAsync();
        Check.Equal(3, history.Count, "one connection record per model");
        Check.True(history.All(r => r.RoundId == round.RoundId), "records carry the round id");
        Check.True(history.All(r => r.Source == ConnectionRecordSource.Automatic), "records are marked automatic");
        Check.True(history.All(r => r.Stage == ConnectionTestStage.ModelResponded && r.ModelResponded), "records show a real model response");
        Check.True(history.All(r => r.CompletedAt is not null), "records have a completion time");
        Check.True(history.All(r => r.HttpStatusCode == 200), "records keep the HTTP status");
        Check.True(history.All(r => r.ElapsedMilliseconds is >= 0), "records keep the elapsed time");
        Check.Equal(3, history.Select(r => r.RecordId).Distinct().Count(), "each record has its own id");
        Check.Equal(1, history.Count(r => r.Protocol == ApiProtocol.AnthropicMessages), "protocol is snapshotted per record");

        InspectionSnapshot after = await service.GetInspectionSnapshotAsync();
        Check.NotNull(after.LatestRound, "latest round is persisted");
        Check.Equal(round.RoundId, after.LatestRound!.RoundId, "persisted round is this round");
        Check.Equal(3, after.LatestRound.Succeeded, "persisted round keeps counters");
    }

    // I04 -------------------------------------------------------------------
    // Authorization is persisted: a new process (service instance) resumes the approved scope and can run
    // without a new consent, while Core itself never starts a round on its own.
    public static async Task I04_PersistedAuthorizationSurvivesRestart()
    {
        using var tmp = new TempDir();
        var seedHandler = new FakeHttpHandler();
        Guid profileId;
        using (var seeding = New(tmp.Path, seedHandler))
        {
            RelayProfile profile = await SeedAsync(seeding, seedHandler, "Primary", RelayBase, ["m1", "m2"]);
            profileId = profile.Id;
            await EnableAsync(seeding);
        }

        var restartedHandler = new FakeHttpHandler();
        restartedHandler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        restartedHandler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        using var restarted = New(tmp.Path, restartedHandler);

        InspectionSnapshot snapshot = await restarted.GetInspectionSnapshotAsync();
        Check.True(snapshot.Settings.Enabled, "enabled preference persisted");
        Check.Equal(15, snapshot.Settings.IntervalMinutes, "interval persisted");
        Check.Equal(profileId, snapshot.Targets[0].Target.ProfileId, "same target after restart");
        Check.Equal(InspectionAuthorizationState.Authorized, snapshot.Targets[0].Authorization, "authorization persisted");
        Check.Equal(2, snapshot.Targets[0].AuthorizedModels.Count, "both models remain authorized");
        Check.NotNull(snapshot.Targets[0].AuthorizedAt, "authorization timestamp persisted");
        Check.Equal(0, restartedHandler.Count, "Core owns no timer: a fresh instance sends nothing by itself");

        InspectionRoundSummary round = await restarted.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        Check.Equal(2, round.Succeeded, "approved scope runs without a new consent");
        Check.Equal(2, restartedHandler.Count, "one request per authorized model after restart");
        Check.Equal(2, (await restarted.GetConnectionHistoryAsync()).Count, "records survive restart");
    }

    // I05 -------------------------------------------------------------------
    // A newly cached model waits for approval, but the previously authorized models keep running.
    public static async Task I05_ExpandedScopePendingButAuthorizedSubsetContinues()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "Primary", RelayBase, ["m1", "m2"]);
        await EnableAsync(service);

        // Adding a model to the cache must not silently widen the approved scope.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2", "m3"));
        RelayProfile expanded = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
        Check.Equal(3, expanded.Models.Count, "cache now holds a third model");
        // The new model may be checked in the UI draft, but the legacy write must not persist it.
        var beforeDraft = await service.GetInspectionSnapshotAsync();
        var draft = AllSelections(beforeDraft);
        await Check.ThrowsClientAsync(() => service.SaveInspectionSelectionsAsync(draft), null,
            "legacy selection writes cannot persist a draft without confirmation");

        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        InspectionTargetStatus status = snapshot.Targets.Single(t => t.Target.ProfileId == profile.Id);
        Check.Equal(InspectionAuthorizationState.Authorized, status.Authorization, "the saved scope is still fully authorized");
        Check.Equal(2, status.AuthorizedModels.Count, "only the previously approved models stay eligible");
        Check.True(status.AuthorizedModels.Contains("m1") && status.AuthorizedModels.Contains("m2"), "approved subset preserved");
        Check.False(status.AuthorizedModels.Contains("m3"), "new model is not authorized");

        int before = handler.Count;
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(2, round.Total, "an unconfirmed model never reaches the round queue");
        Check.Equal(2, round.Succeeded, "authorized subset still runs");
        Check.Equal(0, round.Skipped, "unconfirmed model is not part of the round");
        RecordedRequest[] attempts = handler.Requests.Skip(before).ToArray();
        Check.Equal(2, attempts.Length, "no request for the unapproved model");
        Check.False(attempts.Any(r => RequestModel(r) == "m3"), "m3 was never sent");

        // A brand-new profile also waits for approval and is not called.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("n1"));
        RelayProfile other = await service.SaveProfileAsync(Fixtures.NewProfile("Second", Relay2), Key2);
        await service.RefreshModelsAsync(other.Id, ModelConsent(other));
        InspectionSnapshot second = await service.GetInspectionSnapshotAsync();
        Check.Equal(InspectionAuthorizationState.PendingApproval,
            second.Targets.Single(t => t.Target.ProfileId == other.Id).Authorization, "new profile is pending");
        Check.Equal(0, second.Targets.Single(t => t.Target.ProfileId == other.Id).AuthorizedModels.Count, "new profile has no authorized models");

        // Shrinking the cache never expands authorization; the remaining subset stays authorized.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        await service.RefreshModelsAsync(profile.Id, ModelConsent(expanded));
        InspectionSnapshot shrunk = await service.GetInspectionSnapshotAsync();
        Check.Equal(InspectionAuthorizationState.Authorized,
            shrunk.Targets.Single(t => t.Target.ProfileId == profile.Id).Authorization,
            "shrinking to an already approved subset keeps it authorized");
        Check.Equal(1, shrunk.Targets.Single(t => t.Target.ProfileId == profile.Id).AuthorizedModels.Count,
            "authorized set follows the current cache, never grows");
    }

    // I06 -------------------------------------------------------------------
    // Disabling persists, survives restart, blocks the timer round, and leaves manual rounds available.
    public static async Task I06_DisablePersistsAndManualRoundStillWorks()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        Guid profileId;
        using (var service = New(tmp.Path, handler))
        {
            RelayProfile profile = await SeedAsync(service, handler, "Primary", RelayBase, ["m1"]);
            profileId = profile.Id;
            await EnableAsync(service);

            InspectionSnapshot disabled = await service.SaveInspectionSettingsAsync(
                new InspectionSettings { Enabled = false, IntervalMinutes = 15 });
            Check.False(disabled.Settings.Enabled, "disable takes effect immediately");
            Check.Equal(15, disabled.Settings.IntervalMinutes, "disable keeps the interval");

            int before = handler.Count;
            await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Automatic),
                "InspectionDisabled", "automatic round refused after disable");
            Check.Equal(before, handler.Count, "disabled automatic round sends nothing");

            handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
            InspectionRoundSummary manual = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
            Check.Equal(1, manual.Succeeded, "manual round still allowed while the timer is off");
            ConnectionTestResult record = (await service.GetConnectionHistoryAsync()).Single();
            Check.Equal(ConnectionRecordSource.RoundManual, record.Source, "manual round records its own source");
        }

        var restartHandler = new FakeHttpHandler();
        using var restarted = New(tmp.Path, restartHandler);
        InspectionSnapshot afterRestart = await restarted.GetInspectionSnapshotAsync();
        Check.False(afterRestart.Settings.Enabled, "disabled state survives restart");
        Check.Equal(InspectionAuthorizationState.Authorized, afterRestart.Targets[0].Authorization, "disable keeps the approved scope");
        await Check.ThrowsClientAsync(() => restarted.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "InspectionDisabled", "restart does not silently re-enable the timer");
        Check.Equal(0, restartHandler.Count, "restart after disable sends nothing");
        Check.Equal(profileId, afterRestart.Targets[0].Target.ProfileId, "same profile after restart");
    }

    // I07 -------------------------------------------------------------------
    // Disabling while a round is running stops the queue immediately: the in-flight request is not
    // repeated and no queued model is sent afterwards.
    public static async Task I07_DisableDuringRoundStopsQueue()
    {
        using var tmp = new TempDir();
        var seedHandler = new FakeHttpHandler();
        using (var seeding = New(tmp.Path, seedHandler))
        {
            await SeedAsync(seeding, seedHandler, "Primary", RelayBase, ["m1", "m2"]);
            await EnableAsync(seeding);
        }

        var gate = new GatedHandler { BlockAtRequestNumber = 1 };
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        using var roundService = New(tmp.Path, gate);
        Task<InspectionRoundSummary> roundTask = roundService.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        // The control instance is a separate service on the same directory: the network request holds no
        // long store lock, so the disable can be persisted while the round is blocked in the network.
        using var control = New(tmp.Path, new FakeHttpHandler());
        await control.SaveInspectionSettingsAsync(new InspectionSettings { Enabled = false, IntervalMinutes = 15 })
            .WaitAsync(TimeSpan.FromSeconds(10));

        gate.Release();
        InspectionRoundSummary round = await roundTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check.Equal(1, gate.Count, "only the already in-flight request was sent; nothing queued afterwards");
        Check.True(round.Cancelled, "round reports cancellation");
        Check.Equal(2, round.Total, "total still shows the frozen scope");
        Check.Equal(0, round.Succeeded, "the cancelled attempt is never counted as success");
        Check.Equal(1, round.Succeeded + round.Failed + round.UnknownCount + round.PossibleSuccessCount, "only the single in-flight attempt is accounted for");
        IReadOnlyList<ConnectionTestResult> history = await control.GetConnectionHistoryAsync();
        Check.Equal(1, history.Count, "no second record written after disable");
        Check.False(history[0].ModelResponded, "the cancelled attempt is not a model response");
        Check.False((await control.GetInspectionSnapshotAsync()).Settings.Enabled, "inspection stays disabled");
    }

    // I08 -------------------------------------------------------------------
    // 401 pauses the affected profile, 429 cools down the whole host, other profiles continue, and no
    // request is ever retried, moved to another key/path, or routed through BazaarLink.
    public static async Task I08_HttpErrorsPauseCooldownNoBypass()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile first = await SeedAsync(service, handler, "First", RelayBase, ["a1", "a2"]);
        RelayProfile sameHost = await SeedAsync(service, handler, "Second", RelayBase, ["b1"]);
        RelayProfile otherHost = await SeedAsync(service, handler, "Other", Relay2, ["c1"], ApiProtocol.ChatCompletions, Key2);
        await EnableAsync(service);

        int seedRequests = handler.Count;
        handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        handler.EnqueueStatus((HttpStatusCode)429);
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);

        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        RecordedRequest[] attempts = handler.Requests.Skip(seedRequests).ToArray();
        Check.Equal(3, attempts.Length, "each attempted model is requested exactly once (no retry)");
        Check.Equal("a1", RequestModel(attempts[0]), "first profile attempted its first model");
        Check.Equal("b1", RequestModel(attempts[1]), "second profile on the same host is attempted once");
        Check.Equal("c1", RequestModel(attempts[2]), "profile on another host continues");
        Check.Contains(Header(attempts[2], "Authorization"), "Bearer " + Key2, "no key swap after a 429");
        Check.Equal("https://relay2.example.com/v1/chat/completions", attempts[2].Uri.AbsoluteUri, "no path change to bypass the limit");
        Check.True(handler.Requests.All(r => !r.Uri.Host.Equals("bazaarlink.ai", StringComparison.OrdinalIgnoreCase)),
            "errors never fall back to a BazaarLink request");

        Check.Equal(1, round.Succeeded, "only the unaffected host succeeded");
        Check.Equal(2, round.Failed, "401 and 429 are recorded as failures");
        Check.Equal(1, round.Skipped, "the second model of the paused profile is skipped");

        InspectionSnapshot after = await service.GetInspectionSnapshotAsync();
        InspectionTargetStatus paused = after.Targets.Single(t => t.Target.ProfileId == first.Id);
        Check.Equal(InspectionAuthorizationState.Paused, paused.Authorization, "401 pauses the affected profile");
        Check.Equal(InspectionAuthorizationState.Authorized, after.Targets.Single(t => t.Target.ProfileId == sameHost.Id).Authorization,
            "401 of one profile does not pause another");
        Check.Equal(InspectionAuthorizationState.Authorized, after.Targets.Single(t => t.Target.ProfileId == otherHost.Id).Authorization,
            "profile on another host stays authorized");

        // Next round: the paused profile stays silent, the 429 host is in cooldown, the clean host runs.
        int beforeSecondRound = handler.Count;
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary second = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        RecordedRequest[] secondAttempts = handler.Requests.Skip(beforeSecondRound).ToArray();
        Check.Equal(1, secondAttempts.Length, "only the healthy host is called in the next round");
        Check.Equal("c1", RequestModel(secondAttempts[0]), "the cooled-down host is not retried");
        Check.Equal(2, second.Total, "a profile already paused before round start is not eligible to be frozen");
        Check.Equal(1, second.Skipped, "the still-authorized host is frozen but cannot send during cooldown");
        IReadOnlyList<ConnectionTestResult> history = await service.GetConnectionHistoryAsync();
        ConnectionTestResult unauthorized = history.Single(r => r.ModelId == "a1");
        Check.Equal(401, unauthorized.HttpStatusCode, "401 status recorded");
        Check.Equal(ConnectionTestStage.Failed, unauthorized.Stage, "401 is a definite failure");
        Check.False(unauthorized.ModelResponded, "no model response on 401");
        ConnectionTestResult limited = history.Single(r => r.ModelId == "b1");
        Check.Equal(429, limited.HttpStatusCode, "429 status recorded");
        Check.NotNull(limited.RetryNotBefore, "429 stores a local retry deadline");
        Check.True(limited.RetryNotBefore > DateTimeOffset.UtcNow, "retry deadline is in the future");
        TimeSpan remaining = limited.RetryNotBefore!.Value - DateTimeOffset.UtcNow;
        Check.True(remaining > TimeSpan.FromSeconds(50) && remaining <= TimeSpan.FromSeconds(61),
            "429 without Retry-After defaults to a ~60 second cooldown, got " + remaining.TotalSeconds.ToString("F0") + "s");
        DateTimeOffset? hostDeadline = after.Targets.Single(t => t.Target.ProfileId == sameHost.Id).RetryNotBefore;
        Check.NotNull(hostDeadline, "host cooldown is surfaced on the affected target");
        Check.True(Math.Abs((hostDeadline!.Value - limited.RetryNotBefore!.Value).TotalSeconds) < 1,
            "target cooldown matches the recorded retry deadline");
        Check.Null(after.Targets.Single(t => t.Target.ProfileId == otherHost.Id).RetryNotBefore, "another host has no cooldown");

        // A manual test on the same host is refused locally, without any request and without another key.
        int beforeManual = handler.Count;
        RelayProfile fresh = (await service.GetProfilesAsync()).First(p => p.Id == sameHost.Id);
        await Check.ThrowsClientAsync(() => service.TestConnectionAsync(sameHost.Id, "b1", ConnectionConsent(fresh, "b1")),
            "ConnectionRateLimited", "manual test honours the host cooldown");
        Check.Equal(beforeManual, handler.Count, "cooldown refusal sends no request");

        // The paused state is persisted, not only in memory.
        using var restarted = New(tmp.Path, new FakeHttpHandler());
        InspectionSnapshot reopened = await restarted.GetInspectionSnapshotAsync();
        Check.Equal(InspectionAuthorizationState.Paused,
            reopened.Targets.Single(t => t.Target.ProfileId == first.Id).Authorization, "pause survives restart");
    }

    // I09 -------------------------------------------------------------------
    public static async Task ConfigurationRoundTripNeverRevivesQueuedTarget()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "First", RelayBase, ["first"]);
        RelayProfile queued = await SeedAsync(service, handler, "Queued", Relay2, ["queued"]);
        await EnableAsync(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.EnqueueAsync(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(ChatOk, Encoding.UTF8, "application/json") };
        });
        var work = service.RunDueInspectionRoundAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var moved = await service.SaveProfileAsync(queued with { BaseUrl = RelayBase }, null);
            queued = await service.SaveProfileAsync(moved with { BaseUrl = Relay2 }, null);
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("queued"));
            await service.RefreshModelsAsync(queued.Id, ModelConsent(queued));
            await EnableAsync(service); // Authorizes the next round only.
        }
        finally { release.TrySetResult(); }
        var round = await work;
        Check.Equal(1, round.Succeeded, "unaffected target completes");
        Check.Equal(1, round.Skipped, "old frozen target cannot be revived by the same address and newly cached model");
        Check.Equal(0, round.Failed, "not dispatched is not a failure");
        Check.Equal("first", RequestModel(handler.Requests.Single(r => r.Method == "POST")), "no POST to the changed target during the old round");
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk).EnqueueJson(HttpStatusCode.OK, ChatOk);
        var next = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(2, next.Succeeded, "freshly confirmed targets can run next time");
    }

    public static async Task AllCoolingTargetsKeepPreviousRound()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "First", RelayBase, ["first"]);
        await SeedAsync(service, handler, "Second", Relay2, ["second"]);
        await EnableAsync(service, 10);
        handler.EnqueueStatus((HttpStatusCode)429, r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(22)));
        handler.EnqueueStatus((HttpStatusCode)429, r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(20)));
        var round = await service.RunDueInspectionRoundAsync();
        var saved = await service.GetInspectionSnapshotAsync();
        Check.Equal(saved.Targets.Min(t => t.RetryNotBefore), saved.RetryNotBefore, "the next runnable host controls the wait");
        Check.True(saved.RetryNotBefore > saved.LastCompletedAt!.Value.AddMinutes(10), "both hosts outlast the next scheduled slot");
        // Set a persisted ended time so a real scheduler callback can arrive during both cooldowns.
        var store = new DesktopStore(tmp.Path, new ReversibleTestProtector());
        var state = await store.LoadAsync(default);
        state.Vault.LastInspectionCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        await store.SaveAsync(state, default);
        string file = Path.Combine(tmp.Path, "desktop-state.json");
        byte[] before = File.ReadAllBytes(file);
        int requests = handler.Count;
        await Check.ThrowsClientAsync(() => service.RunDueInspectionRoundAsync(), "ConnectionRateLimited", "all hosts cooling down does not begin a round");
        Check.Equal(requests, handler.Count, "no outbound work");
        Check.True(before.SequenceEqual(File.ReadAllBytes(file)), "no empty round written to disk");
        Check.Equal(round.RoundId, (await service.GetInspectionSnapshotAsync()).LatestRound!.RoundId, "previous round remains visible");
    }

    public static async Task FinalGateCancellationIsNotAConnectionFailure()
    {
        using var tmp = new TempDir();
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler, new CancelBeforeDispatchProtector(cancellation, "connectionHistory", "code", "Interrupted"));
        var profile = await SeedAsync(service, handler, "First", RelayBase, ["first"]);
        await EnableAsync(service);
        var store = new DesktopStore(tmp.Path, new ReversibleTestProtector());
        var loaded = await store.LoadAsync(default);
        var now = DateTimeOffset.UtcNow;
        loaded.Vault.ConnectionHistory = Enumerable.Range(1, 10_000).Select(age => new ConnectionTestResult
        {
            ProfileId = profile.Id, ProfileName = profile.Name, ModelId = "first", BaseUrl = profile.BaseUrl,
            KeyRevision = profile.KeyRevision, Protocol = profile.Protocol, Stage = ConnectionTestStage.ModelResponded,
            ModelResponded = true, StartedAt = now.AddSeconds(-age), CompletedAt = now.AddSeconds(-age)
        }).ToList();
        var originalIds = loaded.Vault.ConnectionHistory.Select(r => r.RecordId).ToArray();
        await store.SaveAsync(loaded, default);
        var round = await service.RunDueInspectionRoundAsync(cancellationToken: cancellation.Token);
        Check.True(cancellation.IsCancellationRequested && round.Cancelled, "round was cancelled after the prewrite");
        Check.Equal(1, round.Skipped, "no request in this round for the cancelled target");
        Check.Equal(0, round.Failed, "no bogus failed representative");
        var after = await service.GetConnectionHistoryAsync();
        Check.True(after.Select(r => r.RecordId).SequenceEqual(originalIds), "all 10,000 real records keep their IDs and order after cancellation");
        Check.False(handler.Requests.Any(r => r.Method == "POST"), "zero sends at final gate");
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        var completed = await service.RunDueInspectionRoundAsync();
        var retained = await service.GetConnectionHistoryAsync();
        Check.Equal(completed.RoundId, retained[0].RoundId!.Value, "a real result takes a history slot");
        Check.True(retained.Skip(1).Select(r => r.RecordId).SequenceEqual(originalIds.Take(9_999)), "a real result evicts exactly the oldest record");
    }

    // The connection history is independent from the Probe history, carries the documented fields,
    // exports without secrets and supports delete/clear.
    public static async Task I09_HistoryFieldsIsolationExportDelete()
    {
        using var tmp = new TempDir();
        using var exportDir = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await SeedAsync(service, handler, "Primary", RelayBase, ["m1"]);
        await EnableAsync(service);

        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        ConnectionTestResult manual = await service.TestConnectionAsync(profile.Id, "m1", ConnectionConsent(profile, "m1"));

        IReadOnlyList<ConnectionTestResult> history = await service.GetConnectionHistoryAsync();
        Check.Equal(2, history.Count, "inspection and manual test both record");
        ConnectionTestResult roundRecord = history.Single(r => r.Source == ConnectionRecordSource.RoundManual);
        ConnectionTestResult manualRecord = history.Single(r => r.Source == ConnectionRecordSource.Manual);
        Check.Equal(round.RoundId, roundRecord.RoundId, "round record references its round");
        Check.Null(manualRecord.RoundId, "manual record has no round");
        Check.Equal(manual.RecordId, manualRecord.RecordId, "manual record id is stable");
        Check.Equal("Primary", manualRecord.ProfileName, "profile name snapshot");
        Check.Equal("m1", manualRecord.ModelId, "model snapshot");
        Check.Equal(ApiProtocol.ChatCompletions, manualRecord.Protocol, "protocol snapshot");
        Check.Equal(profile.KeyRevision, manualRecord.KeyRevision, "key revision snapshot");
        Check.Equal(ConnectionTestStage.ModelResponded, manualRecord.Stage, "stage snapshot");
        Check.True(manualRecord.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-5), "start time recorded");
        Check.NotNull(manualRecord.CompletedAt, "completion time recorded");
        Check.True(manualRecord.ElapsedMilliseconds is >= 0, "elapsed time recorded");
        Check.Equal(ConnectionRecordSource.Manual, history[0].Source, "history is newest first");

        // Global inspection authorization must never replace the per-request consent of a manual test.
        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.TestConnectionAsync(profile.Id, "m1", null!),
            "ConsentRequired", "manual test still needs its own consent even when globally authorized");
        Check.Equal(before, handler.Count, "no request without the manual consent");
        Check.NotNull(await service.GetLastConnectionResultAsync(profile.Id), "last result of the profile is the manual test");

        // The Probe history is a different list and stays empty.
        Check.Equal(0, (await service.GetHistoryAsync()).Count, "inspector runs never create Probe records");

        string exportPath = Path.Combine(exportDir.Path, "connections.json");
        await service.ExportConnectionHistoryAsync(exportPath);
        Check.True(File.Exists(exportPath), "export file written");
        string exported = Fixtures.ReadAllText(exportPath);
        Check.Contains(exported, "\"m1\"", "export keeps the model id");
        Check.Contains(exported, "Primary", "export keeps the profile name");
        Check.NotContains(exported, Key, "export never contains a key");
        Check.NotContains(exported, "Bearer", "export never contains an auth header");
        Check.NotContains(exported, "relay.example.com", "export omits the relay address");
        Check.NotContains(exported, "sk-test", "export carries no credential-shaped text");

        await service.DeleteConnectionRecordAsync(manualRecord.RecordId);
        IReadOnlyList<ConnectionTestResult> afterDelete = await service.GetConnectionHistoryAsync();
        Check.Equal(1, afterDelete.Count, "delete removes exactly one record");
        Check.Equal(roundRecord.RecordId, afterDelete[0].RecordId, "the other record is kept");
        Check.Equal(0, (await service.GetHistoryAsync()).Count, "delete leaves the Probe history alone");

        await service.ClearConnectionHistoryAsync();
        Check.Equal(0, (await service.GetConnectionHistoryAsync()).Count, "clear removes all connection records");
        Check.Null(await service.GetLastConnectionResultAsync(profile.Id), "cleared profile keeps no cached result");
    }

    // I10 -------------------------------------------------------------------
    // A record deleted while its request is in flight must never be re-added by the completion.
    public static async Task I10_NoHistoryResurrectionOnCompletion()
    {
        using var tmp = new TempDir();
        var seedHandler = new FakeHttpHandler();
        Guid profileId;
        using (var seeding = New(tmp.Path, seedHandler))
        {
            RelayProfile profile = await SeedAsync(seeding, seedHandler, "Primary", RelayBase, ["m1"]);
            profileId = profile.Id;
            await EnableAsync(seeding);
        }

        var gate = new GatedHandler { BlockAtRequestNumber = 1 };
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        using var roundService = New(tmp.Path, gate);
        Task<InspectionRoundSummary> roundTask = roundService.RunInspectionRoundAsync(InspectionTrigger.Manual);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        using var control = New(tmp.Path, new FakeHttpHandler());
        IReadOnlyList<ConnectionTestResult> during = await control.GetConnectionHistoryAsync();
        Check.Equal(1, during.Count, "the pre-write record is visible while the request is in flight");
        Check.Equal(profileId, during[0].ProfileId, "pre-write record belongs to the round profile");
        Guid recordId = during[0].RecordId;
        await control.DeleteConnectionRecordAsync(recordId);
        Check.Equal(0, (await control.GetConnectionHistoryAsync()).Count, "record deleted mid-flight");

        gate.Release();
        await roundTask.WaitAsync(TimeSpan.FromSeconds(30));
        IReadOnlyList<ConnectionTestResult> afterCompletion = await control.GetConnectionHistoryAsync();
        Check.Equal(0, afterCompletion.Count, "completion does not resurrect the deleted record");
    }

    // I11 -------------------------------------------------------------------
    // A network request holds no long store lock, a second round is refused, and the completion never
    // overwrites a concurrent profile/key edit.
    public static async Task I11_ConcurrencyQueriesResponsiveRoundBusyConfigKept()
    {
        using var tmp = new TempDir();
        var seedHandler = new FakeHttpHandler();
        RelayProfile profile;
        using (var seeding = New(tmp.Path, seedHandler))
        {
            profile = await SeedAsync(seeding, seedHandler, "Primary", RelayBase, ["m1", "m2"]);
            await EnableAsync(seeding);
        }
        int originalRevision = profile.KeyRevision;

        var gate = new GatedHandler { BlockAtRequestNumber = 1 };
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        using var roundService = New(tmp.Path, gate);
        Task<InspectionRoundSummary> roundTask = roundService.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        using var control = New(tmp.Path, new FakeHttpHandler());
        // Local queries must stay responsive while the round waits for the network.
        IReadOnlyList<RelayProfile> profiles = await control.GetProfilesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(1, profiles.Count, "GetProfiles is not blocked by the in-flight network request");
        IReadOnlyList<ConnectionTestResult> during = await control.GetConnectionHistoryAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(1, during.Count, "GetConnectionHistory is not blocked by the in-flight network request");

        await Check.ThrowsClientAsync(() => control.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "RoundBusy", "a second round is refused instead of being queued");

        // Rotating the key while the round is in flight must survive the completion.
        RelayProfile rotated = await control.SaveProfileAsync(profile with { Name = "Rotated" }, Key2)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Check.True(rotated.KeyRevision > originalRevision, "key rotation committed during the round");
        Check.Equal(0, rotated.Models.Count, "key rotation cleared the cache as documented");

        gate.Release();
        InspectionRoundSummary round = await roundTask.WaitAsync(TimeSpan.FromSeconds(30));

        IReadOnlyList<RelayProfile> after = await control.GetProfilesAsync();
        Check.Equal(1, after.Count, "profile still present");
        Check.Equal("Rotated", after[0].Name, "concurrent rename not overwritten by the round");
        Check.Equal(rotated.KeyRevision, after[0].KeyRevision, "concurrent key rotation not overwritten");
        Check.Null(await control.GetLastConnectionResultAsync(profile.Id), "stale round result is not cached for the rotated key");

        IReadOnlyList<ConnectionTestResult> history = await control.GetConnectionHistoryAsync();
        ConnectionTestResult attempt = history.Single();
        Check.Equal(originalRevision, attempt.KeyRevision, "record keeps the revision that was actually used");
        Check.Equal(ConnectionRecordSource.Automatic, attempt.Source, "record keeps its source");
        Check.Equal(1, round.Succeeded, "the in-flight request still completed");
        Check.Equal(1, round.Skipped, "the second model was skipped because the target changed");
        Check.Equal(1, gate.Count, "the second model was never sent");
    }

    // I12 -------------------------------------------------------------------
    // Retention keeps the newest 10000 records and drops anything older than 30 days (fixture store).
    public static async Task I12_HistoryRetentionCapAndAge()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using (var service = New(tmp.Path, handler))
        {
            RelayProfile profile = await SeedAsync(service, handler, "Cap", RelayBase, ["m1"]);
            handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
            await service.TestConnectionAsync(profile.Id, "m1", ConnectionConsent(profile, "m1"));
            Check.Equal(1, (await service.GetConnectionHistoryAsync()).Count, "baseline record exists");
        }
        string primary = Path.Combine(tmp.Path, "desktop-state.json");
        var protector = new ReversibleTestProtector();
        JsonNode root = JsonNode.Parse(File.ReadAllText(primary))!;
        JsonNode vault = JsonNode.Parse(protector.Unprotect(
            Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        JsonNode template = vault["connectionHistory"]![0]!.DeepClone();

        DateTimeOffset newest = DateTimeOffset.UtcNow.AddMinutes(-1);
        JsonArray records = [];
        for (int i = 0; i < 5; i++)
        {
            JsonNode old = template.DeepClone();
            old["recordId"] = Guid.NewGuid().ToString();
            old["profileName"] = "OLD-RECORD";
            old["startedAt"] = newest.AddDays(-40).AddMinutes(-i).ToString("O");
            old["completedAt"] = newest.AddDays(-40).AddMinutes(-i).ToString("O");
            records.Add(old);
        }
        for (int i = 0; i < 10_005; i++)
        {
            JsonNode recent = template.DeepClone();
            recent["recordId"] = Guid.NewGuid().ToString();
            recent["startedAt"] = newest.AddSeconds(-i).ToString("O");
            recent["completedAt"] = newest.AddSeconds(-i).ToString("O");
            records.Add(recent);
        }
        vault["connectionHistory"] = records;
        root["protectedState"] = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString(StoreJson))));
        File.WriteAllText(primary, root.ToJsonString(StoreJson));

        var reopenedHandler = new FakeHttpHandler();
        using var reopened = New(tmp.Path, reopenedHandler);
        IReadOnlyList<ConnectionTestResult> history = await reopened.GetConnectionHistoryAsync();
        Check.Equal(10_000, history.Count, "retention caps the connection history at 10000 records");
        Check.False(history.Any(r => r.ProfileName == "OLD-RECORD"), "records older than 30 days are dropped");
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        Check.True(history.All(r => r.StartedAt >= cutoff), "every kept record is inside the 30 day window");
        Check.Equal(newest, history[0].StartedAt, "newest record is first");
        Check.Equal(newest.AddSeconds(-9_999), history[^1].StartedAt, "the oldest kept record is the 10000th newest");
        Check.Equal(10_000, history.Select(r => r.RecordId).Distinct().Count(), "kept record ids are unique");
        Check.Equal(0, reopenedHandler.Count, "reading the history performs no network request");
    }

    // I13 -------------------------------------------------------------------
    // The unknown pre-write record is committed before the request is sent; after an interruption a new
    // process sees it, does not resend, and the Probe history is untouched.
    public static async Task I13_PrewriteUnknownRecordSurvivesRestart()
    {
        using var tmp = new TempDir();
        var seedHandler = new FakeHttpHandler();
        using (var seeding = New(tmp.Path, seedHandler))
        {
            await SeedAsync(seeding, seedHandler, "Primary", RelayBase, ["m1", "m2"]);
            await EnableAsync(seeding);
        }

        var gate = new GatedHandler { BlockAtRequestNumber = 1 };
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        gate.EnqueueJson(HttpStatusCode.OK, ChatOk);
        using var roundService = New(tmp.Path, gate);
        Task<InspectionRoundSummary> roundTask = roundService.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        var restartHandler = new FakeHttpHandler();
        using (var restarted = New(tmp.Path, restartHandler))
        {
            IReadOnlyList<ConnectionTestResult> history = await restarted.GetConnectionHistoryAsync();
            Check.Equal(1, history.Count, "pre-write record is durable before the response");
            Check.Equal(ConnectionTestStage.Unknown, history[0].Stage, "pre-write stage is Unknown");
            Check.Equal("Interrupted", history[0].Code, "pre-write code marks the unknown outcome");
            Check.Null(history[0].CompletedAt, "pre-write record has no completion time");
            Check.Equal(ConnectionRecordSource.Automatic, history[0].Source, "pre-write keeps the round source");
            Check.NotNull(history[0].RoundId, "pre-write is bound to the round");
            Check.Equal(0, (await restarted.GetHistoryAsync()).Count, "inspection never writes Probe records");
        }
        Check.Equal(0, restartHandler.Count, "restart sends nothing and never resends the interrupted attempt");

        gate.Release();
        InspectionRoundSummary round = await roundTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check.True(round.Succeeded >= 1, "the blocked request completed after release");
        Check.Equal(2, round.Total, "the frozen scope is retained");
        ConnectionTestResult record = (await roundService.GetConnectionHistoryAsync())
            .Single(r => r.RoundId == round.RoundId && r.ModelId == "m1");
        Check.Equal(ConnectionTestStage.ModelResponded, record.Stage, "the pre-written record was updated in place when it completed");
        Check.NotNull(record.CompletedAt, "the completed record carries a completion time");
    }

    // I15 -------------------------------------------------------------------
    // A schema-2 vault written before this feature (no inspection keys, only the old single-target
    // session consent) loads as "off, nothing authorized" and is not promoted to a global approval.
    public static async Task I15_PreviousVaultLoadsWithoutPromotingOldConsent()
    {
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var seedHandler = new FakeHttpHandler();
        Guid profileId;
        using (var seeding = New(tmp.Path, seedHandler, protector))
        {
            RelayProfile profile = await SeedAsync(seeding, seedHandler, "Legacy Session", RelayBase, ["m1"]);
            profileId = profile.Id;
            // Old-style single-target automatic test approval: Enabled is session-only and never serialized.
            var settings = new ConnectionTestSettings { ProfileId = profile.Id, ModelId = "m1", IntervalMinutes = 60, Enabled = true };
            await seeding.SaveConnectionSettingsAsync(settings,
                new ConnectionScheduleConsent(ConnectionConsent(profile, "m1"), 60));
        }

        string primary = Path.Combine(tmp.Path, "desktop-state.json");
        var protector2 = protector;
        JsonNode root = JsonNode.Parse(File.ReadAllText(primary))!;
        Check.Equal(3, root["schemaVersion"]!.GetValue<int>(), "the seeded store is current schema");
        root["schemaVersion"] = 2; // This fixture represents the old pre-inspection release.
        JsonNode vault = JsonNode.Parse(protector2.Unprotect(
            Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        // Strip every field this feature introduced, reproducing a pre-inspection vault.
        foreach (string key in new[] { "inspectionSettings", "inspectionGrants", "inspectionHostDeadlines",
                     "inspectionNonce", "latestInspectionRound" })
            vault.AsObject().Remove(key);
        Check.False(vault.AsObject().ContainsKey("inspectionSettings"), "fixture really is a pre-feature vault");
        root["protectedState"] = Convert.ToBase64String(
            protector2.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString(StoreJson))));
        File.WriteAllText(primary, root.ToJsonString(StoreJson));

        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler, protector2);
        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        Check.False(snapshot.Settings.Enabled, "pre-feature vault loads with inspection off");
        Check.Equal(30, snapshot.Settings.IntervalMinutes, "pre-feature vault defaults to a 30 minute interval");
        Check.Null(snapshot.LatestRound, "no round is invented");
        InspectionTargetStatus status = snapshot.Targets.Single(t => t.Target.ProfileId == profileId);
        Check.Equal(InspectionAuthorizationState.PendingApproval, status.Authorization,
            "the old single-target session approval is not promoted to a global approval");
        Check.Equal(0, status.AuthorizedModels.Count, "no model is pre-authorized after the upgrade");
        Check.Null(status.AuthorizedAt, "no authorization timestamp is invented");

        // The old per-profile session settings still exist but do not enable a global round.
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "InspectionDisabled", "upgrade alone never starts a round");
        Check.Equal(0, handler.Count, "upgrade sends nothing");

        // A first save after the upgrade rewrites the envelope and keeps the old session data.
        InspectionSnapshot enabled = await EnableAsync(service);
        Check.True(enabled.Settings.Enabled, "explicit approval after upgrade works");
        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        Check.Equal(1, round.Succeeded, "approved scope runs after upgrade");
        Check.Equal(1, (await service.GetConnectionHistoryAsync()).Count, "the round records into the new history");
    }

    // I14 -------------------------------------------------------------------
    // Interval bounds and the exact consent semantics of a saved-off approval.
    public static async Task I14_IntervalBoundsAndSavedOffConsent()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        await SeedAsync(service, handler, "Primary", RelayBase, ["m1"]);
        InspectionSnapshot initial = await service.GetInspectionSnapshotAsync();
        InspectionSelection[] draft = AllSelections(initial);
        InspectionConsent stale = Fixtures.InspectionApproval(initial, draft, 20);

        foreach (int interval in new[] { 0, 1441, -5 })
            await Check.ThrowsClientAsync(
                () => service.ConfirmInspectionSettingsAsync(draft, stale with { IntervalMinutes = interval }),
                "InvalidInterval", "interval " + interval + " is out of range");

        // A fresh consent recorded while the switch is off stores the exact scope but must not enable it.
        InspectionSnapshot savedOff = await service.ConfirmInspectionSettingsAsync(draft, stale);
        Check.False(savedOff.Settings.Enabled, "consent does not turn the switch on");
        Check.Equal(20, savedOff.Settings.IntervalMinutes, "interval saved");
        Check.Equal(InspectionAuthorizationState.Authorized, savedOff.Targets[0].Authorization, "scope recorded");
        Check.Equal(1, savedOff.Targets[0].AuthorizedModels.Count, "model recorded");

        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "InspectionDisabled", "saved-off scope still refuses the timer round");
        Check.Equal(before, handler.Count, "saved-off scope sends nothing automatically");

        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary manual = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(1, manual.Succeeded, "saved-off scope can be run once manually");

        // Changing the interval needs its own fresh consent; the old interval-scoped grants no longer apply.
        await Check.ThrowsClientAsync(
            () => service.ConfirmInspectionSettingsAsync(draft, stale with { IntervalMinutes = 30 }), null,
            "interval change with a stale revision is refused");
        Check.Equal(20, (await service.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "stale confirmation leaves interval untouched");
        InspectionSnapshot changed = await Fixtures.ConfirmInspectionAsync(service, draft, 30, enable: true);
        Check.True(changed.Settings.Enabled, "enable at the new interval");
        Check.Equal(30, changed.Settings.IntervalMinutes, "new interval saved");
        Check.Equal(InspectionAuthorizationState.Authorized, changed.Targets[0].Authorization, "new interval is authorized");

        handler.EnqueueJson(HttpStatusCode.OK, ChatOk);
        InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
        Check.Equal(1, round.Succeeded, "enabled inspection runs at the new interval");
    }

    // ---- gated transport ---------------------------------------------------

    /// <summary>
    /// In-memory transport that can hold one request open so a round can be observed while it waits for
    /// the network. No socket is ever opened.
    /// </summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RecordedRequest> Requests { get; } = new();
        public int Count => Requests.Count;
        public int BlockAtRequestNumber { get; set; } = -1;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public GatedHandler EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                headers[header.Key] = header.Value.ToArray();
            Requests.Add(new RecordedRequest
            {
                Method = request.Method.Method,
                Uri = request.RequestUri ?? throw new InvalidOperationException("request URI missing"),
                Headers = headers,
                Body = body
            });

            if (Requests.Count == BlockAtRequestNumber)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_responses.Count == 0)
                throw new InvalidOperationException($"GatedHandler has no scripted response for {request.Method} {request.RequestUri}");
            return _responses.Dequeue()(request);
        }
    }
}
