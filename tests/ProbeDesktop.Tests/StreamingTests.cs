using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class StreamingTests
{
    private const string Delta = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private static OutboundConsent Consent(RelayProfile p, string model) => new(p.Id, p.KeyRevision, p.BaseUrl,
        OutboundPurpose.ConnectionTest, ConnectionTestEndpoints.Recipient(p.BaseUrl, p.Protocol), model, DateTimeOffset.UtcNow, p.Protocol);

    public static async Task Matrix()
    {
        var cases = new List<(ApiProtocol Protocol, string Media, string Body, ConnectionTestStage Expected)>
        {
            (ApiProtocol.ChatCompletions, "text/event-stream", Delta + Done, ConnectionTestStage.ModelResponded),
            (ApiProtocol.ChatCompletions, "text/event-stream", Delta.Replace("assistant", "user") + Done, ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.ChatCompletions, "text/event-stream", "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\n" + Done, ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.ChatCompletions, "text/event-stream", Done, ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.ChatCompletions, "text/event-stream", Delta, ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", Delta + "data: [DONE]", ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", Delta + Done + "event: error\ndata: {\"error\":{\"message\":\"quota\"}}\n\n", ConnectionTestStage.Failed),
            (ApiProtocol.ChatCompletions, "text/event-stream", ": heartbeat\r\nevent: message\r\ndata: {\"choices\":\r\ndata: [{\"delta\":{\"content\":\"OK\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n", ConnectionTestStage.ModelResponded),
            (ApiProtocol.OpenAIResponses, "text/event-stream", "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.completed\"}\n\n", ConnectionTestStage.ModelResponded),
            (ApiProtocol.OpenAIResponses, "text/event-stream", "event: response.completed\ndata: {}\n\n", ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.OpenAIResponses, "text/event-stream", "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.incomplete\"}\n\n", ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.OpenAIResponses, "text/event-stream", "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"failed\"}}\n\n", ConnectionTestStage.Failed),
            (ApiProtocol.AnthropicMessages, "text/event-stream", "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", ConnectionTestStage.ModelResponded),
            (ApiProtocol.AnthropicMessages, "text/event-stream", "data: {\"type\":\"message_stop\"}\n\n", ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.AnthropicMessages, "text/event-stream", Delta + Done, ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", "data: {broken\n\n" + Done, ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", "data: " + new string('x', 65537), ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", string.Concat(Enumerable.Repeat(": heartbeat\n", 16385)), ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", string.Concat(Enumerable.Repeat("data: {}\n\n", 4097)), ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", "data: " + new string(' ', 60000) + "\ndata: " + new string(' ', 60000) + "\ndata: " + new string(' ', 60000), ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/event-stream", new string('x', 2 * 1024 * 1024 + 1), ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "application/json", "{\"error\":null}", ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.ChatCompletions, "application/json", "{\"error\":{\"message\":\"quota\"}}", ConnectionTestStage.Failed),
            (ApiProtocol.ChatCompletions, "application/json", "{\"success\":false}", ConnectionTestStage.Failed),
            (ApiProtocol.OpenAIResponses, "application/json", "{\"status\":\"failed\"}", ConnectionTestStage.Failed),
            (ApiProtocol.OpenAIResponses, "application/json", "{\"status\":\"cancelled\"}", ConnectionTestStage.Failed),
            (ApiProtocol.OpenAIResponses, "application/json", "{\"status\":\"incomplete\",\"output\":[]}", ConnectionTestStage.PossibleSuccess),
            (ApiProtocol.OpenAIResponses, "application/json", "{\"status\":\"queued\"}", ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "application/json", "\"<html>verification</html>\"", ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "application/json", "{\"html\":\"<html>verification</html>\"}", ConnectionTestStage.Unknown),
            (ApiProtocol.ChatCompletions, "text/html", "<html>verification</html>", ConnectionTestStage.Unknown)
        };
        foreach (string reason in new[] { "length", "content_filter" })
        {
            cases.Add((ApiProtocol.ChatCompletions, "application/json",
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"" + reason + "\"}]}", ConnectionTestStage.PossibleSuccess));
            cases.Add((ApiProtocol.ChatCompletions, "text/event-stream", Delta +
                "data: {\"choices\":[{\"finish_reason\":\"" + reason + "\"}]}\n\n" + Done, ConnectionTestStage.PossibleSuccess));
        }
        cases.Add((ApiProtocol.AnthropicMessages, "application/json",
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"stop_reason\":\"max_tokens\"}", ConnectionTestStage.PossibleSuccess));
        const string anthropicText = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n";
        cases.Add((ApiProtocol.AnthropicMessages, "text/event-stream", anthropicText +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n", ConnectionTestStage.PossibleSuccess));
        foreach (string status in new[] { "incomplete", "in_progress" })
            cases.Add((ApiProtocol.OpenAIResponses, "text/event-stream",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"" + status + "\"}}\n\n",
                status == "incomplete" ? ConnectionTestStage.PossibleSuccess : ConnectionTestStage.Unknown));
        foreach (var c in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler().EnqueueRaw(HttpStatusCode.OK, c.Media, c.Body);
            Guid id;
            using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector()))
            {
                RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = c.Protocol }, Program.Key1);
                id = p.Id;
                ConnectionTestResult result = await service.TestConnectionAsync(id, "model-a", Consent(p, "model-a"));
                Check.Equal(c.Expected, result.Stage, c.Protocol + "/" + c.Body[..Math.Min(65, c.Body.Length)]);
                Check.Equal(c.Expected == ConnectionTestStage.ModelResponded, result.ModelResponded, "only confirmed text counts");
                using JsonDocument request = JsonDocument.Parse(handler.Requests[0].Body);
                Check.True(request.RootElement.GetProperty("stream").GetBoolean(), "stream true on every protocol");
                Check.Equal(64,
                    request.RootElement.GetProperty(c.Protocol == ApiProtocol.OpenAIResponses ? "max_output_tokens" : "max_tokens").GetInt32(), "marker response budget");
                Check.Equal(1, handler.Count, "single request, no fallback");
            }
            using var restarted = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
            Check.Equal(c.Expected, (await restarted.GetLastConnectionResultAsync(id))!.Stage, "stage survives restart");
        }
        Check.Equal(5, (int)ConnectionTestStage.PossibleSuccess, "append-only enum");
        Check.Equal(4, (int)ConnectionTestStage.Unknown, "old enum unchanged");
    }

    public static async Task TransportAndCounts()
    {
        foreach (bool stall in new[] { false, true })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler().Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamBody(new BrokenStream(Delta + Done, stall)) });
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            using var cancel = new CancellationTokenSource(250);
            ConnectionTestResult r = await service.TestConnectionAsync(p.Id, "model-a", Consent(p, "model-a"), cancel.Token);
            Check.Equal(ConnectionTestStage.Unknown, r.Stage, "even completed prefix cannot hide broken transport");
            Check.False(r.ModelResponded, "no transport-incomplete success");
            Check.Equal(1, handler.Count, "no retry after transport failure");
        }
        using var dir = new TempDir();
        var h = new FakeHttpHandler().EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("a", "b", "c", "d"));
        using (var service = new DesktopService(dir.Path, Fixtures.Client(h), new ReversibleTestProtector()))
        {
            RelayProfile p = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            await service.RefreshModelsAsync(p.Id, new(p.Id, p.KeyRevision, p.BaseUrl, OutboundPurpose.ModelList, ModelApiEndpoints.Models(p.BaseUrl, p.Protocol), null, DateTimeOffset.UtcNow));
            InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
            snapshot = await service.SaveInspectionSelectionsAsync([new(p.Id, true, ["a", "b", "c", "d"])]);
            await service.SaveInspectionSettingsAsync(new() { Enabled = false }, new(snapshot.Targets.Select(t => t.Target).ToArray(), 30, DateTimeOffset.UtcNow) { MaxAttempts = 3 });
            h.EnqueueRaw(HttpStatusCode.OK, "text/event-stream", Delta + Done);
            for (int i = 0; i < 3; i++) h.EnqueueJson(HttpStatusCode.OK, "{}");
            for (int i = 0; i < 3; i++) h.EnqueueJson(HttpStatusCode.OK, "{\"error\":{}}");
            for (int i = 0; i < 3; i++) h.EnqueueRaw(HttpStatusCode.OK, "text/event-stream", Delta);
            var progress = new InlineProgress();
            InspectionRoundSummary round = await service.RunInspectionRoundAsync(InspectionTrigger.Manual, progress);
            CheckCounts(round);
            CheckCounts(progress.Values.Last());
            Check.Equal(4, progress.Values.Last().Succeeded + progress.Values.Last().Failed + progress.Values.Last().PossibleSuccessCount + progress.Values.Last().UnknownCount, "independent accounted progress");
            Check.Equal(11, h.Count, "one fetch plus ten attempts; four final targets");
            var history = await service.GetConnectionHistoryAsync();
            Check.Equal(3, history.Count(r => r.ModelId == "d" && r.Stage == ConnectionTestStage.Unknown),
                "all truncated attempt stages remain raw Unknown despite failed summary classification");
        }
        using var reload = new DesktopService(dir.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
        CheckCounts((await reload.GetInspectionSnapshotAsync()).LatestRound!);
        Check.Equal(3, (await reload.GetConnectionHistoryAsync()).Count(r => r.ModelId == "d" && r.Stage == ConnectionTestStage.Unknown),
            "raw Unknown attempts survive restart without stage migration");
    }
    private static void CheckCounts(InspectionRoundSummary round)
    {
        Check.Equal(1, round.Succeeded, "success count"); Check.Equal(2, round.Failed, "failed includes unknown target");
        Check.Equal(1, round.PossibleSuccessCount, "possible count"); Check.Equal(0, round.UnknownCount, "legacy unknown counter remains zero for new rounds");
    }
    private sealed class InlineProgress : IProgress<InspectionRoundSummary>
    {
        internal List<InspectionRoundSummary> Values { get; } = [];
        public void Report(InspectionRoundSummary value) => Values.Add(value);
    }
    private sealed class StreamBody : HttpContent
    {
        private readonly Stream body;
        public StreamBody(Stream body)
        {
            this.body = body;
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult(body);
    }
    private sealed class BrokenStream(string prefix, bool stall) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            // Fragment across UTF-8/line boundaries to exercise incremental framing.
            if (Position < Length) return await base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token);
            if (stall) await Task.Delay(Timeout.Infinite, token);
            throw new IOException("fake interrupted transport");
        }
    }
}
