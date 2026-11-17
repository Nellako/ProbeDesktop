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
        await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1"])]);
        var target = snapshot.Targets.Single().Target with { Models = ["m1"] };
        await service.SaveInspectionSettingsAsync(new() { Enabled = true, IntervalMinutes = 30 }, new([target], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        await service.SaveInspectionSelectionsAsync([new(profile.Id, false, ["m1"])]);
        Check.Equal("m1", (await service.GetInspectionSnapshotAsync()).Selections.Single().SelectedModelIds.Single(), "unchecking parent keeps children");
        await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]);
        handler.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
        var round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        Check.Equal(1, round.Succeeded, "authorized model continues");
        Check.Equal(1, round.Skipped, "newly selected model never expands grant");
        var latest = await service.GetLatestResultsAsync(profile.Id, "m1");
        Check.True(latest.Connection!.ModelResponded, "completed direct result available");
        Check.Null(latest.Probe, "no completed Probe returns null");
        Check.Null((await service.GetLatestResultsAsync(profile.Id, "m2")).Connection, "skipped model has no result");
        var changed = await service.SaveInspectionIntervalAsync(45);
        Check.True(changed.Settings.Enabled, "interval save preserves enabled");
        Check.Equal(45, changed.Settings.IntervalMinutes, "interval saves independently");
        await Check.ThrowsClientAsync(() => service.SaveInspectionIntervalAsync(9), "InvalidInterval", "invalid interval refused");
        Check.Equal(45, (await service.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "invalid interval does not overwrite");
        await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(), "InspectionNotAuthorized", "frequency change needs fresh consent before next round");
        await service.SaveInspectionIntervalAsync(45, new([target with { Models = ["m1", "m2"] }], 45, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1"])]);
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
        var empty = await service.GetInspectionSnapshotAsync();
        Check.True(empty.Targets.Single().HasModelCache, "successfully fetched empty remains a cache snapshot");
        Check.Equal(0, empty.Targets.Single().Target.Models.Count, "fetched empty has zero models");
        await service.SaveInspectionSettingsAsync(new() { Enabled = false });
        await service.SaveInspectionIntervalAsync(60);
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
            await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]);
            await service.SaveInspectionSettingsAsync(new() { Enabled = true }, new([(await service.GetInspectionSnapshotAsync()).Targets.Single().Target], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        }
        using var transport = new IntervalGateHandler();
        using var running = new DesktopService(tmp.Path, new HttpClient(transport), new ReversibleTestProtector());
        using var control = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
        var task = running.RunInspectionRoundAsync();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var target = (await control.GetInspectionSnapshotAsync()).Targets.Single().Target;
        await control.SaveInspectionIntervalAsync(40, new([target], 40, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        Check.False(transport.Cancelled, "interval save does not cancel current HTTP request");
        transport.Release.SetResult();
        var round = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Check.False(round.Cancelled, "interval save does not invalidate round nonce");
        Check.Equal(2, round.Succeeded, "both frozen authorized models finish after interval save");
        Check.Equal(2, transport.Count, "no retries and no queued request cancelled");
        Check.Equal(40, (await control.GetInspectionSnapshotAsync()).Settings.IntervalMinutes, "new frequency committed");
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
        await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]);
        await service.SaveIdentityInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]);

        int before = handler.Count;
        var connectionScope = (await service.GetInspectionSnapshotAsync()).Targets.Single().Target;
        // Confirm cannot widen the scope: a model outside the cached/selected set is rejected.
        await Check.ThrowsClientAsync(() => service.SaveInspectionIntervalAsync(30,
                new([connectionScope with { Models = [.. connectionScope.Models, "not-cached"] }], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 }),
            "ConsentRequired", "interval-path confirm rejects an out-of-scope model");
        var connection = await service.SaveInspectionIntervalAsync(30,
            new([connectionScope], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        Check.False(connection.Settings.Enabled, "connection confirm keeps inspection off");
        Check.Equal(30, connection.Settings.IntervalMinutes, "connection interval unchanged by confirm");
        Check.Equal(InspectionAuthorizationState.Authorized, connection.Targets.Single().Authorization, "off-state confirm records the connection grant");
        Check.Equal(2, connection.Targets.Single().AuthorizedModels.Count, "both selected models authorized");
        Check.True((await service.GetIdentityInspectionSnapshotAsync()).Targets.All(t => !t.Authorized),
            "the connection confirm does not grant identity inspection");

        var identity = await service.SaveIdentityInspectionIntervalAsync(30,
            new((await service.GetIdentityInspectionSnapshotAsync()).Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow));
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
            await service.SaveInspectionSelectionsAsync([new(profile.Id, true, ["m1", "m2"])]);
            await service.SaveInspectionSettingsAsync(new() { Enabled = true, IntervalMinutes = 30 },
                new([(await service.GetInspectionSnapshotAsync()).Targets.Single().Target], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        }

        using var transport = new IntervalGateHandler();
        using var running = new DesktopService(tmp.Path, new HttpClient(transport), new ReversibleTestProtector());
        using var control = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
        var task = running.RunInspectionRoundAsync();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var target = (await control.GetInspectionSnapshotAsync()).Targets.Single().Target;
        var confirmed = await control.SaveInspectionIntervalAsync(30, new([target], 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
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

}
