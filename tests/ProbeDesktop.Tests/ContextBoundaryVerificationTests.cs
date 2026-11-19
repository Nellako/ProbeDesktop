using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>Independent reading-check regressions. HTTP is entirely in memory; production
/// request creation, response parsing, persistence and inspection orchestration remain real.</summary>
public static class ContextBoundaryVerificationTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string Event(object value) => "data: " + Json(value) + "\n\n";
    private static string Prompt(string request, ApiProtocol protocol)
    {
        using var doc = JsonDocument.Parse(request);
        return (protocol == ApiProtocol.OpenAIResponses ? doc.RootElement.GetProperty("input") :
            doc.RootElement.GetProperty("messages")[0].GetProperty("content")).GetString()!;
    }
    private static string Answer(string prompt) => string.Join("|", new[] { "FIRST", "MIDDLE", "LAST" }.Select(label =>
    {
        Match match = Regex.Match(prompt, @"(?m)^" + label + "=([A-F0-9]{4})\r?$");
        Check.True(match.Success, "transmitted input has its own " + label + " marker");
        return match.Groups[1].Value;
    }));
    private static object Message(string text) => new { type = "message", role = "assistant",
        content = new[] { new { type = "output_text", text } } };
    private static string Body(ApiProtocol protocol, string answer) => protocol switch
    {
        ApiProtocol.ChatCompletions => Json(new { choices = new[] { new { index = 0,
            message = new { role = "assistant", content = answer }, finish_reason = "stop" } } }),
        ApiProtocol.OpenAIResponses => Json(new { status = "completed", output = new[] { Message(answer) } }),
        _ => Json(new { type = "message", role = "assistant", content = new[] { new { type = "text", text = answer } }, stop_reason = "end_turn" })
    };
    private static string Delta(ApiProtocol protocol, string text) => protocol switch
    {
        ApiProtocol.ChatCompletions => Event(new { choices = new[] { new { index = 0, delta = new { content = text } } } }),
        ApiProtocol.OpenAIResponses => Event(new { type = "response.output_text.delta", item_id = "message-0", output_index = 0, content_index = 0, delta = text }),
        _ => Event(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
    };
    private static string End(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.ChatCompletions => "data: [DONE]\n\n",
        ApiProtocol.OpenAIResponses => Event(new { type = "response.completed" }),
        _ => Event(new { type = "message_stop" })
    };
    private static OutboundConsent Consent(RelayProfile profile) => new(profile.Id, profile.KeyRevision,
        profile.BaseUrl, OutboundPurpose.ConnectionTest, ModelApiEndpoints.Inference(profile.BaseUrl, profile.Protocol),
        "model-a", DateTimeOffset.UtcNow, profile.Protocol);

    private static async Task<ConnectionTestResult> Run(ApiProtocol protocol, Func<string, string> response,
        bool streaming = true, bool broken = false, bool fragmented = false)
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.Enqueue(_ =>
        {
            string answer = Answer(Prompt(handler.Requests.Last().Body, protocol));
            string body = response(answer);
            return new(HttpStatusCode.OK)
            {
                Content = fragmented || broken ? new StreamContentBody(new FragmentStream(body, broken)) :
                    new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        });
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Program.Key1);
        ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile));
        Check.Equal(1, handler.Count, "the reading check never performs a fallback request");
        Check.Equal(1, result.PromptVersion, "attempt uses the new version even if it cannot complete");
        return result;
    }

    public static async Task ResponsesJsonOutputIsolation()
    {
        var result = await Run(ApiProtocol.OpenAIResponses, answer => Json(new
        {
            status = "completed", output = new[] { Message(answer[..5]), Message(answer[5..]) }
        }), streaming: false);
        Check.True(result.ModelResponded, "independent text messages still establish connection response");
        Check.NotEqual(ConnectionContextCheck.Passed, result.ContextCheck,
            "two distinct output messages, neither containing the answer, must not be joined into a passing answer");
    }

    public static async Task ResponsesSseOutputIsolation()
    {
        var result = await Run(ApiProtocol.OpenAIResponses, answer =>
            Event(new { type = "response.output_text.delta", item_id = "message-A", output_index = 0, content_index = 0, delta = answer[..5] }) +
            Event(new { type = "response.output_text.delta", item_id = "message-B", output_index = 1, content_index = 0, delta = answer[5..] }) +
            End(ApiProtocol.OpenAIResponses));
        Check.True(result.ModelResponded, "multi-item stream does not redefine the connection stage");
        Check.NotEqual(ConnectionContextCheck.Passed, result.ContextCheck,
            "deltas from different item_id/output_index values must not manufacture a passing answer");
    }

    public static async Task EmptyFinalSnapshotOverridesDeltas()
    {
        var result = await Run(ApiProtocol.OpenAIResponses, answer => Delta(ApiProtocol.OpenAIResponses, answer) +
            Event(new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>() } }));
        Check.NotEqual(ConnectionContextCheck.Passed, result.ContextCheck,
            "an explicit empty final output cannot confirm an answer present only in superseded deltas");
    }

    public static async Task ChatChoicesNeverJoin()
    {
        foreach (bool streaming in new[] { false, true })
        {
            var result = await Run(ApiProtocol.ChatCompletions, answer => streaming ?
                Event(new { choices = new[] { new { index = 0, delta = new { content = answer[..5] } }, new { index = 1, delta = new { content = answer[5..] } } } }) +
                    End(ApiProtocol.ChatCompletions) : Json(new { choices = new[]
                    {
                        new { index = 0, message = new { role = "assistant", content = answer[..5] } },
                        new { index = 1, message = new { role = "assistant", content = answer[5..] } }
                    } }), streaming);
            Check.True(result.ModelResponded, "each real partial choice establishes text response");
            Check.NotEqual(ConnectionContextCheck.Passed, result.ContextCheck, "JSON/SSE choices cannot be concatenated");
        }
    }

    public static async Task FragmentationAndFinalSnapshots()
    {
        foreach (ApiProtocol protocol in Enum.GetValues<ApiProtocol>())
        {
            var result = await Run(protocol, answer => ": \u4e2d\u6587 heartbeat\r\n" +
                string.Concat(answer.Select(ch => Delta(protocol, ch.ToString()))) +
                (protocol == ApiProtocol.OpenAIResponses ?
                    "data: {\"type\":\"response.completed\",\"response\":" + Body(protocol, answer) + "}\n\n" : End(protocol)),
                fragmented: true);
            Check.Equal(ConnectionContextCheck.Passed, result.ContextCheck,
                protocol + " character deltas, one-byte transport fragments and repeated final text pass exactly once");
        }
        foreach (string prior in new[] { "incorrect earlier delta" })
        {
            var result = await Run(ApiProtocol.OpenAIResponses, answer => Delta(ApiProtocol.OpenAIResponses, prior) +
                "data: {\"type\":\"response.completed\",\"response\":" + Body(ApiProtocol.OpenAIResponses, answer) + "}\n\n");
            Check.Equal(ConnectionContextCheck.Passed, result.ContextCheck, "complete final text supersedes partial capture");
        }
        var overridden = await Run(ApiProtocol.OpenAIResponses, answer => Delta(ApiProtocol.OpenAIResponses, answer) +
            "data: {\"type\":\"response.completed\",\"response\":" + Body(ApiProtocol.OpenAIResponses, "not the markers") + "}\n\n");
        Check.Equal(ConnectionContextCheck.Mismatch, overridden.ContextCheck, "wrong final snapshot supersedes apparently correct deltas");
    }

    public static async Task OverflowCannotBeErasedByFinalSnapshot()
    {
        var result = await Run(ApiProtocol.OpenAIResponses, answer => Delta(ApiProtocol.OpenAIResponses, new string('x', 2100)) +
            "data: {\"type\":\"response.completed\",\"response\":" + Body(ApiProtocol.OpenAIResponses, answer) + "}\n\n");
        Check.Equal(ConnectionContextCheck.Inconclusive, result.ContextCheck,
            "capture overflow is a safety limit and must not be erased by a later short snapshot");
    }

    public static async Task AmbiguousUnindexedChatChoices()
    {
        var result = await Run(ApiProtocol.ChatCompletions, answer =>
            Event(new { choices = new[] { new { delta = new { content = answer[..5] } }, new { delta = new { content = "other-A" } } } }) +
            Event(new { choices = new[] { new { delta = new { content = answer[5..] } }, new { delta = new { content = "other-B" } } } }) +
            End(ApiProtocol.ChatCompletions));
        Check.Equal(ConnectionContextCheck.Inconclusive, result.ContextCheck,
            "multiple choices without index cannot safely identify which output is being continued");
    }

    public static async Task SameMessageContentBlocksRemainValid()
    {
        foreach (ApiProtocol protocol in new[] { ApiProtocol.OpenAIResponses, ApiProtocol.AnthropicMessages })
        {
            var json = await Run(protocol, answer => protocol == ApiProtocol.OpenAIResponses ?
                Json(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[]
                    { new { type = "output_text", text = answer[..5] }, new { type = "output_text", text = answer[5..] } } } } }) :
                Json(new { type = "message", role = "assistant", content = new[]
                    { new { type = "text", text = answer[..5] }, new { type = "text", text = answer[5..] } }, stop_reason = "end_turn" }), streaming: false);
            Check.Equal(ConnectionContextCheck.Passed, json.ContextCheck, protocol + " joins content blocks only within one message");
            var sse = await Run(protocol, answer => protocol == ApiProtocol.OpenAIResponses ?
                Event(new { type = "response.output_text.delta", output_index = 0, item_id = "one-message", content_index = 0, delta = answer[..5] }) +
                Event(new { type = "response.output_text.delta", output_index = 0, item_id = "one-message", content_index = 1, delta = answer[5..] }) + End(protocol) :
                Event(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = answer[..2] } }) +
                Event(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = answer[2..5] } }) +
                Event(new { type = "content_block_stop", index = 0 }) +
                Event(new { type = "content_block_start", index = 1, content_block = new { type = "text", text = answer[5..] } }) +
                Event(new { type = "content_block_stop", index = 1 }) + End(protocol));
            Check.Equal(ConnectionContextCheck.Passed, sse.ContextCheck, protocol + " stream keeps same-message content blocks usable");
        }
    }

    public static async Task ResponsesIdentityConflictsAndFinalOutputIsolation()
    {
        foreach (string mode in new[] { "item-conflict", "index-conflict", "final-multi-message" })
        {
            var result = await Run(ApiProtocol.OpenAIResponses, answer =>
                Event(new { type = "response.output_text.delta", item_id = "message-A", output_index = 0, content_index = 0, delta = answer[..5] }) +
                Event(new { type = "response.output_text.delta", item_id = mode == "item-conflict" ? "message-B" : "message-A",
                    output_index = mode == "index-conflict" ? 1 : 0, content_index = 0, delta = answer[5..] }) +
                (mode == "final-multi-message" ? Event(new { type = "response.completed", response = new { status = "completed",
                    output = new[] { Message(answer[..5]), Message(answer[5..]) } } }) : End(ApiProtocol.OpenAIResponses)));
            Check.NotEqual(ConnectionContextCheck.Passed, result.ContextCheck, mode + " cannot be merged into a passing answer");
        }
    }

    public static async Task ChatIndexSafetyAndCompatibility()
    {
        foreach (bool streaming in new[] { false, true })
        foreach (string mode in new[] { "indexed", "unindexed-single", "reordered-indexed", "negative", "fractional", "string",
            "null", "duplicate-zero", "duplicate-other", "mixed-unindexed", "nonobject" })
        {
            var result = await Run(ApiProtocol.ChatCompletions, answer =>
            {
                var chosen = new JsonObject { ["index"] = 0, [streaming ? "delta" : "message"] =
                    new JsonObject { ["role"] = "assistant", ["content"] = answer } };
                var choices = new JsonArray { chosen };
                switch (mode)
                {
                    case "unindexed-single": chosen.Remove("index"); break;
                    case "reordered-indexed":
                        var other = chosen.DeepClone(); other["index"] = 1;
                        other[streaming ? "delta" : "message"]!["content"] = "another choice, not the answer";
                        choices.Insert(0, other); break;
                    case "negative": chosen["index"] = -1; break;
                    case "fractional": chosen["index"] = 0.5; break;
                    case "string": chosen["index"] = "0"; break;
                    case "null": chosen["index"] = null; break;
                    case "duplicate-zero": choices.Add(chosen.DeepClone()); break;
                    case "duplicate-other":
                        var one = chosen.DeepClone(); one["index"] = 1;
                        choices.Add(one); choices.Add(one.DeepClone()); break;
                    case "mixed-unindexed":
                        var missing = chosen.DeepClone(); missing.AsObject().Remove("index"); choices.Add(missing); break;
                    case "nonobject": choices.Add(17); break;
                }
                var body = new JsonObject { ["choices"] = choices }.ToJsonString();
                return streaming ? "data: " + body + "\n\n" + End(ApiProtocol.ChatCompletions) : body;
            }, streaming);
            Check.Equal(mode is "indexed" or "unindexed-single" or "reordered-indexed" ? ConnectionContextCheck.Passed : ConnectionContextCheck.Inconclusive,
                result.ContextCheck, $"Chat/{streaming}/{mode}: valid relay compatibility remains; ambiguous identities fail closed");
        }
        var switched = await Run(ApiProtocol.ChatCompletions, answer => Delta(ApiProtocol.ChatCompletions, answer[..5]) +
            Event(new { choices = new[] { new { delta = new { content = answer[5..] } } } }) + End(ApiProtocol.ChatCompletions));
        Check.Equal(ConnectionContextCheck.Inconclusive, switched.ContextCheck, "switching indexed/unindexed source mid-stream cannot manufacture a pass");
    }

    public static async Task ResponsesMetadataSafetyAndCompatibility()
    {
        foreach (string mode in new[] { "none", "output-only", "item-only", "both", "item-empty", "item-null", "item-number",
            "output-negative", "output-fractional", "output-string", "content-negative", "content-fractional", "content-reversed", "metadata-mode-change" })
        {
            var result = await Run(ApiProtocol.OpenAIResponses, answer =>
            {
                var first = new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = answer[..5] };
                if (mode is not ("none" or "item-only")) first["output_index"] = 0;
                if (mode is not ("none" or "output-only")) first["item_id"] = "same-message";
                switch (mode)
                {
                    case "item-empty": first["item_id"] = ""; break;
                    case "item-null": first["item_id"] = null; break;
                    case "item-number": first["item_id"] = 17; break;
                    case "output-negative": first["output_index"] = -1; break;
                    case "output-fractional": first["output_index"] = 0.5; break;
                    case "output-string": first["output_index"] = "0"; break;
                    case "content-negative": first["content_index"] = -1; break;
                    case "content-fractional": first["content_index"] = 0.5; break;
                    case "content-reversed": first["content_index"] = 1; break;
                }
                var second = first.DeepClone(); second["delta"] = answer[5..];
                if (mode == "content-reversed") second["content_index"] = 0;
                if (mode == "metadata-mode-change") { second.AsObject().Remove("item_id"); second.AsObject().Remove("output_index"); }
                return "data: " + first.ToJsonString() + "\n\ndata: " + second.ToJsonString() + "\n\n" +
                    // A later good snapshot must not erase an earlier ambiguous/invalid identity.
                    "data: {\"type\":\"response.completed\",\"response\":" + Body(ApiProtocol.OpenAIResponses, answer) + "}\n\n";
            });
            Check.Equal(mode is "none" or "output-only" or "item-only" or "both" ? ConnectionContextCheck.Passed : ConnectionContextCheck.Inconclusive,
                result.ContextCheck, "Responses/" + mode + ": unambiguous single-output compatibility and sticky safety boundaries");
        }
        foreach (bool overflow in new[] { false, true })
        {
            var result = await Run(ApiProtocol.OpenAIResponses, answer => Delta(ApiProtocol.OpenAIResponses,
                new string(' ', 2048 - answer.Length + (overflow ? 1 : 0)) + answer) +
                "data: {\"type\":\"response.completed\",\"response\":" + Body(ApiProtocol.OpenAIResponses, answer) + "}\n\n");
            Check.Equal(overflow ? ConnectionContextCheck.Inconclusive : ConnectionContextCheck.Passed, result.ContextCheck,
                "the 2048-character capture boundary is inclusive and overflow stays sticky");
        }
    }

    public static async Task InterruptedCorrectAnswersNeverPass()
    {
        foreach (ApiProtocol protocol in Enum.GetValues<ApiProtocol>())
        foreach (string mode in new[] { "broken", "truncated", "tail-error", "partial-tail" })
        {
            var result = await Run(protocol, answer =>
            {
                string delta = Delta(protocol, answer);
                if (mode == "truncated") return delta + (protocol switch
                {
                    ApiProtocol.ChatCompletions => Event(new { choices = new[] { new { index = 0, finish_reason = "length" } } }),
                    ApiProtocol.OpenAIResponses => Event(new { type = "response.incomplete" }),
                    _ => Event(new { type = "message_delta", delta = new { stop_reason = "max_tokens" } })
                }) + End(protocol);
                return delta + End(protocol) + (mode switch
                {
                    "tail-error" => Event(new { type = "error", error = new { message = "synthetic failure" } }),
                    "partial-tail" => "data: {",
                    _ => ""
                });
            }, broken: mode == "broken");
            Check.Equal(ConnectionContextCheck.Inconclusive, result.ContextCheck, protocol + "/" + mode + " cannot pass even with exact answer prefix");
            Check.False(result.ModelResponded, "untrusted terminal state is not confirmed response");
        }
    }

    public static async Task ExactAnswerRules()
    {
        foreach (ApiProtocol protocol in Enum.GetValues<ApiProtocol>())
        foreach (string mode in new[] { "outer-space", "reverse", "missing", "duplicate", "quoted", "separator" })
        {
            ConnectionContextCheck expected = mode == "outer-space" ? ConnectionContextCheck.Passed : ConnectionContextCheck.Mismatch;
            var result = await Run(protocol, answer =>
            {
                string output = mode switch
                {
                    "outer-space" => " \r\n" + answer + "\t ",
                    "reverse" => string.Join("|", answer.Split('|').Reverse()),
                    "missing" => answer[..9],
                    "duplicate" => answer + "|" + answer,
                    "quoted" => "\"" + answer + "\"",
                    _ => answer.Replace('|', ',')
                };
                // Reversing equal first/last random values is observationally the same answer.
                if (mode == "reverse" && output == answer) expected = ConnectionContextCheck.Passed;
                return Body(protocol, output);
            }, streaming: false);
            Check.Equal(expected, result.ContextCheck, protocol + "/" + mode + " strict marker grammar");
            Check.Equal(ConnectionTestStage.ModelResponded, result.Stage, "grammar mismatch does not alter connectivity");
        }
    }

    public static async Task MismatchDoesNotRetryOrPauseInspection()
    {
        foreach (InspectionTrigger trigger in Enum.GetValues<InspectionTrigger>())
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler().EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("model-a"));
            using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
            await Fixtures.ConfirmInspectionAsync(service, [new(profile.Id, true, ["model-a"])], enable: true);
            handler.EnqueueJson(HttpStatusCode.OK, Body(ApiProtocol.ChatCompletions, "I did not follow the copy instruction"));
            var round = await service.RunInspectionRoundAsync(trigger);
            Check.Equal(2, handler.Count, "one model fetch plus exactly one mismatch attempt");
            Check.Equal(1, round.Succeeded, "connection success counter stays independent of marker mismatch");
            Check.Equal(0, round.Failed + round.UnknownCount + round.PossibleSuccessCount + round.Skipped, "mismatch creates no extra counter");
            Check.Equal(ConnectionContextCheck.Mismatch, (await service.GetConnectionHistoryAsync()).Single().ContextCheck, "mismatch is retained");
            Check.Equal(InspectionAuthorizationState.Authorized, (await service.GetInspectionSnapshotAsync()).Targets.Single().Authorization,
                "mismatch must not pause an otherwise authorized connection target");
        }
    }

    public static Task LegacyPayloadAndStrictNewFields()
    {
        var record = new ConnectionTestResult { ProfileId = Guid.NewGuid(), KeyRevision = 1,
            BaseUrl = "https://relay.example.com/v1", ModelId = "model-a", Stage = ConnectionTestStage.ModelResponded, ModelResponded = true };
        var payload = new MigrationPayload { Vault = new Vault { ConnectionHistory = [record], ConnectionResults = new() { [record.ProfileId] = record } } };
        JsonNode legacy = JsonSerializer.SerializeToNode(payload, DesktopStore.Json)!;
        foreach (JsonNode? node in legacy["vault"]!["connectionHistory"]!.AsArray().Concat(
            legacy["vault"]!["connectionResults"]!.AsObject().Select(p => p.Value)))
        {
            node!.AsObject().Remove("contextCheck");
            node.AsObject().Remove("promptVersion");
        }
        var parsed = MigrationValidation.Parse(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        Check.Equal(ConnectionContextCheck.NotChecked, parsed.Vault.ConnectionHistory.Single().ContextCheck, "legacy payload is not inferred Passed");
        Check.Equal(0, parsed.Vault.ConnectionResults[record.ProfileId].PromptVersion, "legacy payload has no new prompt version");
        foreach (string mode in new[] { "missing-original", "unknown", "numeric-enum", "unknown-enum", "null-enum", "bad-version", "duplicate" })
        {
            JsonNode bad = legacy.DeepClone();
            var item = bad["vault"]!["connectionHistory"]![0]!.AsObject();
            switch (mode)
            {
                case "missing-original": item.Remove("modelId"); break;
                case "unknown": item["rawAnswer"] = "never allowed"; break;
                case "numeric-enum": item["contextCheck"] = 1; break;
                case "unknown-enum": item["contextCheck"] = "FuturePass"; break;
                case "null-enum": item["contextCheck"] = null; break;
                case "bad-version": item["promptVersion"] = "one"; break;
                default: item["contextCheck"] = "Passed"; break;
            }
            string json = bad.ToJsonString();
            if (mode == "duplicate") json = json.Replace("\"contextCheck\":\"Passed\"", "\"contextCheck\":\"Passed\",\"contextCheck\":\"Mismatch\"");
            bool rejected = false;
            try { MigrationValidation.Parse(Encoding.UTF8.GetBytes(json)); }
            catch (JsonException) { rejected = true; }
            Check.True(rejected, mode + " must still be rejected by full migration parsing");
        }
        return Task.CompletedTask;
    }

    public static async Task SummaryNeverRetainsPromptOrOutput()
    {
        using var tmp = new TempDir();
        using var export = new TempDir();
        var protector = new ReversibleTestProtector();
        var handler = new FakeHttpHandler();
        var prompts = new List<string>();
        var answers = new List<string>();
        const string sentinel = "synthetic-output-never-store-63be6e4d";
        for (int i = 0; i < 6; i++) handler.Enqueue(_ =>
        {
            string prompt = Prompt(handler.Requests.Last().Body, ApiProtocol.ChatCompletions);
            string answer = Answer(prompt);
            prompts.Add(prompt); answers.Add(answer);
            return new(HttpStatusCode.OK) { Content = new StringContent(Body(ApiProtocol.ChatCompletions,
                prompts.Count % 2 == 0 ? answer + sentinel : answer), Encoding.UTF8, "application/json") };
        });
        Guid id;
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            id = profile.Id;
            for (int i = 0; i < 6; i++) await service.TestConnectionAsync(id, "model-a", Consent(profile));
            Check.Equal(6, answers.Distinct().Count(), "fresh requests must not reuse a fixed reading challenge");
            string output = Path.Combine(export.Path, "summary.json");
            await service.ExportConnectionHistoryAsync(output);
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "desktop-state.json")));
            string decrypted = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(state.RootElement.GetProperty("protectedState").GetString()!)));
            string summary = await File.ReadAllTextAsync(output);
            foreach (string raw in prompts.Concat(answers).Append(sentinel))
            {
                Check.NotContains(decrypted, raw, "protected vault retains only summary, not request/output text");
                Check.NotContains(summary, raw, "export retains only summary, not request/output text");
            }
            Check.NotContains(decrypted, "FIRST=", "vault does not persist even escaped prompt text");
            using var exported = JsonDocument.Parse(summary);
            foreach (var row in exported.RootElement.GetProperty("records").EnumerateArray())
            {
                Check.Equal(1, row.GetProperty("promptVersion").GetInt32(), "exported attempt is versioned");
                Check.True(row.GetProperty("contextCheck").GetString() is "Passed" or "Mismatch", "export carries only the summary status");
            }
        }
        using var reopened = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), protector);
        Check.Equal(ConnectionContextCheck.Mismatch, (await reopened.GetLastConnectionResultAsync(id))!.ContextCheck, "new status survives reopen");
        Check.Equal(6, (await reopened.GetConnectionHistoryAsync()).Count, "all attempts survive without text retention");
    }

    private sealed class StreamContentBody : HttpContent
    {
        private readonly Stream stream;
        public StreamContentBody(Stream stream)
        {
            this.stream = stream;
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class FragmentStream(string body, bool broken) : MemoryStream(Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) return base.ReadAsync(buffer[..1], token);
            if (broken) throw new IOException("synthetic disconnect after completed prefix");
            return ValueTask.FromResult(0);
        }
    }
}
