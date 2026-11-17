using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class InspectionRetryTests
{
    private const string Ok = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";
    private static async Task<RelayProfile> Seed(DesktopService service, FakeHttpHandler handler)
    {
        RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m"));
        p = await service.RefreshModelsAsync(p.Id, new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(p.BaseUrl, p.Protocol), null, DateTimeOffset.UtcNow));
        await service.SaveInspectionSelectionsAsync([new(p.Id, true, ["m"])]);
        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        await service.SaveInspectionSettingsAsync(new() { Enabled = true },
            new(snapshot.Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        return p;
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
            Check.Equal(history.OrderByDescending(r => r.CompletedAt).First().RecordId, final.RecordId, "helper selects final not first failure");
            ConnectionTestResult[] differentTargets = [final, final with { KeyRevision = final.KeyRevision + 1 },
                final with { BaseUrl = final.BaseUrl + "/other" }, final with { Protocol = ApiProtocol.AnthropicMessages },
                final with { ProfileId = Guid.NewGuid() }, final with { ModelId = "other" },
                final with { RoundId = Guid.NewGuid() }, final with { CompletedAt = null }];
            Check.Equal(6, InspectionRoundResults.FinalConnections(differentTargets, round.RoundId).Count, "precise target identity and completion/round filters never merge another target");
            if (outcome.Contains("green")) Check.Equal(1, round.Succeeded, "green terminates retry chain");
            if (outcome == "possible") Check.Equal(1, round.PossibleSuccessCount, "possible remains separate");
            if (outcome == "unknown") Check.Equal(1, round.UnknownCount, "unknown remains unknown");
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
        foreach (string change in new[] { "selection", "key", "disable" })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            RelayProfile profile = await Seed(service, handler);
            handler.Enqueue(_ =>
            {
                if (change == "selection") service.SaveInspectionSelectionsAsync([new(profile.Id, false, ["m"])]).GetAwaiter().GetResult();
                else if (change == "key") service.SaveProfileAsync(profile, Program.Key2).GetAwaiter().GetResult();
                else service.SaveInspectionSettingsAsync(new() { Enabled = false }).GetAwaiter().GetResult();
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            });
            InspectionRoundSummary round = await service.RunInspectionRoundAsync();
            Check.Equal(2, handler.Count, change + " stops subsequent attempts");
            Check.Equal(1, round.PossibleSuccessCount + round.UnknownCount, "last actual response retained even if cancellation interrupted body read");
            Check.Equal(0, round.Skipped, "no double count after scope change");
            Check.Equal(1, (await service.GetConnectionHistoryAsync()).Count, "completed attempt stays in history");
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
            Check.True(first.Message.Contains("本轮同目标最多三次调用", StringComparison.Ordinal),
                "the intermediate 5xx record discloses the retry envelope");
            Check.Equal((int)authStatus, final.HttpStatusCode, "final record is the auth failure");
            Check.NotContains(final.Message, "本轮同目标最多三次调用", "auth/rate-limit messages are not overwritten by the retry envelope");
            Check.Equal(InspectionAuthorizationState.Paused,
                (await service.GetInspectionSnapshotAsync()).Targets.Single().Authorization, "auth failure pauses the profile");
            await Check.ThrowsClientAsync(() => service.RunInspectionRoundAsync(InspectionTrigger.Manual), "InspectionNotAuthorized",
                "a paused profile sends nothing in the next round");
        }
    }

    /// <summary>界面静态文案（帮助页、连接巡检页）只承诺请求次数的 3 倍，不承诺费用上限。</summary>
    public static async Task DisclosureCopyPromisesNoCostCeiling()
    {
        await Task.CompletedTask;
        string repo = RepositoryRoot();
        foreach ((string file, string needle, bool fullDisclosure) in new[]
        {
            (Path.Combine(repo, "src", "ProbeDesktop.App", "Views", "InspectionView.xaml"), "最多再重试两次（共 3 次）", false),
            (Path.Combine(repo, "src", "ProbeDesktop.App", "Views", "HelpView.xaml"), "最多再重试两次", true),
        })
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            Check.Contains(text, needle, name + " still discloses the bounded retries");
            Check.Contains(text, "3 倍", name + " states the worst-case request multiplier");
            Check.NotContains(text, "费用上限为", name + " must not promise a cost ceiling");
            Check.NotContains(text, "费用上限是", name + " must not promise a cost ceiling");
            Check.NotContains(text, "费用估算也按", name + " must not publish a cost estimate");
            Check.Contains(text, "实际费用按中转站计价", name + " defers the real cost to the relay's pricing");
            Check.NotContains(text, "单次请求量的 3 倍", name + " must not multiply the request volume by the wrong base");
            if (!fullDisclosure) continue;
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
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var handler = new FakeHttpHandler();
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector)) await Seed(service, handler);
        string path = Path.Combine(tmp.Path, "desktop-state.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode vault = JsonNode.Parse(protector.Unprotect(Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        root["schemaVersion"] = 2; // Previous one-attempt release, before the schema-3 safety contract.
        foreach (JsonNode? grant in vault["inspectionGrants"]!.AsArray()) grant!.AsObject().Remove("maxAttempts");
        root["protectedState"] = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString())));
        File.WriteAllText(path, root.ToJsonString());
        using var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var snapshot = await reopened.GetInspectionSnapshotAsync();
        Check.True(snapshot.Settings.Enabled, "old preference preserved without granting sends");
        Check.Equal(InspectionAuthorizationState.PendingApproval, snapshot.Targets.Single().Authorization, "old one-attempt grant is pending");
        foreach (InspectionTrigger trigger in Enum.GetValues<InspectionTrigger>())
            await Check.ThrowsClientAsync(() => reopened.RunInspectionRoundAsync(trigger), "InspectionNotAuthorized", "legacy grant cannot send");
        InspectionConsent legacy = new(snapshot.Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow);
        await Check.ThrowsClientAsync(() => reopened.SaveInspectionSettingsAsync(new() { Enabled = true }, legacy), "ConsentRequired", "old consent cannot silently gain retries");
        await reopened.SaveInspectionSettingsAsync(new() { Enabled = true }, legacy with { MaxAttempts = 3 });
        handler.EnqueueJson(HttpStatusCode.OK, Ok);
        Check.Equal(1, (await reopened.RunInspectionRoundAsync()).Succeeded, "fresh explicit approval works");
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
