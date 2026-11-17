using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class ContextCheckTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string Answer(string prompt)
    {
        var values = new[] { "FIRST", "MIDDLE", "LAST" }.Select(label =>
        {
            var match = Regex.Match(prompt, @"\b" + label + "=([A-F0-9]{4})");
            Check.True(match.Success, "all three markers are available in transmitted input");
            return match.Groups[1].Value;
        });
        return string.Join("|", values);
    }

    private static string Body(ApiProtocol protocol, string answer) => protocol switch
    {
        ApiProtocol.ChatCompletions => Json(new { choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = "stop" } } }),
        ApiProtocol.OpenAIResponses => Json(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } } }),
        _ => Json(new { type = "message", role = "assistant", content = new[] { new { type = "text", text = answer } }, stop_reason = "end_turn" })
    };

    private static string StreamBody(ApiProtocol protocol, string answer, string mode)
    {
        string Event(object value) => "data: " + Json(value) + "\n\n";
        string stream = "";
        // Split every character, including inside the markers and separators.
        foreach (char ch in answer)
            stream += protocol switch
            {
                ApiProtocol.ChatCompletions => Event(new { choices = new[] { new { index = 0, delta = new { content = ch.ToString() } } } }),
                ApiProtocol.OpenAIResponses => Event(new { type = "response.output_text.delta", delta = ch.ToString() }),
                _ => Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = ch.ToString() } })
            };
        if (mode == "unfinished") return stream;
        stream += protocol switch
        {
            ApiProtocol.ChatCompletions => "data: [DONE]\n\n",
            // Include a full snapshot: it must replace, not duplicate, deltas.
            ApiProtocol.OpenAIResponses => "data: {\"type\":\"response.completed\",\"response\":" + Body(protocol, answer) + "}\n\n",
            _ => Event(new { type = "message_stop" })
        };
        if (mode == "error") stream += Event(new { type = "error", error = new { message = "test failure" } });
        return stream;
    }

    public static async Task ProtocolsAndPersistence()
    {
        foreach (ApiProtocol protocol in Enum.GetValues<ApiProtocol>())
        foreach (bool streaming in new[] { false, true })
        foreach (string mode in new[] { "correct", "wrong", "extra", "large", "unfinished", "error" })
        {
            using var tmp = new TempDir();
            using var export = new TempDir();
            var handler = new FakeHttpHandler();
            string expected = "";
            handler.Enqueue(request =>
            {
                using var doc = JsonDocument.Parse(handler.Requests.Last().Body);
                var root = doc.RootElement;
                string prompt = (protocol == ApiProtocol.OpenAIResponses ? root.GetProperty("input") : root.GetProperty("messages")[0].GetProperty("content")).GetString()!;
                Check.True(prompt.Length is >= 900 and <= 1600, "input is a small reading note, not the old one-line ping or a 256k payload");
                expected = Answer(prompt);
                string answer = mode switch { "wrong" => "true", "extra" => "Here: " + expected, "large" => new string('x', 2100), _ => expected };
                string body = streaming ? StreamBody(protocol, answer, mode) : Body(protocol, answer);
                if (!streaming && mode == "unfinished") body = protocol switch
                {
                    ApiProtocol.ChatCompletions => body.Replace("\"stop\"", "\"length\""),
                    ApiProtocol.OpenAIResponses => body.Replace("\"completed\"", "\"incomplete\""),
                    _ => body.Replace("\"end_turn\"", "\"max_tokens\"")
                };
                if (!streaming && mode == "error") body = "{\"error\":{\"message\":\"failure\"}}";
                return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
            });
            Guid id;
            ConnectionContextCheck expectedCheck = mode switch
            {
                "correct" => ConnectionContextCheck.Passed,
                "wrong" or "extra" => ConnectionContextCheck.Mismatch,
                _ => ConnectionContextCheck.Inconclusive
            };
            using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector()))
            {
                var profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Program.Key1);
                id = profile.Id;
                var consent = new OutboundConsent(id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ConnectionTest,
                    ModelApiEndpoints.Inference(profile.BaseUrl, protocol), "model-a", DateTimeOffset.UtcNow, protocol);
                var result = await service.TestConnectionAsync(id, "model-a", consent);
                Check.Equal(expectedCheck, result.ContextCheck, $"{protocol}/{streaming}/{mode}");
                Check.Equal(1, result.PromptVersion, "new request is versioned");
                Check.Equal(1, handler.Count, "no re-request for mismatch");
                if (mode is "wrong" or "extra")
                    Check.True(result.ModelResponded, "reading mismatch does not mean connection failure");
                await service.ExportConnectionHistoryAsync(Path.Combine(export.Path, "summary.json"));
                string summary = await File.ReadAllTextAsync(Path.Combine(export.Path, "summary.json"));
                Check.Contains(summary, "contextCheck", "summary includes independent check");
                Check.NotContains(summary, expected, "raw answer not exported");
                Check.NotContains(result.Message, expected, "raw answer not displayed or persisted in message");
            }
            using var reopened = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), new ReversibleTestProtector());
            Check.Equal(expectedCheck, (await reopened.GetLastConnectionResultAsync(id))!.ContextCheck, "check survives restart");
        }
    }

    public static Task LegacyContract()
    {
        var old = JsonSerializer.Deserialize<ConnectionTestResult>("{\"ModelResponded\":true}")!;
        Check.Equal(ConnectionContextCheck.NotChecked, old.ContextCheck, "old success must not imply marker pass");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new ConnectionTestResult(), DesktopStore.Json));
        var fields = json.RootElement.EnumerateObject().Where(p => p.Name is not ("contextCheck" or "promptVersion"))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        using var legacy = JsonDocument.Parse(Json(fields));
        MigrationValidation.ValidateShape(legacy.RootElement, typeof(ConnectionTestResult));
        fields.Remove("modelId");
        using var bad = JsonDocument.Parse(Json(fields));
        bool rejected = false;
        try { MigrationValidation.ValidateShape(bad.RootElement, typeof(ConnectionTestResult)); }
        catch (JsonException) { rejected = true; }
        Check.True(rejected, "legacy allowance does not weaken mandatory original fields");
        return Task.CompletedTask;
    }
}
