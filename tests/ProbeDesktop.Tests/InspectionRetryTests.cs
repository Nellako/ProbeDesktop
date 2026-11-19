using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class InspectionRetryTests
{
    private const string Ok = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";
    private static async Task<RelayProfile> Seed(DesktopService service, FakeHttpHandler handler, int attempts = 3)
    {
        RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m"));
        p = await service.RefreshModelsAsync(p.Id, new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(p.BaseUrl, p.Protocol), null, DateTimeOffset.UtcNow));
        await Fixtures.ConfirmInspectionAsync(service, [new(p.Id, true, ["m"])], attempts: attempts, enable: true);
        return p;
    }

    public static async Task ConfirmedRetryChoicesPersist()
    {
        foreach (int attempts in new[] { 1, 2, 3 })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using (var writer = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector()))
                await Seed(writer, handler, attempts);
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            var snapshot = await service.GetInspectionSnapshotAsync();
            Check.Equal(attempts - 1, snapshot.Settings.MaxRetries, "reopened setting stores retries, not attempts");
            foreach (int invalid in new[] { -1, 0, 4 })
                await Check.ThrowsClientAsync(() => service.ConfirmInspectionSettingsAsync(snapshot.Selections,
                    Fixtures.InspectionApproval(snapshot, snapshot.Selections, attempts: invalid)), "InvalidRetries", "attempts outside 1..3 rejected");
            Check.Equal(1, handler.Count, "confirmation, read and rejection sent nothing beyond model fetch");
            for (int i = 0; i < 3; i++) handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
            var round = await service.RunInspectionRoundAsync();
            Check.Equal(attempts, handler.Count - 1, "exact confirmed maximum across persistent restart");
            Check.Equal(1, round.Failed, "failed target counted once");
            Check.Equal(attempts, (await service.GetConnectionHistoryAsync()).Count, "only dispatched attempts in history");
        }
    }

    public static async Task BoundedAttemptsAndFinalResults()
    {
        foreach (InspectionTrigger trigger in Enum.GetValues<InspectionTrigger>())
        foreach (string outcome in new[] { "green", "second-green", "third-green", "possible", "failed", "unknown", "401", "403", "429" })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            await Seed(service, handler);
            int expected = outcome is "green" or "401" or "403" or "429" ? 1 : outcome == "second-green" ? 2 : 3;
            for (int i = 0; i < expected; i++)
            {
                bool green = outcome == "green" || outcome == "second-green" && i == 1 || outcome == "third-green" && i == 2;
                if (green) handler.EnqueueJson(HttpStatusCode.OK, Ok);
                else if (int.TryParse(outcome, out int status)) handler.EnqueueStatus((HttpStatusCode)status);
                else if (outcome == "unknown") handler.EnqueueException(new HttpRequestException("fake transport"));
                else handler.EnqueueJson(HttpStatusCode.OK, outcome == "possible" ? "{}" : "{\"error\":{}}");
            }
            InspectionRoundSummary round = await service.RunInspectionRoundAsync(trigger);
            Check.Equal(expected + 1, handler.Count, outcome + " bounded sends plus model fetch");
            var history = await service.GetConnectionHistoryAsync();
            Check.Equal(expected, history.Count, "each completed attempt persists");
            Check.Equal(1, round.Succeeded + round.Failed + round.UnknownCount + round.PossibleSuccessCount, "target counted only once");
            Check.Equal(0, round.Skipped, "completed target not skipped");
            var final = InspectionRoundResults.FinalConnections(history, round.RoundId).Single();
            // In this homogeneous/failed-then-green matrix, best equals the newest attempt.
            Check.Equal(history.OrderByDescending(r => r.CompletedAt).First().RecordId, final.RecordId, "matrix representative is the best result (newest for equal ranks)");
            ConnectionTestResult[] differentTargets = [final, final with { KeyRevision = final.KeyRevision + 1 },
                final with { BaseUrl = final.BaseUrl + "/other" }, final with { Protocol = ApiProtocol.AnthropicMessages },
                final with { ProfileId = Guid.NewGuid() }, final with { ModelId = "other" },
                final with { RoundId = Guid.NewGuid() }, final with { CompletedAt = null }];
            Check.Equal(6, InspectionRoundResults.FinalConnections(differentTargets, round.RoundId).Count, "precise target identity and completion/round filters never merge another target");
            if (outcome.Contains("green")) Check.Equal(1, round.Succeeded, "green terminates retry chain");
            if (outcome == "possible") Check.Equal(1, round.PossibleSuccessCount, "possible remains separate");
            Check.Equal(0, round.UnknownCount, "new rounds do not populate the legacy unknown counter");
            if (outcome == "unknown")
            {
                Check.Equal(1, round.Failed, "raw unknown is counted as failed for new rounds");
                Check.True(history.All(r => r.Stage == ConnectionTestStage.Unknown), "counting never rewrites each raw unknown attempt");
            }
        }
    }

    public static async Task CompletedAttemptSurvivesCancellation()
    {
        foreach (int cancelAfter in new[] { 1, 2, 3 })
        {
            using var tmp = new TempDir();
            using var cancellation = new CancellationTokenSource();
            var protector = new CompletionProtector(cancellation, cancelAfter);
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
            await Seed(service, handler);
            for (int i = 0; i < 3; i++) handler.EnqueueJson(HttpStatusCode.OK, "{}");
            protector.Armed = true;
            InspectionRoundSummary round = await service.RunInspectionRoundAsync(cancellationToken: cancellation.Token);
            Check.Equal(cancelAfter + 1, handler.Count, "cancel after committed result prevents another send");
            Check.Equal(1, round.PossibleSuccessCount, "last real result not lost at cancellation boundary");
            Check.Equal(0, round.Skipped, "no duplicate skipped count for finished target");
            Check.Equal(cancelAfter, (await service.GetConnectionHistoryAsync()).Count, "all real attempts retained");
        }
    }

    public static async Task ChangedScopeStopsRetries()
    {
        foreach (string change in new[] { "cache", "key", "disable" })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            RelayProfile profile = await Seed(service, handler);
            TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.EnqueueAsync(async (_, ct) =>
            {
                dispatched.SetResult();
                await release.Task.WaitAsync(ct);
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            });
            var running = service.RunInspectionRoundAsync();
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (change == "cache")
                {
                    handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson());
                    await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile)).WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (change == "key") await service.SaveProfileAsync(profile, Program.Key2).WaitAsync(TimeSpan.FromSeconds(5));
                else await service.SaveInspectionSettingsAsync(new() { Enabled = false }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.TrySetResult(); }
            InspectionRoundSummary round = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(1, handler.Requests.Count(r => r.Method == "POST"), change + " stops subsequent attempts");
            var history = await service.GetConnectionHistoryAsync();
            Check.Equal(1, history.Count, "completed attempt stays in history");
            Check.True(history[0].Stage is ConnectionTestStage.PossibleSuccess or ConnectionTestStage.Unknown,
                "scope-change boundary either completes the empty response or cancels its body read");
            Check.Equal(history[0].Stage == ConnectionTestStage.PossibleSuccess ? 1 : 0, round.PossibleSuccessCount,
                "counter follows actual completion boundary, not an assumed cancellation time");
            Check.Equal(history[0].Stage == ConnectionTestStage.Unknown ? 1 : 0, round.Failed,
                "cancelled read counts failed while its raw Unknown stage stays intact");
            Check.Equal(0, round.UnknownCount, "new cancellation summary does not populate legacy unknown counter");
            Check.Equal(0, round.Skipped, "no double count after scope change");
        }
    }

    /// <summary>单次手动直连测试不属于巡检重试：非绿色结果也只发送一次。</summary>
    public static async Task SingleManualConnectionNeverRetries()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            handler.EnqueueStatus(status);
            handler.EnqueueJson(HttpStatusCode.OK, Ok); // 若发生重试才会被消费
            ConnectionTestResult result = await service.TestConnectionAsync(p.Id, "m", new OutboundConsent(
                p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ConnectionTest,
                ConnectionTestEndpoints.Recipient(p.BaseUrl, p.Protocol), "m", DateTimeOffset.UtcNow, p.Protocol));
            Check.Equal(1, handler.Count, status + " manual single test sends exactly once");
            Check.Equal(status, (HttpStatusCode)result.HttpStatusCode!.Value, "manual result keeps its own status");
            Check.Equal(1, (await service.GetConnectionHistoryAsync()).Count, "manual single test writes one record");
            Check.NotContains(result.Message, "本轮同目标最多三次调用", "manual record is not labelled as an inspection attempt");
        }
    }

    /// <summary>重试链在第 2 次遇到 401/403 时立即停止：保留该次真实记录并暂停配置，不补第 3 次。</summary>
    public static async Task AuthFailureMidChainStopsAndPauses()
    {
        foreach (HttpStatusCode authStatus in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            await Seed(service, handler);
            int seedCount = handler.Count;
            handler.EnqueueJson(HttpStatusCode.InternalServerError, "{\"error\":{}}");
            handler.EnqueueStatus(authStatus);
            handler.EnqueueJson(HttpStatusCode.OK, Ok); // 不得被消费

            InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Automatic);
            Check.Equal(2, handler.Count - seedCount, authStatus + " stops at the auth failure, no third try");
            Check.Equal(1, round.Failed, "the target is counted once, by its final attempt");
            Check.Equal(0, round.Succeeded + round.PossibleSuccessCount + round.UnknownCount + round.Skipped, "no double count");
            var history = await service.GetConnectionHistoryAsync();
            Check.Equal(1, InspectionRoundResults.FinalConnections(history, round.RoundId).Count, "one final row for the target");
            ConnectionTestResult first = history.OrderBy(r => r.CompletedAt).First();
            ConnectionTestResult final = InspectionRoundResults.FinalConnections(history, round.RoundId).Single();
            Check.True(first.Message.Contains("本轮同目标最多发 3 次请求", StringComparison.Ordinal),
                "the intermediate 5xx record discloses the retry envelope");
            Check.Equal((int)authStatus, final.HttpStatusCode, "final record is the auth failure");
            Check.NotContains(final.Message, "本轮同目标最多", "auth/rate-limit messages are not overwritten by the retry envelope");
            Check.Equal(InspectionAuthorizationState.Paused,
                (await service.GetInspectionSnapshotAsync()).Targets.Single().Authorization, "auth failure pauses the profile");
            await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Manual), "InspectionNotAuthorized",
                "a paused profile sends nothing in the next round");
        }
    }

    /// <summary>界面静态文案披露可选重试、默认与请求次数，不承诺费用上限。</summary>
    public static async Task DisclosureCopyPromisesNoCostCeiling()
    {
        await Task.CompletedTask;
        string repo = RepositoryRoot();
        foreach ((string file, bool fullDisclosure) in new[]
        {
            (Path.Combine(repo, "src", "ProbeDesktop.App", "Views", "InspectionView.xaml"), false),
            (Path.Combine(repo, "src", "ProbeDesktop.App", "Views", "HelpView.xaml"), true),
        })
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            Check.Contains(text, "最大重试", name + " distinguishes retries from total requests");
            Check.NotContains(text, "最多再重试两次（共 3 次）", name + " must not imply every plan has three attempts");
            Check.NotContains(text, "费用上限为", name + " must not promise a cost ceiling");
            Check.NotContains(text, "费用上限是", name + " must not promise a cost ceiling");
            Check.NotContains(text, "费用估算也按", name + " must not publish a cost estimate");
            Check.NotContains(text, "单次请求量的 3 倍", name + " must not multiply the request volume by the wrong base");
            if (!fullDisclosure) continue;
            Check.Contains(text, "0、1、2 次", name + " enumerates the available retry values");
            Check.Contains(text, "默认 0", name + " explains the safe default");
            Check.Contains(text, "1、2、3 次请求", name + " distinguishes actual attempts from selected retries");
            Check.Contains(text, "不是费用上限", name + " disclaims a price cap");
            Check.Contains(text, "旧授权", name + " states legacy authorization needs re-approval");
            Check.Contains(text, "身份识别巡检", name + " keeps identity inspection distinguished");
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(caller)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProbeDesktop.slnx"))) dir = dir.Parent;
        if (dir is null) throw new TestFailure("repository root not found from " + caller);
        return dir.FullName;
    }

    public static async Task LegacyAuthorizationRequiresFreshConsent()
    {
        foreach (bool explicitOneAttempt in new[] { false, true })
        {
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var handler = new FakeHttpHandler();
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            await Seed(service, handler, attempts: 1);
            handler.EnqueueJson(HttpStatusCode.OK, Ok);
            await service.RunInspectionRoundAsync();
        }
        string path = Path.Combine(tmp.Path, "desktop-state.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode vault = JsonNode.Parse(protector.Unprotect(Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        vault.AsObject().Remove("inspectionSettingsRevision");
        vault.AsObject().Remove("inspectionSettingsVersion");
        vault.AsObject().Remove("inspectionUpgradePending");
        vault.AsObject().Remove("lastInspectionCompletedAt");
        vault["inspectionSettings"]!.AsObject().Remove("maxRetries");
        vault["inspectionSettings"]!["intervalMinutes"] = 47;
        vault["latestInspectionRound"]!.AsObject().Remove("intervalMinutes");
        vault["latestInspectionRound"]!.AsObject().Remove("maxRetries");
        if (!explicitOneAttempt) root["schemaVersion"] = 2;
        foreach (JsonNode? grant in vault["inspectionGrants"]!.AsArray())
        {
            grant!["intervalMinutes"] = 47;
            if (!explicitOneAttempt) grant.AsObject().Remove("maxAttempts");
        }
        root["protectedState"] = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString())));
        File.WriteAllText(path, root.ToJsonString());
        using var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var snapshot = await reopened.GetInspectionSnapshotAsync();
        Check.True(snapshot.Settings.Enabled, "old preference preserved without granting sends");
        Check.True(snapshot.NeedsUpgradeConfirmation, "old plan requires confirmation even with a one-attempt grant");
        Check.Equal(47, snapshot.Settings.IntervalMinutes, "legacy interval survives");
        Check.Equal(0, snapshot.Settings.MaxRetries, "legacy plan does not acquire additional retries");
        Check.Equal("m", snapshot.Selections.Single().SelectedModelIds.Single(), "legacy selection survives");
        Check.Equal(1, (await reopened.GetConnectionHistoryAsync()).Count, "old history survives");
        Check.Equal(snapshot.LatestRound!.CompletedAt, snapshot.LastCompletedAt, "genuine previous completion survives migration");
        Check.Equal(InspectionAuthorizationState.PendingApproval, snapshot.Targets.Single().Authorization, "old one-attempt grant is pending");
        foreach (InspectionTrigger trigger in Enum.GetValues<InspectionTrigger>())
            await Check.ThrowsClientAsync(() => reopened.RunInspectionRoundAsync(trigger), "InspectionNotAuthorized", "legacy grant cannot send");
        Check.Equal(2, handler.Count, "legacy grant blocks both triggers without HTTP");
        await Fixtures.ConfirmInspectionAsync(reopened, snapshot.Selections, 47, attempts: 1);
        Check.False((await reopened.GetInspectionSnapshotAsync()).NeedsUpgradeConfirmation, "fresh exact confirmation clears the upgrade gate");
        handler.EnqueueJson(HttpStatusCode.OK, Ok);
        Check.Equal(1, (await reopened.RunInspectionRoundAsync()).Succeeded, "fresh explicit approval works");
        }
    }

    private sealed class CompletionProtector(CancellationTokenSource cancellation, int cancelAfter) : ISecretProtector
    {
        private readonly ReversibleTestProtector inner = new();
        public bool Armed { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            JsonNode vault = JsonNode.Parse(plaintext)!;
            if (Armed && vault["connectionHistory"]!.AsArray().Count(r => r!["completedAt"] is not null) >= cancelAfter)
            {
                Armed = false;
                cancellation.Cancel();
            }
            return inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext) => inner.Unprotect(ciphertext);
    }
}
