using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProbeDesktop.App.Logic;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// 独立验证用例（code-verifier）。只做观察与断言，不修改生产代码。
/// 覆盖：连接响应分类边界、无密钥查询、四类计数、旧记录不改写、导出白名单、UI 筛选标签映射。
/// </summary>
public static class VerifierProtocolTests
{
    private const string RelayBase = "https://relay.example.com/v1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>失败关闭的未知结果代码集合：格式、上限、内容类型都不得被当作成功。</summary>
    private static readonly HashSet<string> UnknownCodes = new(StringComparer.Ordinal)
    {
        "IncompatibleResponse", "MalformedJson", "ResponseTooLarge", "UnexpectedContent", "Network", "CancelledOrTimeout"
    };

    private static bool Has(JsonElement record, string name) =>
        record.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static DesktopService New(string dir, FakeHttpHandler handler)
        => new(dir, Fixtures.Client(handler), new ReversibleTestProtector());

    private static OutboundConsent ConnectionConsent(RelayProfile p, string model)
        => new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(p.BaseUrl, p.Protocol), model, DateTimeOffset.UtcNow, p.Protocol);

    private static OutboundConsent ProbeConsent(RelayProfile p, string model)
        => new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.Probe,
            Fixtures.ServiceRunUrl, model, DateTimeOffset.UtcNow, p.Protocol);

    private static async Task<ConnectionTestResult> Run(string media, string body, ApiProtocol protocol, string model = "model-a")
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().EnqueueRaw(HttpStatusCode.OK, media, body);
        using var service = New(tmp.Path, handler);
        RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Program.Key1);
        ConnectionTestResult result = await service.TestConnectionAsync(p.Id, model, ConnectionConsent(p, model));
        Check.Equal(1, handler.Count, "exactly one request, no retry or fallback");
        return result;
    }

    // V01 -------------------------------------------------------------------
    // SSE 分类边界：错误优先、缺完成信号为 Unknown、incomplete 为 PossibleSuccess、HTML/明确错误 JSON 不得成功。
    public static async Task V01_ClassificationBoundaries()
    {
        var cases = new (string Label, string Media, string Body, ApiProtocol Protocol, ConnectionTestStage Expected)[]
        {
            ("chat sse delta without [DONE]", "text/event-stream",
                "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n", ApiProtocol.ChatCompletions, ConnectionTestStage.Unknown),
            ("chat sse done without text", "text/event-stream", "data: [DONE]\n\n",
                ApiProtocol.ChatCompletions, ConnectionTestStage.PossibleSuccess),
            ("chat sse text then trailing error", "text/event-stream",
                "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\nevent: error\ndata: {\"error\":{\"message\":\"quota\"}}\n\n",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Failed),
            ("chat sse explicit type error after done", "text/event-stream",
                "data: [DONE]\n\ndata: {\"type\":\"error\",\"message\":\"bad key\"}\n\n",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Failed),
            ("chat sse error event without data", "text/event-stream", "event: error\ndata: \n\n",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Failed),
            ("chat sse only comments", "text/event-stream", ": ping\n: ping\n\n",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Unknown),
            ("responses sse incomplete after text", "text/event-stream",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.incomplete\"}\n\n",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.PossibleSuccess),
            ("responses sse incomplete before text", "text/event-stream",
                "data: {\"type\":\"response.incomplete\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.PossibleSuccess),
            ("responses sse completed with nested failure", "text/event-stream",
                "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"failed\"}}\n\n",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.Failed),
            ("responses sse delta without completion", "text/event-stream",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.Unknown),
            ("anthropic sse text without message_stop", "text/event-stream",
                "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n",
                ApiProtocol.AnthropicMessages, ConnectionTestStage.Unknown),
            ("anthropic sse error event", "text/event-stream",
                "data: {\"type\":\"message_stop\"}\n\nevent: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}\n\n",
                ApiProtocol.AnthropicMessages, ConnectionTestStage.Failed),
            ("html body with json media type", "application/json", "<html>sign in</html>",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Unknown),
            ("json html marker", "application/json", "{\"html\":\"<html>challenge</html>\"}",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Unknown),
            ("json says content_type text/html", "application/json", "{\"content_type\":\"text/html\",\"status\":\"ok\"}",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Unknown),
            ("explicit error json is failed", "application/json", "{\"error\":{\"message\":\"quota exceeded\"}}",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Failed),
            ("success false json is failed", "application/json", "{\"success\":false,\"result\":\"OK\"}",
                ApiProtocol.ChatCompletions, ConnectionTestStage.Failed),
            ("responses json failed status", "application/json", "{\"status\":\"failed\",\"output\":[]}",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.Failed),
            ("responses json incomplete is possible", "application/json",
                "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.PossibleSuccess),
            ("responses json queued is unknown", "application/json", "{\"status\":\"queued\"}",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.Unknown),
            ("responses json in_progress is unknown", "application/json", "{\"status\":\"in_progress\",\"output\":[]}",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.Unknown),
            ("chat json valid text", "application/json",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}",
                ApiProtocol.ChatCompletions, ConnectionTestStage.ModelResponded),
            ("anthropic json valid text", "application/json",
                "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}",
                ApiProtocol.AnthropicMessages, ConnectionTestStage.ModelResponded),
            ("responses json valid text", "application/json",
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}",
                ApiProtocol.OpenAIResponses, ConnectionTestStage.ModelResponded),
        };

        foreach ((string label, string media, string body, ApiProtocol protocol, ConnectionTestStage expected) in cases)
        {
            ConnectionTestResult r = await Run(media, body, protocol);
            Check.Equal(expected, r.Stage, label + ": stage");
            Check.Equal(expected == ConnectionTestStage.ModelResponded, r.ModelResponded,
                label + ": ModelResponded only for confirmed text");
            Check.True(r.Message.Length > 0, label + ": message present");
            if (expected == ConnectionTestStage.PossibleSuccess)
            {
                Check.Equal("PossibleSuccess", r.Code, label + ": code separates possible success");
                Check.Contains(r.Message, "可能成功", label + ": message does not claim confirmed success");
            }

            if (expected == ConnectionTestStage.Failed) Check.Equal("ApiError", r.Code, label + ": failure code");
            if (expected == ConnectionTestStage.Unknown)
                Check.True(UnknownCodes.Contains(r.Code), label + ": unknown outcome uses a fail-closed code, got " + r.Code);
        }
    }

    // V02 -------------------------------------------------------------------
    // 有界读取：字节、行、事件、事件内容上限全部落为 Unknown（不成功、不失败），且只发一次请求。
    public static async Task V02_BoundedReads()
    {
        string EventLimit = string.Concat(Enumerable.Repeat("data: {}\n\n", 4097));
        string LineLimit = string.Concat(Enumerable.Repeat(": heartbeat\n", 16385));
        string EventSizeLimit = "data: " + new string(' ', 60000) + "\ndata: " + new string(' ', 60000) + "\ndata: " + new string(' ', 60000) + "\n\n";
        string TotalBytesLimit = new string('x', (2 * 1024 * 1024) + 1);

        var cases = new (string Label, string Body)[]
        {
            ("event count limit", EventLimit),
            ("line count limit", LineLimit),
            ("single event size limit", EventSizeLimit),
            ("total byte limit", TotalBytesLimit),
        };
        foreach ((string label, string body) in cases)
        {
            ConnectionTestResult r = await Run("text/event-stream", body, ApiProtocol.ChatCompletions);
            Check.Equal(ConnectionTestStage.Unknown, r.Stage, label + ": unknown, never success or failure");
            Check.False(r.ModelResponded, label + ": not a model response");
            Check.True(UnknownCodes.Contains(r.Code), label + ": fail-closed code, got " + r.Code);
        }

        // 非法 UTF-8 / 破损 SSE 帧同样为未知，且不会把前缀文本当成成功。
        ConnectionTestResult broken = await Run("text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: {broken\n\n", ApiProtocol.ChatCompletions);
        Check.Equal(ConnectionTestStage.Unknown, broken.Stage, "malformed frame keeps result unknown");
        Check.False(broken.ModelResponded, "malformed frame never claims success");
    }

    // V03 -------------------------------------------------------------------
    // 计数四类 + 跳过互不合并；持久化后重新读取保持一致。
    public static async Task V03_RoundCounters()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler().EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("ok", "maybe", "bad", "lost"));
        using (var service = New(dir.Path, handler))
        {
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            await service.RefreshModelsAsync(p.Id, new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ModelList,
                ModelApiEndpoints.Models(p.BaseUrl, p.Protocol), null, DateTimeOffset.UtcNow));
            await service.SaveInspectionSelectionsAsync([new(p.Id, true, ["ok", "maybe", "bad", "lost"])]);
            InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
            await service.SaveInspectionSettingsAsync(new() { Enabled = false },
                new(snapshot.Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });

            handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}");
            for (int i = 0; i < 3; i++) handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"foo\":\"bar\"}");
            for (int i = 0; i < 3; i++) handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"error\":{\"message\":\"quota\"}}");
            for (int i = 0; i < 3; i++) handler.EnqueueRaw(HttpStatusCode.OK, "text/event-stream", "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n");

            InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
            Check.Equal(4, round.Total, "total models");
            Check.Equal(1, round.Succeeded, "succeeded counted once");
            Check.Equal(1, round.PossibleSuccessCount, "possible success counted once");
            Check.Equal(1, round.Failed, "failed counted once");
            Check.Equal(1, round.UnknownCount, "unknown counted once");
            Check.Equal(0, round.Skipped, "nothing skipped");
            Check.Equal(4, round.Succeeded + round.PossibleSuccessCount + round.Failed + round.UnknownCount,
                "four categories are disjoint and complete");
            Check.NotNull(round.CompletedAt, "round completed");
            Check.Equal(11, handler.Count, "one model list plus ten bounded attempts");

            // 已完成的连接记录不应停留在中间阶段（结论：SendConnection 的最终返回值不会是中间阶段）。
            IReadOnlyList<ConnectionTestResult> records = await service.GetConnectionHistoryAsync();
            Check.Equal(10, records.Count, "each attempt gets a real record");
            foreach (ConnectionTestResult record in records)
            {
                Check.NotEqual(ConnectionTestStage.ApiReached, record.Stage, "final record never stays at ApiReached");
                Check.NotEqual(ConnectionTestStage.LocalValidationPassed, record.Stage, "final record never stays at LocalValidationPassed");
                Check.NotNull(record.CompletedAt, "final record always has a completion time");
            }
        }
        using var reload = New(dir.Path, new FakeHttpHandler());
        InspectionRoundSummary persisted = (await reload.GetInspectionSnapshotAsync()).LatestRound!;
        Check.Equal(1, persisted.Succeeded, "persisted succeeded");
        Check.Equal(1, persisted.PossibleSuccessCount, "persisted possible");
        Check.Equal(1, persisted.Failed, "persisted failed");
        Check.Equal(1, persisted.UnknownCount, "persisted unknown");
    }

    // V04 -------------------------------------------------------------------
    // 旧记录不改写：新增 PossibleSuccess 记录不得改变既有 Unknown/ModelResponded 记录，导出保留旧阶段。
    public static async Task V04_LegacyRecordsUnchanged()
    {
        using var vault = new TempDir();
        using var exportDir = new TempDir();
        Guid profileId;
        ConnectionTestResult oldUnknown, oldSuccess;
        using (var service = New(vault.Path, new FakeHttpHandler()))
        {
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            profileId = p.Id;
        }

        using (var handler = new FakeHttpHandler().EnqueueRaw(HttpStatusCode.OK, "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n"))
        using (var service = New(vault.Path, handler))
        {
            RelayProfile p = (await service.GetProfilesAsync()).Single();
            oldUnknown = await service.TestConnectionAsync(profileId, "model-a", ConnectionConsent(p, "model-a"));
        }
        using (var handler = new FakeHttpHandler().EnqueueJson(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}"))
        using (var service = New(vault.Path, handler))
        {
            RelayProfile p = (await service.GetProfilesAsync()).Single();
            oldSuccess = await service.TestConnectionAsync(profileId, "model-a", ConnectionConsent(p, "model-a"));
        }
        Check.Equal(ConnectionTestStage.Unknown, oldUnknown.Stage, "baseline unknown record");
        Check.Equal(ConnectionTestStage.ModelResponded, oldSuccess.Stage, "baseline success record");

        IReadOnlyList<ConnectionTestResult> before = await new DesktopService(vault.Path, Fixtures.Client(new FakeHttpHandler()),
            new ReversibleTestProtector()).GetConnectionHistoryAsync();
        ConnectionTestResult snapshotUnknown = before.Single(r => r.RecordId == oldUnknown.RecordId);
        ConnectionTestResult snapshotSuccess = before.Single(r => r.RecordId == oldSuccess.RecordId);

        ConnectionTestResult possible;
        using (var handler = new FakeHttpHandler().EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"foo\":\"bar\"}"))
        using (var service = New(vault.Path, handler))
        {
            RelayProfile p = (await service.GetProfilesAsync()).Single();
            possible = await service.TestConnectionAsync(profileId, "model-b", ConnectionConsent(p, "model-b"));
            Check.Equal(ConnectionTestStage.PossibleSuccess, possible.Stage, "new possible success record");

            IReadOnlyList<ConnectionTestResult> after = await service.GetConnectionHistoryAsync();
            ConnectionTestResult unknownAfter = after.Single(r => r.RecordId == oldUnknown.RecordId);
            ConnectionTestResult successAfter = after.Single(r => r.RecordId == oldSuccess.RecordId);
            Check.Equal(snapshotUnknown.Stage, unknownAfter.Stage, "old unknown stage not rewritten");
            Check.Equal(snapshotUnknown.CompletedAt, unknownAfter.CompletedAt, "old unknown timestamp not rewritten");
            Check.Equal(snapshotUnknown.Code, unknownAfter.Code, "old unknown code not rewritten");
            Check.Equal(snapshotSuccess.Stage, successAfter.Stage, "old success stage not rewritten");
            Check.Equal(snapshotSuccess.CompletedAt, successAfter.CompletedAt, "old success timestamp not rewritten");
            Check.False(unknownAfter.ModelResponded, "old unknown still not a model response");

            string exportPath = Path.Combine(exportDir.Path, "connections.json");
            await service.ExportConnectionHistoryAsync(exportPath);
            string exported = Fixtures.ReadAllText(exportPath);
            Console.WriteLine("EXPORT_SAMPLE=" + exported.Replace("\r", "").Replace("\n", " ")[..Math.Min(700, exported.Length)]);
            Check.Contains(exported, "\"PossibleSuccess\"", "export keeps the new stage");
            Check.Contains(exported, "\"Unknown\"", "export keeps the old unknown stage");
            Check.Contains(exported, "\"ModelResponded\"", "export keeps the old success stage");
            Check.NotContains(exported, Program.Key1, "export carries no key");
            Check.NotContains(exported, "Bearer", "export carries no auth header");
            Check.NotContains(exported, "relay.example.com", "export omits the relay address");
            Check.NotContains(exported, "RecordId", "export omits local record identifiers");
        }

        using var doc = JsonDocument.Parse(Fixtures.ReadAllText(Path.Combine(exportDir.Path, "connections.json")));
        Check.Equal(3, doc.RootElement.GetProperty("records").GetArrayLength(), "export contains all three records");
        foreach (JsonElement record in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            // 存储与导出都使用 Web（camelCase）JSON 约定，因此按大小写不敏感方式核对字段存在性。
            Check.False(Has(record, "BaseUrl"), "export has no base url field");
            Check.False(Has(record, "RemoteRunId"), "export has no remote run id");
            Check.False(Has(record, "ReportUrl"), "export has no report url");
            Check.False(Has(record, "RecordId"), "export has no local record id");
            Check.False(Has(record, "KeyRevision"), "export has no key revision");
            Check.False(Has(record, "ProfileId"), "export has no profile id");
            Check.True(Has(record, "Stage"), "export keeps the stage");
            Check.True(Has(record, "ModelResponded"), "export keeps the responded flag");
            Check.False(Has(record, "Headers"), "export has no response headers");
        }
    }

    // V05 -------------------------------------------------------------------
    // Probe 三协议：各一次精确 POST（固定官方地址、精确 upstreamFormat），轮询 GET 无密钥。
    public static async Task V05_ProbeThreeProtocols()
    {
        var expected = new (ApiProtocol Protocol, string UpstreamFormat)[]
        {
            (ApiProtocol.ChatCompletions, "openai"),
            (ApiProtocol.OpenAIResponses, "openai-responses"),
            (ApiProtocol.AnthropicMessages, "anthropic"),
        };
        foreach ((ApiProtocol protocol, string upstreamFormat) in expected)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler()
                .EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-verify-" + upstreamFormat));
            using var service = New(tmp.Path, handler);
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Program.Key1);

            ProbeRecord record = await service.StartProbeAsync(p.Id, "model-a", "claimed-a", ProbeConsent(p, "model-a"));
            Check.Equal(1, handler.Count, upstreamFormat + ": exactly one submit, no retry or fallback");
            RecordedRequest post = handler.Requests[0];
            Check.Equal("POST", post.Method, upstreamFormat + ": submit is POST");
            Check.Equal(Fixtures.ServiceRunUrl, post.Uri.AbsoluteUri, upstreamFormat + ": fixed official submit URL");
            Check.False(post.Headers.ContainsKey("Authorization"), upstreamFormat + ": submit never adds relay bearer");
            Check.False(post.Headers.ContainsKey("x-api-key"), upstreamFormat + ": submit never adds relay x-api-key");
            using (JsonDocument body = JsonDocument.Parse(post.Body))
            {
                Check.Equal(upstreamFormat, body.RootElement.GetProperty("upstreamFormat").GetString(), upstreamFormat + ": exact upstreamFormat");
                Check.Equal(RelayBase, body.RootElement.GetProperty("baseUrl").GetString(), upstreamFormat + ": relay base url");
                Check.Equal("model-a", body.RootElement.GetProperty("modelId").GetString(), upstreamFormat + ": model id");
                Check.Equal("claimed-a", body.RootElement.GetProperty("claimedModel").GetString(), upstreamFormat + ": claimed model");
                Check.Equal(Program.Key1, body.RootElement.GetProperty("apiKey").GetString(), upstreamFormat + ": target relay key is the submitted credential");
                Check.False(body.RootElement.TryGetProperty("protocol", out _), upstreamFormat + ": no ad-hoc protocol field");
            }
            Check.Equal(protocol, record.Protocol, upstreamFormat + ": record snapshots protocol");
            Check.Equal("run-verify-" + upstreamFormat, record.RemoteRunId, upstreamFormat + ": run id captured");

            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProbeStatusJson(record.RemoteRunId!, "running"));
            ProbeRecord polled = await service.PollProbeAsync(record.LocalId);
            Check.Equal(2, handler.Count, upstreamFormat + ": exactly one poll GET");
            RecordedRequest get = handler.Requests[1];
            Check.Equal("GET", get.Method, upstreamFormat + ": poll is GET");
            Check.Equal(Fixtures.ServiceRunUrl + "/run-verify-" + upstreamFormat, get.Uri.AbsoluteUri, upstreamFormat + ": poll URL");
            Check.False(get.Headers.ContainsKey("Authorization"), upstreamFormat + ": poll sends no Authorization");
            Check.False(get.Headers.ContainsKey("x-api-key"), upstreamFormat + ": poll sends no x-api-key");
            Check.False(get.Headers.ContainsKey("anthropic-version"), upstreamFormat + ": poll sends no protocol headers");
            Check.Equal("", get.Body, upstreamFormat + ": poll has no body");
            Check.NotContains(get.Uri.AbsoluteUri, Program.Key1, upstreamFormat + ": poll url carries no key");
            Check.Equal(LocalRunState.Running, polled.State, upstreamFormat + ": poll state");
        }
    }

    // V06 -------------------------------------------------------------------
    // UI 纯逻辑分类：四类不合并、PossibleSuccess 不被 ModelResponded 影响、中间阶段不算成功/失败。
    public static async Task V06_UiClassification()
    {
        await Task.CompletedTask;

        Check.Equal(ConnectionTestStage.PossibleSuccess, ConnectionTestStage.PossibleSuccess, "stage enum exists");
        Check.Equal(5, (int)ConnectionTestStage.PossibleSuccess, "PossibleSuccess appended, old values stable");
        Check.Equal(4, (int)ConnectionTestStage.Unknown, "Unknown value unchanged");

        ConnectionTestResult Make(ConnectionTestStage stage, bool responded) => new()
        {
            Stage = stage,
            ModelResponded = responded,
            ProfileName = "p",
            ModelId = "m",
        };

        Check.Equal(LatestResultPicker.ConnectionOutcome.Success, LatestResultPicker.Classify(Make(ConnectionTestStage.ModelResponded, true)), "model responded -> success");
        Check.Equal(LatestResultPicker.ConnectionOutcome.PossibleSuccess, LatestResultPicker.Classify(Make(ConnectionTestStage.PossibleSuccess, false)), "possible success is its own class");
        Check.Equal(LatestResultPicker.ConnectionOutcome.PossibleSuccess, LatestResultPicker.Classify(Make(ConnectionTestStage.PossibleSuccess, true)), "possible success never merges into success");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Failed, LatestResultPicker.Classify(Make(ConnectionTestStage.Failed, false)), "failed");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Unknown, LatestResultPicker.Classify(Make(ConnectionTestStage.Unknown, false)), "unknown");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Incomplete, LatestResultPicker.Classify(Make(ConnectionTestStage.LocalValidationPassed, false)), "local validation incomplete, not failed");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Incomplete, LatestResultPicker.Classify(Make(ConnectionTestStage.ApiReached, false)), "api reached incomplete, not failed");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Incomplete, LatestResultPicker.Classify(Make((ConnectionTestStage)99, false)), "unknown enum is incomplete, never success");
        Check.Equal(LatestResultPicker.ConnectionOutcome.Success, LatestResultPicker.Classify(Make((ConnectionTestStage)99, true)), "defensive fallback only when responded flag is set");

        string[] tones =
        [
            LatestResultPicker.OutcomeTone(LatestResultPicker.ConnectionOutcome.Success),
            LatestResultPicker.OutcomeTone(LatestResultPicker.ConnectionOutcome.PossibleSuccess),
            LatestResultPicker.OutcomeTone(LatestResultPicker.ConnectionOutcome.Failed),
            LatestResultPicker.OutcomeTone(LatestResultPicker.ConnectionOutcome.Unknown),
        ];
        Check.Equal(4, tones.Distinct(StringComparer.Ordinal).Count(), "four outcomes have distinct tones: " + string.Join(",", tones));
        Check.Equal("possible", tones[1], "possible tone is dedicated");
        Check.Equal("成功（可能）", LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.PossibleSuccess), "possible text");

        (string State, string Tone) possible = LatestResultPicker.DescribeConnection(Make(ConnectionTestStage.PossibleSuccess, false));
        Check.Equal("成功（可能）", possible.State, "possible display text");
        Check.Equal("possible", possible.Tone, "possible display tone");
        Check.Equal("未完成", LatestResultPicker.DescribeConnection(Make((ConnectionTestStage)99, false)).State, "unknown enum text");
    }

    // V07 -------------------------------------------------------------------
    // UI 筛选下拉标签与展示分类/来源文案逐一对应（读取 XAML 与纯逻辑，不启动界面）。
    public static async Task V07_UiFilterMapping()
    {
        await Task.CompletedTask;
        string root = RepositoryRoot();
        string historyXaml = File.ReadAllText(Path.Combine(root, "src", "ProbeDesktop.App", "Windows", "ConnectionHistoryWindow.xaml"));

        string[] expectedStatus = ["", "成功", "成功（可能）", "失败", "结果未知", "本地检查通过", "API 可达"];
        string[] expectedSource = ["", "手动", "自动巡检", "立即巡检"];
        List<string> statusTags = Tags(historyXaml, "按状态筛选");
        List<string> sourceTags = Tags(historyXaml, "按来源筛选");

        Check.Equal(string.Join("|", expectedStatus), string.Join("|", statusTags),
            "status filter tags must match ResultLabel output exactly");
        Check.Equal(string.Join("|", expectedSource), string.Join("|", sourceTags),
            "source filter tags must match SourceText output exactly");

        // 每个筛选标签必须在 ResultLabel 输出集合内，否则筛选永远为空。
        var labels = new HashSet<string>(StringComparer.Ordinal)
        {
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Success),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.PossibleSuccess),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Failed),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Unknown),
            "本地检查通过",
            "API 可达",
        };
        foreach (string tag in statusTags.Where(t => t.Length > 0))
            Check.True(labels.Contains(tag), "filter tag must be a producible result label: " + tag);

        // 四个终态文案互不相同，筛选不会串类。
        Check.Equal(4, new[]
        {
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Success),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.PossibleSuccess),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Failed),
            LatestResultPicker.OutcomeText(LatestResultPicker.ConnectionOutcome.Unknown),
        }.Distinct(StringComparer.Ordinal).Count(), "four result labels are distinct");
    }

    // V08 -------------------------------------------------------------------
    // 截断/未正常完成的结束原因不得算作已确认成功（批准计划要求检查完成与截断状态）。
    // 观察口径：非正常结束原因 + 有文本 => 成功（可能），不是 ModelResponded。
    public static async Task V08_TruncationIsNotConfirmedSuccess()
    {
        var cases = new (string Label, string Media, string Body, ApiProtocol Protocol)[]
        {
            ("chat sse finish_reason=length", "text/event-stream",
                "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n",
                ApiProtocol.ChatCompletions),
            ("chat sse finish_reason=content_filter", "text/event-stream",
                "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"content_filter\"}]}\n\ndata: [DONE]\n\n",
                ApiProtocol.ChatCompletions),
            ("chat json finish_reason=length", "application/json",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"length\"}]}",
                ApiProtocol.ChatCompletions),
            ("anthropic sse stop_reason=max_tokens", "text/event-stream",
                "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n" +
                "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n",
                ApiProtocol.AnthropicMessages),
            ("anthropic json stop_reason=max_tokens", "application/json",
                "{\"type\":\"message\",\"role\":\"assistant\",\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}",
                ApiProtocol.AnthropicMessages),
            ("responses completed with nested incomplete status", "text/event-stream",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n",
                ApiProtocol.OpenAIResponses),
            ("responses json completed-text but status incomplete", "application/json",
                "{\"status\":\"incomplete\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}",
                ApiProtocol.OpenAIResponses),
        };

        var observed = new List<string>();
        var offenders = new List<string>();
        foreach ((string label, string media, string body, ApiProtocol protocol) in cases)
        {
            ConnectionTestResult r = await Run(media, body, protocol);
            observed.Add($"{label} => Stage={r.Stage} Code={r.Code} ModelResponded={r.ModelResponded}");
            if (r.Stage == ConnectionTestStage.ModelResponded || r.ModelResponded) offenders.Add(label + " => Stage=" + r.Stage);
        }
        Console.WriteLine("TRUNCATION_MATRIX=" + string.Join(" | ", observed));
        Check.Equal(0, offenders.Count,
            "truncated or contradictory completions must not be confirmed success; offenders: " + string.Join(" ; ", offenders));
    }

    private static List<string> Tags(string xaml, string automationName)
    {
        int start = xaml.IndexOf(automationName, StringComparison.Ordinal);
        if (start < 0) throw new TestFailure("filter combo not found: " + automationName);
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        if (end < 0) throw new TestFailure("filter combo not closed: " + automationName);
        string block = xaml[start..end];
        return Regex.Matches(block, "<ComboBoxItem Content=\"([^\"]*)\" Tag=\"([^\"]*)\"")
            .Select(m => m.Groups[2].Value)
            .ToList();
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(caller)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProbeDesktop.slnx"))) dir = dir.Parent;
        if (dir is null) throw new TestFailure("repository root not found from " + caller);
        return dir.FullName;
    }
}
