using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>v0.9.2 用户契约回归。网络仅使用内存 handler，持久化仅使用独立临时目录。</summary>
public static class BestRoundResultVerificationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddHours(-1);
    private static readonly Guid ProfileId = Guid.NewGuid();
    private const string Ok = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";

    private static ConnectionTestResult Record(Guid round, ConnectionTestStage stage, int second) => new()
    {
        ProfileId = ProfileId, ProfileName = "Verifier", ModelId = "m", KeyRevision = 1,
        BaseUrl = "https://relay.example.com/v1", Protocol = ApiProtocol.ChatCompletions,
        Source = ConnectionRecordSource.Automatic, RoundId = round,
        Stage = stage, ModelResponded = stage == ConnectionTestStage.ModelResponded,
        StartedAt = T0.AddSeconds(second), CompletedAt = T0.AddSeconds(second + 1),
    };

    public static Task BestBeatsLaterFailureAndSameRankUsesNewest()
    {
        Guid round = Guid.NewGuid();
        // Explicit expected record indices are a requirement oracle, not a call back into production ranking.
        foreach (var (stages, expected) in new (ConnectionTestStage[], int)[]
        {
            ([ConnectionTestStage.ModelResponded, ConnectionTestStage.Failed, ConnectionTestStage.Unknown], 0),
            ([ConnectionTestStage.PossibleSuccess, ConnectionTestStage.Failed, ConnectionTestStage.Unknown], 0),
            ([ConnectionTestStage.PossibleSuccess, ConnectionTestStage.ModelResponded, ConnectionTestStage.Failed], 1),
            ([ConnectionTestStage.ModelResponded, ConnectionTestStage.Failed, ConnectionTestStage.ModelResponded], 2),
            ([ConnectionTestStage.PossibleSuccess, ConnectionTestStage.PossibleSuccess, ConnectionTestStage.Failed], 1),
            ([ConnectionTestStage.Unknown, ConnectionTestStage.Failed, ConnectionTestStage.Unknown], 2),
            ([ConnectionTestStage.Failed, ConnectionTestStage.Unknown, ConnectionTestStage.Failed], 2),
        })
        {
            var records = stages.Select((s, i) => Record(round, s, i * 10)).ToArray();
            foreach (var input in new[] { records, records.Reverse().ToArray(), new[] { records[1], records[2], records[0] } })
            {
                string label = string.Join(",", stages);
                Check.Equal(records[expected].RecordId, InspectionRoundResults.FinalConnections(input, round).Single().RecordId,
                    label + ": Core selects best, then most recently completed within rank, independent of list order");
                var row = ConnectionRoundDisplay.LatestPerTarget(input, round).Single();
                Check.Equal(records[expected].RecordId, row.Record.RecordId, label + ": latest-round UI selects same requirement result");
                Check.Equal(3, row.Attempts, label + ": all attempts still counted");
                string note = ConnectionRoundDisplay.AttemptNote(row);
                Check.True(note.Contains("最高状态", StringComparison.Ordinal) || note.Contains("最佳", StringComparison.Ordinal),
                    "attempt note discloses best-result selection");
                Check.Equal(3, input.Length, "selection is non-destructive");
            }
        }
        return Task.CompletedTask;
    }

    public static Task ExactTargetAndRoundIsolation()
    {
        Guid round = Guid.NewGuid();
        var failed = Record(round, ConnectionTestStage.Failed, 20);
        var oldSuccess = Record(Guid.NewGuid(), ConnectionTestStage.ModelResponded, 10);
        var otherTargets = new[]
        {
            Record(round, ConnectionTestStage.ModelResponded, 30) with { ModelId = "other" },
            Record(round, ConnectionTestStage.ModelResponded, 40) with { ProfileId = Guid.NewGuid() },
            Record(round, ConnectionTestStage.ModelResponded, 50) with { KeyRevision = 2 },
            Record(round, ConnectionTestStage.ModelResponded, 60) with { BaseUrl = "https://other.example.com/v1" },
            Record(round, ConnectionTestStage.ModelResponded, 70) with { Protocol = ApiProtocol.AnthropicMessages },
        };
        ConnectionTestResult[] history = [oldSuccess, failed, .. otherTargets,
            Record(round, ConnectionTestStage.ModelResponded, 80) with { Source = ConnectionRecordSource.Manual }];
        var final = InspectionRoundResults.FinalConnections(history, round);
        Check.Equal(6, final.Count, "profile, model, revision, address and protocol are five separate dimensions; manual is excluded");
        Check.True(final.Any(r => r.RecordId == failed.RecordId), "old round and other targets cannot lend a success to current failed target");
        Check.False(final.Any(r => r.RecordId == oldSuccess.RecordId), "old success is not part of new round");
        var display = ConnectionRoundDisplay.LatestPerTarget([failed, oldSuccess, .. otherTargets], round);
        Check.Equal(6, display.Count, "display also isolates all exact target dimensions");
        Check.True(display.Any(r => r.Record.RecordId == failed.RecordId), "display never masks current failure with old success");
        return Task.CompletedTask;
    }

    public static async Task RealRoundsCountersHistoryDeletionAndCeiling()
    {
        foreach (InspectionTrigger trigger in Enum.GetValues<InspectionTrigger>())
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            RelayProfile profile = await Seed(service, handler);
            handler.EnqueueJson(HttpStatusCode.OK, "{}");
            handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
            handler.EnqueueStatus(HttpStatusCode.InternalServerError);
            InspectionRoundSummary first = await service.RunInspectionRoundAsync(trigger);
            Check.Equal(4, handler.Count, "exactly three sends plus initial model fetch, never a fourth attempt");
            Check.Equal(1, first.PossibleSuccessCount, "possible then two failures counts as possible, not last failure");
            Check.Equal(0, first.Succeeded + first.Failed + first.UnknownCount + first.Skipped, "only one target is counted");
            var history = await service.GetConnectionHistoryAsync();
            Check.Equal(3, history.Count, "all attempt records retained");
            var best = history.Single(r => r.Stage == ConnectionTestStage.PossibleSuccess);
            Check.Equal(best.RecordId, (await service.GetLatestResultsAsync(profile.Id, "m")).Connection!.RecordId,
                "model recent result uses this round's best rather than last failure");
            Check.Equal(best.RecordId, (await service.GetLastConnectionResultAsync(profile.Id))!.RecordId,
                "profile last result agrees with best result of current round");

            handler.EnqueueJson(HttpStatusCode.OK, Ok);
            InspectionRoundSummary green = await service.RunInspectionRoundAsync(trigger);
            Check.Equal(1, green.Succeeded, "success terminates retries after one request");
            Check.Equal(5, handler.Count, "successful round sends only once");
            handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
            handler.EnqueueStatus(HttpStatusCode.InternalServerError);
            handler.EnqueueStatus(HttpStatusCode.NotFound);
            InspectionRoundSummary failed = await service.RunInspectionRoundAsync(trigger);
            Check.Equal(1, failed.Failed, "new all-failed round cannot borrow prior green round");
            Check.Equal(0, failed.Succeeded + failed.PossibleSuccessCount + failed.UnknownCount, "new round has no successes");
            Check.Equal(8, handler.Count, "all-failed round consumes exactly three attempts");
            var newest = (await service.GetLatestResultsAsync(profile.Id, "m")).Connection!;
            Check.Equal(failed.RoundId, newest.RoundId, "model recent result is bound to newest round");
            Check.Equal(404, newest.HttpStatusCode, "all-failed tie selects the latest failure");
            Check.Equal(newest.RecordId, (await service.GetLastConnectionResultAsync(profile.Id))!.RecordId,
                "profile last result also respects the new all-failed round");
            history = await service.GetConnectionHistoryAsync();
            Check.Equal(7, history.Count, "every attempt in all three rounds is retained");
            await service.DeleteConnectionRecordAsync(best.RecordId);
            Check.Equal(6, (await service.GetConnectionHistoryAsync()).Count, "delete removes only requested earlier attempt");
            await service.DeleteProfileAsync(profile.Id);
            Check.Equal(6, (await service.GetConnectionHistoryAsync()).Count, "profile deletion retains connection history");
            Check.Equal(8, handler.Count, "history reading and deletion never send HTTP");
        }
    }

    public static async Task PersistedBestAndNewRoundBoundary()
    {
        using var tmp = new TempDir();
        var protector = new ReversibleTestProtector();
        var handler = new FakeHttpHandler();
        Guid id;
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
            id = (await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1)).Id;
        Guid round = Guid.NewGuid();
        var success = Record(round, ConnectionTestStage.ModelResponded, 10) with
            { ProfileId = id, ContextCheck = ConnectionContextCheck.Mismatch, Message = "first response", Code = "ModelResponded", PromptVersion = 1 };
        var failure = Record(round, ConnectionTestStage.Failed, 20) with
            { ProfileId = id, ContextCheck = ConnectionContextCheck.Passed, Message = "later failure", Code = "Http503", PromptVersion = 1 };
        var newerFailure = Record(Guid.NewGuid(), ConnectionTestStage.Failed, 40) with
            { ProfileId = id, ContextCheck = ConnectionContextCheck.Inconclusive, PromptVersion = 1 };
        SaveConnections(tmp.Path, protector, [failure, success]);
        using (var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            var modelResult = (await reopened.GetLatestResultsAsync(id, "m")).Connection!;
            var profileResult = (await reopened.GetLastConnectionResultAsync(id))!;
            Check.Equal(success, modelResult, "model recent result preserves the entire best record after restart, including its own mismatched marker");
            Check.Equal(success, profileResult, "profile last result preserves same entire best record, not the later Passed marker");
        }
        SaveConnections(tmp.Path, protector, [success, failure, newerFailure]);
        using (var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            Check.Equal(newerFailure.RecordId, (await reopened.GetLatestResultsAsync(id, "m")).Connection!.RecordId,
                "new failed round does not borrow earlier successful round after restart");
            Check.Equal(newerFailure, (await reopened.GetLastConnectionResultAsync(id))!,
                "profile last result selects the entire new failed record and its own inconclusive marker");
            Check.Equal(3, (await reopened.GetConnectionHistoryAsync()).Count, "local selection does not collapse stored history");
        }
        Check.Equal(0, handler.Count, "persistence and result queries never call the network");
    }

    public static async Task BestSurvivesCancellationBoundary()
    {
        using var tmp = new TempDir();
        using var cancellation = new CancellationTokenSource();
        var protector = new CancelAfterTwoCompletions(cancellation);
        var handler = new FakeHttpHandler();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        await Seed(service, handler);
        handler.EnqueueJson(HttpStatusCode.OK, "{}");
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        handler.EnqueueJson(HttpStatusCode.OK, Ok); // A third request would be unauthorized after cancellation.
        protector.Armed = true;
        var round = await service.RunInspectionRoundAsync(cancellationToken: cancellation.Token);
        Check.Equal(3, handler.Count, "two attempts plus seed, cancellation prevents the third send");
        Check.Equal(1, round.PossibleSuccessCount, "completed possible result survives a later failure and cancellation");
        Check.Equal(0, round.Succeeded + round.Failed + round.UnknownCount + round.Skipped, "cancelled target is not double counted");
        Check.Equal(2, (await service.GetConnectionHistoryAsync()).Count, "both real attempts survive cancellation");
    }

    public static Task InProgressPreservesBestWholeRecord()
    {
        foreach (var bestStage in new[] { ConnectionTestStage.ModelResponded, ConnectionTestStage.PossibleSuccess })
        {
            Guid round = Guid.NewGuid();
            var best = Record(round, bestStage, 10) with
                { ContextCheck = ConnectionContextCheck.Mismatch, Message = "selected original response", Code = "Original", PromptVersion = 1 };
            var failed = Record(round, ConnectionTestStage.Failed, 20) with
                { ContextCheck = ConnectionContextCheck.Passed, Message = "must not lend a marker", PromptVersion = 2 };
            var running = Record(round, ConnectionTestStage.ApiReached, 30) with
                { CompletedAt = null, ContextCheck = ConnectionContextCheck.NotChecked };
            ConnectionTestResult[] history = [failed, running, best];
            var row = ConnectionRoundDisplay.LatestPerTarget(history, round).Single();
            Check.Equal(best, row.Record, "during retry the whole completed best record is displayed, including its own marker");
            Check.True(row.IsAttemptInProgress, "running third attempt remains visible independently");
            Check.Equal(3, row.Attempts, "all attempts including current request are counted");
            Check.Contains(ConnectionRoundDisplay.AttemptNote(row), "第 3 次尝试进行中", "progress does not disappear behind best result");
            Check.Equal(best, InspectionRoundResults.FinalConnections(history, round).Single(), "Core also ignores unfinished attempt without mixing marker fields");
            Check.Equal(ConnectionContextCheck.Passed, failed.ContextCheck, "selection never mutates another attempt's marker");
            Check.Equal(ConnectionContextCheck.NotChecked, running.ContextCheck, "running attempt stays untested");
        }
        return Task.CompletedTask;
    }

    public static Task ContextDisplayContract()
    {
        foreach (var (check, label, tone) in new[]
        {
            (ConnectionContextCheck.Passed, "通过", "ok"),
            (ConnectionContextCheck.Mismatch, "不匹配", "possible"),
            (ConnectionContextCheck.Inconclusive, "无法判断", "bad"),
            (ConnectionContextCheck.NotChecked, "未检验", "bad"),
        })
        {
            Check.Equal(label, ProtocolInfo.ContextLabel(check), "marker label for " + check);
            Check.Equal(tone, ProtocolInfo.ContextTone(check), "marker color token for " + check);
            Check.Contains(ProtocolInfo.ContextBadgeText(check), label, "badge shares label");
        }
        Check.NotContains(ProtocolInfo.ContextNote(ConnectionContextCheck.NotChecked), "未校验", "legacy wording changed to 未检验");
        return Task.CompletedTask;
    }

    public static async Task LegacyRoundSummaryUsesBestWithoutErasingPartialTotals()
    {
        foreach (string scenario in new[] { "complete-history", "partial-target-history", "deleted-history", "same-rank-unknown" })
        {
            using var tmp = new TempDir();
            var protector = new ReversibleTestProtector();
            var handler = new FakeHttpHandler();
            Guid id;
            using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
                id = (await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1)).Id;
            Guid roundId = Guid.NewGuid();
            var possible = Record(roundId, ConnectionTestStage.PossibleSuccess, 10) with
                { ProfileId = id, ContextCheck = ConnectionContextCheck.Inconclusive, PromptVersion = 1 };
            var failure1 = Record(roundId, ConnectionTestStage.Failed, 20) with { ProfileId = id };
            var failure2 = Record(roundId, ConnectionTestStage.Failed, 30) with { ProfileId = id };
            var rawUnknown = Record(roundId, ConnectionTestStage.Unknown, 40) with { ProfileId = id };
            ConnectionTestResult[] history = scenario switch
            {
                "complete-history" => [possible, failure1, failure2],
                "partial-target-history" => [failure1, failure2],
                "deleted-history" => [],
                _ => [failure1, rawUnknown],
            };
            var oldSummary = new InspectionRoundSummary
            {
                RoundId = roundId, StartedAt = T0, CompletedAt = T0.AddMinutes(2),
                Total = scenario == "partial-target-history" ? 2 : 1,
                Failed = scenario is "complete-history" or "partial-target-history" ? 1 : 0,
                PossibleSuccessCount = scenario is "partial-target-history" or "deleted-history" ? 1 : 0,
                UnknownCount = scenario == "same-rank-unknown" ? 1 : 0,
            };
            SaveConnections(tmp.Path, protector, history, oldSummary);
            using var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
            var snapshot = (await reopened.GetInspectionSnapshotAsync()).LatestRound!;
            Check.Equal(roundId, snapshot.RoundId, scenario + ": original round identity retained");
            Check.Equal(oldSummary.Total, snapshot.Total, scenario + ": frozen scope total retained");
            Check.Equal(0, snapshot.UnknownCount, scenario + ": legacy unknown count is displayed as failed");
            Check.Equal(scenario is "partial-target-history" or "same-rank-unknown" ? 1 : 0, snapshot.Failed,
                scenario + ": complete evidence uses best, incomplete evidence keeps known aggregate");
            Check.Equal(scenario == "same-rank-unknown" ? 0 : 1, snapshot.PossibleSuccessCount,
                scenario + ": old last-attempt summary reconciled without erasing deleted-target totals");
            var after = await reopened.GetConnectionHistoryAsync();
            Check.Equal(history.Length, after.Count, scenario + ": summary projection does not synthesize or delete history");
            foreach (var original in history)
                Check.Equal(original, after.Single(r => r.RecordId == original.RecordId), scenario + ": every raw record is unchanged");
            Check.Equal(0, handler.Count, scenario + ": snapshot reconciliation is strictly local");
        }
    }

    private static async Task<RelayProfile> Seed(DesktopService service, FakeHttpHandler handler)
    {
        var p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m"));
        p = await service.RefreshModelsAsync(p.Id, Fixtures.ModelConsent(p));
        await service.SaveInspectionSelectionsAsync([new(p.Id, true, ["m"])]);
        var snapshot = await service.GetInspectionSnapshotAsync();
        await service.SaveInspectionSettingsAsync(new() { Enabled = true },
            new(snapshot.Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
        return p;
    }

    private static void SaveConnections(string directory, ReversibleTestProtector protector, ConnectionTestResult[] history,
        InspectionRoundSummary? summary = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        string path = Path.Combine(directory, "desktop-state.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var vault = JsonNode.Parse(protector.Unprotect(Convert.FromBase64String(root["protectedState"]!.GetValue<string>())))!;
        vault["connectionHistory"] = JsonSerializer.SerializeToNode(history, options);
        // Existing store contract keeps the per-profile latest pointer separately from complete history.
        // Seed it with the newest actual attempt, not with the expected best result.
        vault["connectionResults"] = JsonSerializer.SerializeToNode(history.GroupBy(r => r.ProfileId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CompletedAt).First()), options);
        if (summary is not null) vault["latestInspectionRound"] = JsonSerializer.SerializeToNode(summary, options);
        root["protectedState"] = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(vault.ToJsonString(options))));
        File.WriteAllText(path, root.ToJsonString(options));
    }

    private sealed class CancelAfterTwoCompletions(CancellationTokenSource cancellation) : ISecretProtector
    {
        private readonly ReversibleTestProtector inner = new();
        public bool Armed { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            var vault = JsonNode.Parse(plaintext)!;
            if (Armed && vault["connectionHistory"]!.AsArray().Count(r => r!["completedAt"] is not null) >= 2)
            {
                Armed = false;
                cancellation.Cancel();
            }
            return inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext) => inner.Unprotect(ciphertext);
    }
}
