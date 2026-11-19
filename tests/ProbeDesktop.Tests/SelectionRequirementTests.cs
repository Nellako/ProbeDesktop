using System.Net;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class SelectionRequirementTests
{
    public static async Task R01_ExplicitSelectionsIntervalAndLatestResults()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
        Check.Equal(30, (await service.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "new default 30");
        var profile = await service.SaveProfileAsync(new RelayProfile { Name = "P", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        OutboundConsent ModelsConsent() => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow);
        var noCache = await service.GetInspectionSnapshotAsync();
        Check.False(noCache.Targets.Single().HasModelCache, "never fetched is distinct");
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        profile = await service.RefreshModelsAsync(profile.Id, ModelsConsent());
        var snapshot = await service.GetInspectionSnapshotAsync();
        Check.False(snapshot.Selections.Single().IsSelected, "first fetch does not select profile");
        Check.Equal(0, snapshot.Selections.Single().SelectedModelIds.Count, "first fetch does not select models");
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Manual), "InspectionNotAuthorized", "empty selection sends no round");
        InspectionSelection[] selected = [new(profile.Id, true, ["m1"])];
        await Fixtures.ConfirmInspectionAsync(service, selected, enable: true);
        var empty = await Fixtures.ConfirmInspectionAsync(service, [new(profile.Id, false, ["m1"])]);
        Check.Equal("m1", (await service.GetInspectionSnapshotAsync()).Selections.Single().SelectedModelIds.Single(), "unchecking parent keeps children");
        Check.True(empty.Settings.Enabled, "empty confirmation preserves the on switch");
        int noSend = handler.Count;
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(), "InspectionNotAuthorized", "confirmed empty plan cannot send");
        Check.Equal(noSend, handler.Count, "empty confirmation and refused round use no HTTP");
        await Fixtures.ConfirmInspectionAsync(service, selected);
        await Check.ThrowsClientAsync(() => service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]),
            "ConsentRequired", "selection-only write cannot expand saved scope");
        handler.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
        var round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(1, round.Succeeded, "authorized model continues");
        Check.Equal(1, round.Total, "unconfirmed model never enters saved scope");
        var latest = await service.GetLatestResultsAsync(profile.Id, "m1");
        Check.True(latest.Connection!.ModelResponded, "completed direct result available");
        Check.Null(latest.Probe, "no completed Probe returns null");
        Check.Null((await service.GetLatestResultsAsync(profile.Id, "m2")).Connection, "skipped model has no result");
        await Check.ThrowsClientAsync(() => service.SaveInspectionIntervalAsync(45), "ConsentRequired", "interval-only mutation is refused");
        Check.Equal(30, (await service.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "legacy write cannot change the saved interval");
        var changed = await Fixtures.ConfirmInspectionAsync(service, selected, 45);
        Check.True(changed.Settings.Enabled, "interval save preserves enabled");
        Check.Equal(45, changed.Settings.IntervalMinutes, "interval saves with the authorized scope");
        await Check.ThrowsClientAsync(() => service.ConfirmInspectionSettingsAsync(selected, Fixtures.InspectionApproval(changed, selected, 9)),
            "InvalidInterval", "invalid interval refused");
        Check.Equal(45, (await service.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "invalid interval does not overwrite");
        for (int i = 0; i < 3; i++) handler.EnqueueStatus(HttpStatusCode.NotFound);
        await service.RunInspectionRoundAsync();
        Check.Equal(404, (await service.GetLatestResultsAsync(profile.Id, "m1")).Connection!.HttpStatusCode, "latest includes failure instead of stale success");
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, ModelsConsent()), "Http503", "failed refresh preserves selection");
        Check.Equal("m1", (await service.GetInspectionSnapshotAsync()).Selections.Single().SelectedModelIds.Single(), "failed cache refresh keeps selected ids");
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m2"));
        await service.RefreshModelsAsync(profile.Id, ModelsConsent());
        Check.Equal(0, (await service.GetInspectionSnapshotAsync()).Selections.Single().SelectedModelIds.Count, "successful refresh intersects ids");
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson());
        await service.RefreshModelsAsync(profile.Id, ModelsConsent());
        var emptyCache = await service.GetInspectionSnapshotAsync();
        Check.True(emptyCache.Targets.Single().HasModelCache, "successfully fetched empty remains a cache snapshot");
        Check.Equal(0, emptyCache.Targets.Single().Target.Models.Count, "fetched empty has zero models");
        await service.SaveInspectionSettingsAsync(new() { Enabled = false });
        await Fixtures.ConfirmInspectionAsync(service, [new(profile.Id, true, [])], 60);
        Check.False((await service.GetInspectionSnapshotAsync()).Settings.Enabled, "off interval save does not enable");
        using (var restarted = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector()))
            Check.True((await restarted.GetInspectionSnapshotAsync()).Selections.Single().IsSelected, "explicit selection survives restart");
        await service.DeleteProfileAsync(profile.Id);
        Check.Equal(0, (await service.GetInspectionSnapshotAsync()).Selections.Count, "profile deletion removes selection");
        Check.NotNull((await service.GetLatestResultsAsync(profile.Id, "m1")).Connection, "history survives profile deletion");
    }
    public static async Task R02_IntervalSaveKeepsInflightRound()
    {
        using var tmp = new TempDir();
        var seed = new FakeHttpHandler();
        RelayProfile profile;
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(seed), new ReversibleTestProtector()))
        {
            profile = await service.SaveProfileAsync(new RelayProfile { Name = "P", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
            seed.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
            profile = await service.RefreshModelsAsync(profile.Id, new(profile.Id, profile.KeyRevision, profile.BaseUrl,
                OutboundPurpose.ModelList, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow));
            await Fixtures.ConfirmInspectionAsync(service, [new(profile.Id, true, ["m1", "m2"])], enable: true);
        }
        using var transport = new IntervalGateHandler();
        using var running = new DesktopService(tmp.Path, new HttpClient(transport), new ReversibleTestProtector());
        using var control = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
        var task = running.RunInspectionRoundAsync();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Fixtures.ConfirmInspectionAsync(control, [new(profile.Id, true, ["m1"])], 40, attempts: 1);
        Check.False(transport.Cancelled, "interval save does not cancel current HTTP request");
        transport.Release.SetResult();
        var round = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Check.False(round.Cancelled, "interval save does not invalidate round nonce");
        Check.Equal(2, round.Succeeded, "both frozen authorized models finish after interval save");
        Check.Equal(2, transport.Count, "no retries and no queued request cancelled");
        Check.Equal(40, (await control.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "new frequency committed");
        var saved = await control.GetInspectionSnapshotAsync();
        Check.Equal(0, saved.Settings.MaxRetries, "new plan has zero retries");
        Check.Equal(round.CompletedAt, saved.LastCompletedAt, "the finished old round supplies the new schedule baseline");
    }

    /// <summary>
    /// The "确认巡检范围…" entry can save an exact-scope approval while the switch stays off:
    /// the grant is persisted, inspection is NOT enabled, and nothing is sent.
    /// Both services are checked because the two approvals are independent.
    /// </summary>
    public static async Task R04_OffStateScopeConfirmGrantsWithoutEnabling()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
        var profile = await service.SaveProfileAsync(new RelayProfile { Name = "P", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        profile = await service.RefreshModelsAsync(profile.Id, new(profile.Id, profile.KeyRevision, profile.BaseUrl,
            OutboundPurpose.ModelList, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow));
        InspectionSelection[] draft = [new(profile.Id, true, ["m1", "m2"])];

        int before = handler.Count;
        var initial = await service.GetInspectionSnapshotAsync();
        var consent = Fixtures.InspectionApproval(initial, draft);
        var connectionScope = consent.Scope.Single();
        // Confirm cannot widen the scope: a model outside the cached/selected set is rejected.
        await Check.ThrowsClientAsync(() => service.ConfirmInspectionSettingsAsync(draft,
                consent with { Scope = [connectionScope with { Models = [.. connectionScope.Models, "not-cached"] }] }),
            "ConsentRequired", "confirm rejects an out-of-scope model");
        var connection = await service.ConfirmInspectionSettingsAsync(draft, consent);
        Check.False(connection.Settings.Enabled, "connection confirm keeps inspection off");
        Check.Equal(30, connection.Settings.IntervalMinutes, "connection interval unchanged by confirm");
        Check.Equal(InspectionAuthorizationState.Authorized, connection.Targets.Single().Authorization, "off-state confirm records the connection grant");
        Check.Equal(2, connection.Targets.Single().AuthorizedModels.Count, "both selected models authorized");
        Check.True((await service.GetIdentityInspectionSnapshotAsync()).Targets.All(t => !t.Authorized),
            "the connection confirm does not grant identity inspection");

        var identity = await Fixtures.ConfirmIdentityAsync(service, [new(profile.Id, true, ["m1", "m2"])]);
        Check.False(identity.Settings.Enabled, "identity confirm keeps inspection off");
        Check.True(identity.Targets.All(t => t.Selected && t.Authorized), "off-state confirm records the identity grant");
        Check.Equal(before, handler.Count, "off-state confirm sends nothing");

        // A grant alone must never start automatic work; a manual connection round may use it once.
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Automatic),
            "InspectionDisabled", "grant alone does not let the connection timer run");
        await Check.ThrowsClientAsync(() => service.BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger.Automatic),
            "IdentityInspectionDisabled", "grant alone does not let the identity timer run");
        Check.Equal(before, handler.Count, "refused automatic rounds send nothing");

        handler.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
        handler.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
        var round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(2, round.Succeeded, "confirmed off-state grant is usable by a manual round");
        Check.False((await service.GetInspectionSnapshotAsync()).Settings.Enabled, "manual round still does not enable inspection");
    }

    /// <summary>
    /// Confirming an already-saved interval again (the new "确认巡检范围…" path) must not cancel the
    /// in-flight connection round, must not re-send, and must leave the interval value untouched.
    /// </summary>
    public static async Task R05_SameIntervalConfirmKeepsInflightRound()
    {
        using var tmp = new TempDir();
        var seed = new FakeHttpHandler();
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(seed), new ReversibleTestProtector()))
        {
            var profile = await service.SaveProfileAsync(new RelayProfile { Name = "P", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
            seed.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
            profile = await service.RefreshModelsAsync(profile.Id, new(profile.Id, profile.KeyRevision, profile.BaseUrl,
                OutboundPurpose.ModelList, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow));
            await Fixtures.ConfirmInspectionAsync(service, [new(profile.Id, true, ["m1", "m2"])], enable: true);
        }

        using var transport = new IntervalGateHandler();
        using var running = new DesktopService(tmp.Path, new HttpClient(transport), new ReversibleTestProtector());
        using var control = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
        var task = running.RunInspectionRoundAsync();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var confirmed = await Fixtures.ConfirmInspectionAsync(control, (await control.GetInspectionSnapshotAsync()).Selections);
        Check.True(confirmed.Settings.Enabled, "confirm does not change the switch");
        Check.Equal(30, confirmed.Settings.IntervalMinutes, "confirm does not change the interval");
        Check.False(transport.Cancelled, "confirm does not cancel the in-flight request");
        transport.Release.SetResult();
        var round = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Check.False(round.Cancelled, "confirm does not invalidate the round nonce");
        Check.Equal(2, round.Succeeded, "frozen target list still finishes");
        Check.Equal(2, transport.Count, "confirm adds no request and no retry");
    }

    private sealed class IntervalGateHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (Count == 1)
            {
                Entered.SetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    public static async Task R06_AtomicConfirmationFailureAndStaleDraft()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        var protector = new ToggleFailProtector();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        await service.RefreshModelsAsync(p.Id, Fixtures.ModelConsent(p));
        var oldConnection = await Fixtures.ConfirmInspectionAsync(service, [new(p.Id, true, ["m1"])], 25, attempts: 1, enable: true);
        var oldIdentity = await Fixtures.ConfirmIdentityAsync(service, [new(p.Id, true, ["m1"])], 35, enable: true);
        InspectionSelection[] newConnection = [new(p.Id, true, ["m2"])];
        IdentityInspectionSelection[] newIdentity = [new(p.Id, true, ["m2"])];
        var c = Fixtures.InspectionApproval(oldConnection, newConnection, 40, attempts: 2);
        var i = Fixtures.IdentityApproval(oldIdentity, newIdentity, 45);
        string path = Path.Combine(tmp.Path, "desktop-state.json");
        var before = File.ReadAllBytes(path);
        var backup = File.ReadAllBytes(path + ".bak");
        protector.FailProtect = true;
        await Check.ThrowsClientAsync(() => service.ConfirmInspectionSettingsAsync(newConnection, c), "StorageWrite", "connection confirmation fails atomically");
        await Check.ThrowsClientAsync(() => service.ConfirmIdentityInspectionSettingsAsync(newIdentity, i), "StorageWrite", "identity confirmation fails atomically");
        protector.FailProtect = false;
        Check.True(before.SequenceEqual(File.ReadAllBytes(path)), "failed saves leave primary byte-for-byte unchanged");
        Check.True(backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "failed saves leave previous backup unchanged");
        using var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var savedC = await reopened.GetInspectionSnapshotAsync();
        var savedI = await reopened.GetIdentityInspectionSnapshotAsync();
        Check.Equal(oldConnection.SettingsRevision, savedC.SettingsRevision, "connection confirmation remains current");
        Check.Equal(oldIdentity.SettingsRevision, savedI.SettingsRevision, "identity confirmation remains current");
        Check.Equal(25, savedC.Settings.IntervalMinutes, "connection interval remains");
        Check.Equal(0, savedC.Settings.MaxRetries, "connection retry count remains");
        Check.Equal(35, savedI.Settings.IntervalMinutes, "identity interval remains");
        Check.Equal("m1", savedC.Targets.Single().AuthorizedModels.Single(), "connection authorization remains");
        Check.Equal("m1", savedI.Targets.Single(t => t.Authorized).Target.ModelId, "identity authorization remains");
        Check.True(savedC.Settings.Enabled && savedI.Settings.Enabled, "switches remain on");
        Check.Null(savedC.LastCompletedAt, "saving is not a completed connection round");
        Check.Null(savedI.LastCompletedAt, "saving is not a completed identity round");
        var committedC = await reopened.ConfirmInspectionSettingsAsync(newConnection, c);
        var committedI = await reopened.ConfirmIdentityInspectionSettingsAsync(newIdentity, i);
        Check.NotEqual(c.SettingsRevision, committedC.SettingsRevision, "successful connection confirmation advances revision");
        Check.NotEqual(i.SettingsRevision, committedI.SettingsRevision, "successful identity confirmation advances revision");
        await Check.ThrowsClientAsync(() => reopened.ConfirmInspectionSettingsAsync(oldConnection.Selections,
            Fixtures.InspectionApproval(oldConnection, oldConnection.Selections, 25, attempts: 1)), "ConsentRequired", "stale connection dialog cannot roll back plan");
        await Check.ThrowsClientAsync(() => reopened.ConfirmIdentityInspectionSettingsAsync(oldIdentity.Selections,
            Fixtures.IdentityApproval(oldIdentity, oldIdentity.Selections, 35)), "ConsentRequired", "stale identity dialog cannot roll back plan");
        Check.Equal("m2", (await reopened.GetInspectionSnapshotAsync()).Targets.Single().AuthorizedModels.Single(), "connection commit survives stale write");
        Check.Equal("m2", (await reopened.GetIdentityInspectionSnapshotAsync()).Targets.Single(t => t.Authorized).Target.ModelId, "identity commit survives stale write");
        Check.Equal(1, handler.Count, "all save attempts and reloads send nothing beyond initial model fetch");
    }

}
