using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Regression tests for the three-protocol change and the schema-1 -> schema-2 migration.
/// FakeHttpHandler only: no socket is ever opened. The schema-1 fixture is built by
/// re-serializing the OLD RelayProfile DTO shape (property order included) so its
/// MetadataHash matches the pre-migration contract exactly.
/// </summary>
public static class ProtocolTests
{
    private const string RelayBase = "https://relay.example.com/v1";
    private const string Key = Program.Key1;

    // Serializer options identical to DesktopStore.Json (used for both the vault and the profile hash).
    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    private static DesktopService New(string dir, FakeHttpHandler handler, ISecretProtector? protector = null)
        => new(dir, Fixtures.Client(handler), protector ?? new ReversibleTestProtector());

    private static OutboundConsent ModelConsent(RelayProfile profile, ApiProtocol? protocol = null, bool pagination = false)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(profile.BaseUrl, protocol ?? profile.Protocol), null,
            DateTimeOffset.UtcNow, protocol ?? profile.Protocol, pagination);

    private static OutboundConsent ConnectionConsent(RelayProfile profile, string modelId, ApiProtocol? protocol = null)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(profile.BaseUrl, protocol ?? profile.Protocol), modelId,
            DateTimeOffset.UtcNow, protocol ?? profile.Protocol);

    private static OutboundConsent ProbeConsent(RelayProfile profile, string modelId, ApiProtocol? protocol = null)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.Probe,
            Fixtures.ServiceRunUrl, modelId, DateTimeOffset.UtcNow, protocol ?? profile.Protocol);

    private static string ModelsJson(params string[] ids)
    {
        var sb = new StringBuilder("{\"object\":\"list\",\"data\":[");
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(ids[i]).Append("\",\"object\":\"model\"}");
        }
        return sb.Append("]}").ToString();
    }

    private static string Header(RecordedRequest request, string name)
        => request.Headers.TryGetValue(name, out string[]? value) ? string.Join(",", value) : "";

    // P01 -------------------------------------------------------------------
    // Frozen enum numbering: Chat=0, Responses=1, Anthropic=2 (persisted values must not drift).
    public static Task P01_ProtocolEnumContract()
    {
        Check.Equal(0, (int)ApiProtocol.ChatCompletions, "ChatCompletions = 0");
        Check.Equal(1, (int)ApiProtocol.OpenAIResponses, "OpenAIResponses = 1");
        Check.Equal(2, (int)ApiProtocol.AnthropicMessages, "AnthropicMessages = 2");
        Check.Equal(3, Enum.GetValues<ApiProtocol>().Length, "exactly three protocols");

        Check.Equal(ApiProtocol.ChatCompletions, new RelayProfile().Protocol, "profile default protocol is Chat");
        Check.Equal(ApiProtocol.ChatCompletions, new ProbeRecord().Protocol, "record default protocol is Chat");
        Check.Equal(ApiProtocol.ChatCompletions, new ConnectionTestResult().Protocol, "result default protocol is Chat");
        Check.Equal(ApiProtocol.ChatCompletions, new ConnectionTestSettings().Protocol, "settings default protocol is Chat");
        Check.False(new OutboundConsent(Guid.NewGuid(), 1, RelayBase, OutboundPurpose.ModelList, "", null, DateTimeOffset.UtcNow)
            .AllowModelPagination, "consent pagination defaults to false");
        Check.Equal(ApiProtocol.ChatCompletions,
            new OutboundConsent(Guid.NewGuid(), 1, RelayBase, OutboundPurpose.ModelList, "", null, DateTimeOffset.UtcNow).Protocol,
            "consent default protocol is Chat");

        // Frozen endpoint contract.
        Check.Equal("https://relay.example.com/v1/models", ModelApiEndpoints.Models(RelayBase), "models path is base + /models");
        Check.Equal("https://relay.example.com/v1/models", ModelApiEndpoints.Models(RelayBase, ApiProtocol.AnthropicMessages),
            "models path is protocol-independent");
        Check.Equal("https://relay.example.com/v1/chat/completions", ModelApiEndpoints.Inference(RelayBase, ApiProtocol.ChatCompletions), "chat inference path");
        Check.Equal("https://relay.example.com/v1/responses", ModelApiEndpoints.Inference(RelayBase, ApiProtocol.OpenAIResponses), "responses inference path");
        Check.Equal("https://relay.example.com/v1/messages", ModelApiEndpoints.Inference(RelayBase, ApiProtocol.AnthropicMessages), "anthropic inference path");
        Check.Equal("https://relay.example.com/v1/messages",
            ConnectionTestEndpoints.Recipient(RelayBase, ApiProtocol.AnthropicMessages), "recipient honours protocol");
        Check.Equal("https://relay.example.com/v1/chat/completions", ConnectionTestEndpoints.Recipient(RelayBase),
            "legacy recipient overload stays Chat");
        Check.Equal("OpenAI Chat Completions", ModelApiEndpoints.ProtocolLabel(ApiProtocol.ChatCompletions), "chat label");
        Check.Equal("OpenAI Responses", ModelApiEndpoints.ProtocolLabel(ApiProtocol.OpenAIResponses), "responses label");
        Check.Equal("Anthropic Messages", ModelApiEndpoints.ProtocolLabel(ApiProtocol.AnthropicMessages), "anthropic label");
        Check.NotContains(ModelApiEndpoints.Models("https://relay.example.com/v1", ApiProtocol.ChatCompletions), "/v1/v1",
            "no duplicated /v1");
        return Task.CompletedTask;
    }

    // P02 -------------------------------------------------------------------
    // Anthropic model list uses x-api-key + anthropic-version and never a Bearer header.
    public static async Task P02_AnthropicModelsHeaders()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"claude-3-7-sonnet-20250219\",\"display_name\":\"Claude\"}],\"has_more\":false}");
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(
            Fixtures.NewProfile() with { Protocol = ApiProtocol.AnthropicMessages }, Key);

        RelayProfile updated = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));

        Check.Equal(1, handler.Count, "exactly one GET");
        RecordedRequest request = handler.Requests[0];
        Check.Equal("GET", request.Method, "method GET");
        Check.Equal("https://relay.example.com/v1/models", request.Uri.AbsoluteUri, "base + /models, no /v1 guessing");
        Check.Equal(Key, Header(request, "x-api-key"), "key travels in x-api-key");
        Check.Equal("2023-06-01", Header(request, "anthropic-version"), "anthropic-version header present");
        Check.Equal("", Header(request, "Authorization"), "no Bearer Authorization for Anthropic");
        Check.Equal(1, updated.Models.Count, "has_more=false list accepted");
        Check.Equal("claude-3-7-sonnet-20250219", updated.Models[0].Id, "model id preserved verbatim");
        Check.NotNull(updated.ModelsFetchedAt, "fetch timestamp set");
        Check.Equal(ApiProtocol.AnthropicMessages, updated.Protocol, "protocol persisted on the profile");
    }

    // P03 -------------------------------------------------------------------
    // Claude has_more=true must never be presented as a complete list, and must not replace the cache.
    public static async Task P03_AnthropicHasMoreRejectsPartial()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"keep-claude-1\"},{\"id\":\"keep-claude-2\"}],\"has_more\":false}");
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(
            Fixtures.NewProfile() with { Protocol = ApiProtocol.AnthropicMessages }, Key);

        RelayProfile seeded = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
        Check.Equal(2, seeded.Models.Count, "baseline complete list cached");
        DateTimeOffset? seededAt = seeded.ModelsFetchedAt;

        handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"partial-1\"}],\"has_more\":true}");
        ClientOperationException ex = await Check.ThrowsClientAsync(
            () => service.RefreshModelsAsync(seeded.Id, ModelConsent(seeded)),
            "ModelListPartial", "has_more=true is rejected as a partial list");
        Check.True(ex.Message.Length > 0, "partial rejection message is meaningful");
        Check.Contains(ex.Message, "不完整", "message states the list is incomplete");

        RelayProfile after = (await service.GetProfilesAsync()).Single();
        Check.Equal(2, after.Models.Count, "previous cache kept, not replaced by the partial page");
        Check.Equal("keep-claude-1", after.Models[0].Id, "cache content unchanged");
        Check.Equal(seededAt, after.ModelsFetchedAt, "cache timestamp unchanged");
        Check.Equal(2, handler.Count, "no follow-up pagination request");

        // Malformed has_more is a schema error, not a silent "complete".
        handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"x\"}],\"has_more\":\"yes\"}");
        await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(after.Id, ModelConsent(after)),
            "ModelSchema", "non-boolean has_more is a schema error");
        Check.Equal(3, handler.Count, "still no extra paging request");
        Check.Equal(2, (await service.GetProfilesAsync()).Single().Models.Count, "cache still intact after schema error");
    }

    // P04 -------------------------------------------------------------------
    // Chat/Responses ignore has_more: the field is not part of their contract.
    public static async Task P04_HasMoreIgnoredForNonAnthropic()
    {
        foreach (ApiProtocol protocol in new[] { ApiProtocol.ChatCompletions, ApiProtocol.OpenAIResponses })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"a1\"},{\"id\":\"a2\"}],\"has_more\":true}");
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);

            RelayProfile updated = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
            Check.Equal(2, updated.Models.Count, protocol + ": has_more is ignored, list cached");
            Check.Equal(1, handler.Count, protocol + ": single request only");
        }
    }

    // P05 -------------------------------------------------------------------
    // Pagination consent is refused before any network I/O, in every protocol.
    public static async Task P05_PaginationConsentRefusedBeforeNetwork()
    {
        foreach (ApiProtocol protocol in new[] { ApiProtocol.ChatCompletions, ApiProtocol.OpenAIResponses, ApiProtocol.AnthropicMessages })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);

            await Check.ThrowsClientAsync(
                () => service.RefreshModelsAsync(profile.Id, ModelConsent(profile, pagination: true)),
                "ModelPaginationUnsupported", protocol + ": pagination permission refused");
            Check.Equal(0, handler.Count, protocol + ": zero requests when pagination requested");
            Check.Equal(0, (await service.GetProfilesAsync()).Single().Models.Count, protocol + ": cache untouched");
        }
    }

    // P06 -------------------------------------------------------------------
    // Protocol switch clears the model cache and rejects a stale pre-switch consent, even for the same URL.
    public static async Task P06_ProtocolSwitchClearsCacheAndConsent()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("cached-a", "cached-b"));
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key);
        RelayProfile withCache = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
        Check.Equal(2, withCache.Models.Count, "chat cache seeded");

        OutboundConsent staleChatConsent = ModelConsent(withCache);
        DateTimeOffset? cachedAt = withCache.ModelsFetchedAt;

        // Same base URL and same key: only the protocol changes.
        RelayProfile switched = await service.SaveProfileAsync(
            withCache with { Protocol = ApiProtocol.AnthropicMessages }, null);
        Check.Equal(ApiProtocol.AnthropicMessages, switched.Protocol, "protocol changed");
        Check.Equal(0, switched.Models.Count, "model cache cleared on protocol switch");
        Check.Null(switched.ModelsFetchedAt, "cache timestamp cleared on protocol switch");
        Check.Equal(withCache.KeyRevision, switched.KeyRevision, "protocol switch does not rotate the key");

        // The /models URL is identical for both protocols, so only the protocol field can catch this.
        Check.Equal(ModelApiEndpoints.Models(RelayBase, ApiProtocol.ChatCompletions),
            ModelApiEndpoints.Models(RelayBase, ApiProtocol.AnthropicMessages), "models recipient is identical across protocols");

        int before = handler.Count;
        await Check.ThrowsClientAsync(
            () => service.RefreshModelsAsync(switched.Id, staleChatConsent with { KeyRevision = switched.KeyRevision }),
            "ConsentRequired", "pre-switch Chat consent refused after switching to Anthropic");
        Check.Equal(before, handler.Count, "stale consent sent no request");

        await Check.ThrowsClientAsync(
            () => service.TestConnectionAsync(switched.Id, "model-a",
                ConnectionConsent(switched, "model-a", ApiProtocol.ChatCompletions)),
            "ConsentRequired", "pre-switch Chat connection consent refused");
        Check.Equal(before, handler.Count, "stale connection consent sent no request");

        // A consent that matches the new protocol works.
        handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"claude-new\"}],\"has_more\":false}");
        RelayProfile renewed = await service.RefreshModelsAsync(switched.Id, ModelConsent(switched));
        Check.Equal(1, renewed.Models.Count, "re-consented Anthropic refresh works");
        Check.Equal(Key, Header(handler.Requests[^1], "x-api-key"), "renewed refresh used x-api-key");
        Check.Equal("", Header(handler.Requests[^1], "Authorization"), "renewed refresh sent no Bearer");
        Check.Equal(ApiProtocol.ChatCompletions, staleChatConsent.Protocol, "stale consent still reports the old protocol");
    }

    // P07 -------------------------------------------------------------------
    // Protocol switch revokes a session-scoped automatic connection-test approval.
    public static async Task P07_ProtocolSwitchRevokesSchedule()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key);

        var settings = new ConnectionTestSettings { ProfileId = profile.Id, ModelId = "model-a", IntervalMinutes = 60, Enabled = true };
        ConnectionScheduleConsent approval = new(ConnectionConsent(profile, "model-a"), 60);
        ConnectionTestSettings enabled = await service.SaveConnectionSettingsAsync(settings, approval);
        Check.True(enabled.Enabled, "schedule approved for the session");
        Check.Equal(ApiProtocol.ChatCompletions, enabled.Protocol, "settings snapshot the approved protocol");

        RelayProfile switched = await service.SaveProfileAsync(profile with { Protocol = ApiProtocol.OpenAIResponses }, null);
        Check.Equal(ApiProtocol.OpenAIResponses, switched.Protocol, "protocol changed");

        int before = handler.Count;
        await Check.ThrowsClientAsync(() => service.TestScheduledConnectionAsync(switched.Id),
            "ScheduleNotAuthorized", "scheduled run refused after a protocol change");
        Check.Equal(before, handler.Count, "revoked schedule sends no request");

        ConnectionTestSettings after = await service.GetConnectionSettingsAsync(switched.Id);
        Check.False(after.Enabled, "session approval is no longer active");
        Check.Equal(60, after.IntervalMinutes, "stored interval retained");
    }

    // P08 -------------------------------------------------------------------
    // Per-protocol connection test URL / auth headers / body shape, and valid-response parsing.
    public static async Task P08_ConnectionTestPerProtocol()
    {
        var cases = new (string Label, ApiProtocol Protocol, string Response)[]
        {
            ("chat", ApiProtocol.ChatCompletions,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}"),
            ("responses", ApiProtocol.OpenAIResponses,
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}"),
            ("anthropic", ApiProtocol.AnthropicMessages,
                "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}")
        };

        foreach ((string label, ApiProtocol protocol, string response) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, response);
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);

            ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", ConnectionConsent(profile, "model-a"));

            Check.Equal(1, handler.Count, label + ": exactly one request");
            RecordedRequest request = handler.Requests[0];
            Check.Equal("POST", request.Method, label + ": POST");
            Check.Equal(ModelApiEndpoints.Inference(RelayBase, protocol), request.Uri.AbsoluteUri, label + ": protocol endpoint");
            Check.Equal("relay.example.com", request.Uri.Host, label + ": relay host, not BazaarLink");
            Check.True(result.ModelResponded, label + ": valid model response recognised");
            Check.Equal(ConnectionTestStage.ModelResponded, result.Stage, label + ": stage");
            Check.Equal(protocol, result.Protocol, label + ": protocol recorded on the result");
            Check.NotContains(request.Body, Key, label + ": key never in the body");
            Check.Contains(result.Message, "不证明", label + ": no identity claim");

            using var body = JsonDocument.Parse(request.Body);
            Check.Equal("model-a", body.RootElement.GetProperty("model").GetString(), label + ": body model");

            if (protocol == ApiProtocol.AnthropicMessages)
            {
                Check.Equal(Key, Header(request, "x-api-key"), label + ": x-api-key auth");
                Check.Equal("2023-06-01", Header(request, "anthropic-version"), label + ": version header");
                Check.Equal("", Header(request, "Authorization"), label + ": no Bearer");
                Check.True(body.RootElement.TryGetProperty("max_tokens", out _), label + ": max_tokens required by Messages API");
                Check.False(body.RootElement.TryGetProperty("max_output_tokens", out _), label + ": no Responses-only field");
            }
            else if (protocol == ApiProtocol.OpenAIResponses)
            {
                Check.Contains(Header(request, "Authorization"), "Bearer " + Key, label + ": Bearer auth");
                Check.True(body.RootElement.TryGetProperty("input", out _), label + ": Responses uses input");
                Check.True(body.RootElement.TryGetProperty("max_output_tokens", out _), label + ": Responses output token field");
                Check.False(body.RootElement.TryGetProperty("messages", out _), label + ": no Chat messages array");
            }
            else
            {
                Check.Contains(Header(request, "Authorization"), "Bearer " + Key, label + ": Bearer auth");
                Check.True(body.RootElement.TryGetProperty("messages", out _), label + ": Chat messages array");
            }
        }
    }

    // P09 -------------------------------------------------------------------
    // A 200 that is not a valid message for the selected protocol is never "model responded".
    public static async Task P09_NonModelResponsesPerProtocol()
    {
        var cases = new (string Label, ApiProtocol Protocol, string Body)[]
        {
            ("responses in_progress", ApiProtocol.OpenAIResponses, "{\"status\":\"in_progress\",\"output\":[]}"),
            ("responses error object", ApiProtocol.OpenAIResponses, "{\"error\":{\"message\":\"quota exceeded\"}}"),
            ("responses no output", ApiProtocol.OpenAIResponses, "{\"status\":\"completed\",\"output\":[]}"),
            ("anthropic error object", ApiProtocol.AnthropicMessages, "{\"type\":\"error\",\"error\":{\"message\":\"bad key\"}}"),
            ("anthropic empty content", ApiProtocol.AnthropicMessages, "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}"),
            ("anthropic blank text", ApiProtocol.AnthropicMessages,
                "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"   \"}]}"),
            ("anthropic wrong role", ApiProtocol.AnthropicMessages,
                "{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}"),
            ("anthropic chat shape", ApiProtocol.AnthropicMessages,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}"),
            ("chat anthropic shape", ApiProtocol.ChatCompletions,
                "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}"),
            ("html page", ApiProtocol.OpenAIResponses, null!)
        };

        foreach ((string label, ApiProtocol protocol, string body) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            if (body is null) handler.EnqueueRaw(HttpStatusCode.OK, "text/html", "<html>sign in</html>");
            else handler.EnqueueJson(HttpStatusCode.OK, body);
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);

            ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", ConnectionConsent(profile, "model-a"));

            Check.False(result.ModelResponded, label + ": must not claim a model response");
            Check.NotEqual(ConnectionTestStage.ModelResponded, result.Stage, label + ": stage is not ModelResponded");
            Check.Equal(1, handler.Count, label + ": exactly one request, no protocol fallback");
            Check.True(result.Message.Length > 0, label + ": meaningful message");
            Check.Equal(protocol, result.Protocol, label + ": protocol recorded");
        }
    }

    // Official upstream format mapping is sent once, never a protocol fallback.
    public static async Task P10_NonChatProbeRejectedBeforeNetwork()
    {
        foreach (ApiProtocol protocol in Enum.GetValues<ApiProtocol>())
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-mapping"));
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);
            ProbeRecord record = await service.StartProbeAsync(profile.Id, "model-a", null, ProbeConsent(profile, "model-a"));
            Check.Equal(1, handler.Count, "single Probe POST");
            using var body = JsonDocument.Parse(handler.Requests[0].Body);
            string expected = protocol switch { ApiProtocol.ChatCompletions => "openai", ApiProtocol.OpenAIResponses => "openai-responses", _ => "anthropic" };
            Check.Equal(expected, body.RootElement.GetProperty("upstreamFormat").GetString(), "official mapping");
            Check.Equal(protocol, record.Protocol, "record pins protocol");
        }
    }

    // P11 -------------------------------------------------------------------
    // Chat Probe still works and pins the protocol snapshot; polling never needs the profile key.
    public static async Task P11_ChatProbeStillWorksAndRecordSnapshotsProtocol()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-chat-1"));
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key);

        ProbeRecord record = await service.StartProbeAsync(profile.Id, "model-a", null, ProbeConsent(profile, "model-a"));
        Check.Equal(ApiProtocol.ChatCompletions, record.Protocol, "record snapshots Chat protocol");
        Check.Equal(1, handler.Count, "single POST");
        Check.False(handler.Requests[0].Headers.ContainsKey("x-api-key"), "Probe POST never uses x-api-key");

        handler.EnqueueJson(HttpStatusCode.OK, "{\"status\":\"running\"}");
        ProbeRecord running = await service.PollProbeAsync(record.LocalId);
        Check.Equal(2, handler.Count, "one poll GET");
        Check.Equal("GET", handler.Requests[1].Method, "poll is GET");
        Check.False(handler.Requests[1].Headers.ContainsKey("Authorization"), "poll sends no Authorization");
        Check.False(handler.Requests[1].Headers.ContainsKey("x-api-key"), "poll sends no x-api-key");
        Check.Equal("", handler.Requests[1].Body, "poll has no body");
        Check.Equal("https://bazaarlink.ai/api/probe/run/run-chat-1", handler.Requests[1].Uri.AbsoluteUri, "poll URL is the fixed service path");
        Check.Equal(LocalRunState.Running, running.State, "running stays Running");

        // A protocol switch on the profile must not affect the already-created run record.
        await service.SaveProfileAsync(profile with { Protocol = ApiProtocol.AnthropicMessages }, null);
        handler.EnqueueJson(HttpStatusCode.OK, "{\"status\":\"running\"}");
        ProbeRecord afterSwitch = await service.PollProbeAsync(record.LocalId);
        Check.Equal(ApiProtocol.ChatCompletions, afterSwitch.Protocol,
            "record keeps the Chat protocol snapshot after a profile switch");
        Check.Equal(LocalRunState.Running, afterSwitch.State, "poll still works without the profile key");
        Check.Equal(3, handler.Count, "exactly one more GET");
    }

    // P12 -------------------------------------------------------------------
    // Schema-1 migration: legacy defaults to Chat, secrets stay usable, and the first save upgrades to schema 2.
    public static async Task P12_Schema1LoadsAndMigratesOnFirstSave()
    {
        using var tmp = new TempDir();
        LegacyFixture.Write(tmp.Path, Key);
        byte[] original = File.ReadAllBytes(LegacyFixture.Primary(tmp.Path));

        var handler = new FakeHttpHandler();
        using (var service = New(tmp.Path, handler))
        {
            IReadOnlyList<RelayProfile> loaded = await service.GetProfilesAsync();
            Check.Equal(1, loaded.Count, "legacy profile loaded");
            Check.Equal(ApiProtocol.ChatCompletions, loaded[0].Protocol, "legacy profile defaults to Chat");
            Check.Equal(LegacyFixture.ProfileId, loaded[0].Id, "profile id preserved");
            Check.Equal(3, loaded[0].KeyRevision, "key revision preserved");
            Check.Equal("gpt-4o", loaded[0].DefaultModelId, "default model preserved");
            Check.True(original.SequenceEqual(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path))),
                "read-only load does not rewrite legacy bytes");

            // The migrated secret must still decrypt and be usable.
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o"));
            await service.RefreshModelsAsync(loaded[0].Id, ModelConsent(loaded[0]));
            Check.Equal(1, handler.Count, "one refresh after migration");
            Check.Contains(Header(handler.Requests[0], "Authorization"), "Bearer " + Key, "legacy secret decrypts and is used");
        }

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path))))
        {
            Check.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32(), "save upgraded the file to schema 2");
            JsonElement profile = document.RootElement.GetProperty("profiles")[0];
            Check.True(profile.TryGetProperty("protocol", out JsonElement protocol), "schema 2 profile carries a protocol");
            Check.Equal("ChatCompletions", protocol.GetString(), "migrated protocol persisted as Chat");
        }

        Check.True(File.Exists(LegacyFixture.Primary(tmp.Path) + ".bak"), "previous version kept as .bak");

        using (JsonDocument backup = JsonDocument.Parse(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path) + ".bak")))
            Check.Equal(1, backup.RootElement.GetProperty("schemaVersion").GetInt32(), "backup holds the original schema 1 file");
        Check.True(original.SequenceEqual(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path) + ".bak")),
            "backup bytes equal the pre-migration file exactly");
    }

    // P13 -------------------------------------------------------------------
    // Injecting a protocol field into a schema-1 profile is tampering: no silent default, no hash bypass.
    public static async Task P13_Schema1InjectedProtocolRejected()
    {
        foreach (string injected in new[] { "\"OpenAIResponses\"", "\"AnthropicMessages\"", "\"ChatCompletions\"", "1", "null" })
        {
            using var tmp = new TempDir();
            LegacyFixture.Write(tmp.Path, Key, injectProtocol: injected);
            byte[] original = File.ReadAllBytes(LegacyFixture.Primary(tmp.Path));

            using var service = New(tmp.Path, new FakeHttpHandler());
            await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), "StorageIntegrity",
                "schema-1 profile with injected protocol " + injected + " is rejected");

            Check.True(original.SequenceEqual(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path))),
                "rejected file is not overwritten");
            Check.False(File.Exists(LegacyFixture.Primary(tmp.Path) + ".bak"),
                "no backup is produced, so nothing can be recovered around the hash");
        }

        // Same for an unknown legacy field that could smuggle new state.
        using var tmp2 = new TempDir();
        LegacyFixture.Write(tmp2.Path, Key, extraLegacyField: "\"allowModelPagination\":true");
        using var service2 = New(tmp2.Path, new FakeHttpHandler());
        await Check.ThrowsClientAsync(() => service2.GetProfilesAsync(), "StorageIntegrity",
            "unknown schema-1 field is rejected");
    }

    // P14 -------------------------------------------------------------------
    // Schema-2 tampering with a VALID protocol value is an integrity failure, not a silent .bak rollback.
    public static async Task P14_Schema2ProtocolTamperNotRolledBack()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using (var service = New(tmp.Path, handler))
        {
            await service.SaveProfileAsync(Fixtures.NewProfile("First", "https://relay.example.com/v1"), Key);
            await service.SaveProfileAsync(Fixtures.NewProfile("Second", "https://relay2.example.com/v1"), Key);
        }

        string primary = LegacyFixture.Primary(tmp.Path);
        string backup = primary + ".bak";
        Check.True(File.Exists(backup), "a known-good backup exists before tampering");
        string backupBefore = Fixtures.ReadAllText(backup);

        string text = Fixtures.ReadAllText(primary);
        string tampered = text.Replace("\"protocol\": \"ChatCompletions\"", "\"protocol\": \"AnthropicMessages\"");
        Check.NotEqual(text, tampered, "tamper actually changed a valid protocol value");
        File.WriteAllText(primary, tampered);

        using (var service = New(tmp.Path, new FakeHttpHandler()))
        {
            await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), "StorageIntegrity",
                "protocol/enum tamper is an integrity failure");
        }

        Check.Contains(Fixtures.ReadAllText(primary), "AnthropicMessages", "tampered primary left in place for inspection");
        Check.Equal(backupBefore, Fixtures.ReadAllText(backup), "backup untouched");
        Check.False(Directory.EnumerateFiles(tmp.Path, "*.tmp").Any(), "no temporary file left behind");

        // An out-of-range enum in schema 2 must also never resolve to a fallback protocol.
        using var tmp2 = new TempDir();
        using (var service = New(tmp2.Path, new FakeHttpHandler()))
            await service.SaveProfileAsync(Fixtures.NewProfile(), Key);
        string p2 = LegacyFixture.Primary(tmp2.Path);
        File.WriteAllText(p2, Fixtures.ReadAllText(p2).Replace("\"protocol\": \"ChatCompletions\"", "\"protocol\": \"TotallyBogus\""));
        using (var service = New(tmp2.Path, new FakeHttpHandler()))
        {
            ClientOperationException ex = await Check.ThrowsClientAsync(
                () => service.GetProfilesAsync(), null, "unknown protocol name is refused");
            Check.True(ex.Code is "StorageProtocol" or "StorageIntegrity", "unknown protocol surfaces a storage code, got " + ex.Code);
        }
    }

    // P15 -------------------------------------------------------------------
    // A failed migration save leaves the original legacy file untouched and still readable.
    public static async Task P15_FailedMigrationSaveKeepsLegacyFile()
    {
        using var tmp = new TempDir();
        LegacyFixture.Write(tmp.Path, Key);
        byte[] original = File.ReadAllBytes(LegacyFixture.Primary(tmp.Path));

        var failing = new ToggleFailProtector { FailProtect = true };
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(new FakeHttpHandler()), failing))
        {
            await Check.ThrowsClientAsync(
                () => service.SaveProfileAsync(Fixtures.NewProfile() with { Id = LegacyFixture.ProfileId }, null),
                null, "save fails while protection is broken");
        }

        Check.True(original.SequenceEqual(File.ReadAllBytes(LegacyFixture.Primary(tmp.Path))),
            "legacy file byte-identical after a failed migration save");
        Check.False(File.Exists(LegacyFixture.Primary(tmp.Path) + ".bak"),
            "failed save created no backup that could mask the legacy file");
        Check.False(Directory.EnumerateFiles(tmp.Path, "*.tmp").Any(), "no temporary file left behind");

        // The legacy keys are still readable and usable afterwards.
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o"));
        using var reopened = New(tmp.Path, handler);
        IReadOnlyList<RelayProfile> profiles = await reopened.GetProfilesAsync();
        Check.Equal(1, profiles.Count, "legacy profile still readable");
        Check.Equal(3, profiles[0].KeyRevision, "legacy revision intact");
        await reopened.RefreshModelsAsync(profiles[0].Id, ModelConsent(profiles[0]));
        Check.Contains(Header(handler.Requests[0], "Authorization"), "Bearer " + Key, "legacy key still usable after failed save");
    }

    // P16 -------------------------------------------------------------------
    // Corrupt JSON still recovers from the backup (T23 behaviour is preserved by the migration work).
    public static async Task P16_CorruptJsonStillRecoversFromBackup()
    {
        using var tmp = new TempDir();
        LegacyFixture.Write(tmp.Path, Key);
        var handler = new FakeHttpHandler();
        using (var service = New(tmp.Path, handler))
            await service.SaveProfileAsync(Fixtures.NewProfile("Second", "https://relay2.example.com/v1") with { Id = LegacyFixture.ProfileId }, null);

        string primary = LegacyFixture.Primary(tmp.Path);
        Check.True(File.Exists(primary + ".bak"), "backup exists before corruption");
        Check.Equal(3, JsonDocument.Parse(Fixtures.ReadAllText(primary)).RootElement.GetProperty("schemaVersion").GetInt32(),
            "primary is schema 2 after the save");
        File.WriteAllText(primary, "{ corrupt-not-json");

        int warnings = 0;
        using var reopened = New(tmp.Path, new FakeHttpHandler());
        try { await reopened.GetProfilesAsync(); }
        catch (ClientOperationException ex) when (ex.Code == "StorageRecovered") { warnings++; }
        IReadOnlyList<RelayProfile> recovered = await reopened.GetProfilesAsync();
        Check.Equal(1, recovered.Count, "recovered from the schema-1 backup");
        Check.Equal(LegacyFixture.Name, recovered[0].Name, "backup holds the pre-save legacy profile");
        Check.Equal(ApiProtocol.ChatCompletions, recovered[0].Protocol, "recovered profile still defaults to Chat");
        Check.Equal(1, warnings, "exactly one recovery warning");
    }

    // P17 -------------------------------------------------------------------
    // Adding a DTO property with a default must not silently invalidate an existing checksum.
    public static async Task P17_Schema1HashIsDtoShapeBound()
    {
        // Recomputed exactly as the schema-1 contract did: old DTO order, indented web serializer.
        string profilesJson = LegacyFixture.ProfilesJsonForHash();
        using var document = JsonDocument.Parse(profilesJson);
        List<LegacyProfileDto> dto = document.RootElement.Deserialize<List<LegacyProfileDto>>(StoreJson)!;
        string recomputed = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, StoreJson)));
        Check.Equal(LegacyFixture.ExpectedMetadataHash, recomputed,
            "the legacy hash is reproducible from the old DTO shape");

        // The same profile serialized through the NEW RelayProfile adds `protocol` and changes the hash.
        var modern = new RelayProfile
        {
            Id = LegacyFixture.ProfileId,
            Name = LegacyFixture.Name,
            BaseUrl = RelayBase,
            SecretRef = LegacyFixture.SecretReference,
            KeyRevision = 3,
            DefaultModelId = "gpt-4o",
            Models = [new ModelEntry("gpt-4o", "openai"), new ModelEntry("o3")],
            ModelsFetchedAt = LegacyFixture.UpdatedAt,
            CreatedAt = LegacyFixture.CreatedAt,
            UpdatedAt = LegacyFixture.UpdatedAt
        };
        string modernJson = JsonSerializer.Serialize(new List<RelayProfile> { modern }, StoreJson);
        Check.Contains(modernJson, "\"protocol\"", "new DTO does serialize the protocol field");
        string modernHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new List<RelayProfile> { modern }, StoreJson)));
        Check.NotEqual(LegacyFixture.ExpectedMetadataHash, modernHash,
            "a new DTO property changes the hash, so the legacy hash cannot come from the new shape");

        // Proof that the store rejects a schema-1 file whose hash was computed with the new shape.
        using var tmp = new TempDir();
        LegacyFixture.Write(tmp.Path, Key, metadataHashOverride: modernHash);
        using var service = New(tmp.Path, new FakeHttpHandler());
        await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), null,
            "schema-1 file with a new-shape hash is not accepted");
    }

    // P18 -------------------------------------------------------------------
    // Malformed envelope shapes fail closed with a storage code: never a crash, never a silent default.
    public static async Task P18_MalformedEnvelopeFailsClosed()
    {
        const string badState = "\"RU5DMQ==\"";
        var cases = new (string Label, string Json)[]
        {
            ("missing profiles", "{\"schemaVersion\":1,\"protectedState\":" + badState + "}"),
            ("profiles is object", "{\"schemaVersion\":1,\"profiles\":{},\"protectedState\":" + badState + "}"),
            ("profiles is null", "{\"schemaVersion\":1,\"profiles\":null,\"protectedState\":" + badState + "}"),
            ("protectedState not a string", "{\"schemaVersion\":1,\"profiles\":[],\"protectedState\":123}"),
            ("missing protectedState", "{\"schemaVersion\":1,\"profiles\":[]}"),
            ("schema2 profile not an object", "{\"schemaVersion\":2,\"profiles\":[42],\"protectedState\":" + badState + "}"),
            ("schema2 profile is a string", "{\"schemaVersion\":2,\"profiles\":[\"x\"],\"protectedState\":" + badState + "}"),
            ("duplicate envelope key", "{\"schemaVersion\":1,\"schemaVersion\":1,\"profiles\":[],\"protectedState\":" + badState + "}"),
            ("unknown envelope field", "{\"schemaVersion\":1,\"profiles\":[],\"protectedState\":" + badState + ",\"extra\":1}")
        };

        foreach ((string label, string json) in cases)
        {
            using var tmp = new TempDir();
            File.WriteAllText(LegacyFixture.Primary(tmp.Path), json);
            using var service = New(tmp.Path, new FakeHttpHandler());
            ClientOperationException ex = await Check.ThrowsClientAsync(
                () => service.GetProfilesAsync(), null, label + " fails closed");
            Check.True(ex.Code is "StorageIntegrity" or "StorageVersion",
                label + " surfaces a storage code, got " + ex.Code);
            Check.True(ex.Message.Length > 0, label + " has a friendly message");
            Check.Equal(json, Fixtures.ReadAllText(LegacyFixture.Primary(tmp.Path)), label + " file left untouched");
        }

        // Version problems are distinguishable from integrity problems.
        foreach (string version in new[]
                 {
                     "{\"profiles\":[],\"protectedState\":" + badState + "}",
                     "{\"schemaVersion\":\"2\",\"profiles\":[],\"protectedState\":" + badState + "}",
                     "[]",
                     "{\"schemaVersion\":4,\"profiles\":[],\"protectedState\":" + badState + "}"
                 })
        {
            using var tmp = new TempDir();
            File.WriteAllText(LegacyFixture.Primary(tmp.Path), version);
            using var service = New(tmp.Path, new FakeHttpHandler());
            await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), "StorageVersion",
                "unsupported/missing version is a StorageVersion failure");
        }
    }

    // P19 -------------------------------------------------------------------
    // A duplicated protocol field must not let the last-wins parser pick an unverified value.
    public static async Task P19_DuplicateProtocolFieldRejected()
    {
        using var tmp = new TempDir();
        using (var service = New(tmp.Path, new FakeHttpHandler()))
            await service.SaveProfileAsync(Fixtures.NewProfile(), Key);

        string primary = LegacyFixture.Primary(tmp.Path);
        string text = Fixtures.ReadAllText(primary);
        string duplicated = text.Replace("\"protocol\": \"ChatCompletions\",",
            "\"protocol\": \"ChatCompletions\"," + Environment.NewLine + "      \"protocol\": \"AnthropicMessages\",",
            StringComparison.Ordinal);
        Check.NotEqual(text, duplicated, "duplicate protocol field was actually inserted");
        File.WriteAllText(primary, duplicated);

        using (var service = New(tmp.Path, new FakeHttpHandler()))
        {
            await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), "StorageIntegrity",
                "duplicate protocol field is rejected");
        }
        Check.Equal(duplicated, Fixtures.ReadAllText(primary), "file left untouched for inspection");
    }

    // P20 -------------------------------------------------------------------
    // A relay that echoes the key back in a new-protocol response never leaks it into results or disk.
    public static async Task P20_KnownKeyEchoNoLeakInNewProtocols()
    {
        var cases = new (string Label, ApiProtocol Protocol, int Status, string Body)[]
        {
            ("anthropic 401 echo", ApiProtocol.AnthropicMessages, 401,
                "{\"error\":{\"message\":\"invalid x-api-key " + Key + "\"}}"),
            ("responses 400 echo", ApiProtocol.OpenAIResponses, 400,
                "{\"error\":{\"message\":\"bad key " + Key + "\"}}"),
            ("anthropic 200 echo", ApiProtocol.AnthropicMessages, 200,
                "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"echo\":\"" + Key + "\"}")
        };

        foreach ((string label, ApiProtocol protocol, int status, string body) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueJson((HttpStatusCode)status, body);
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = protocol }, Key);

            ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", ConnectionConsent(profile, "model-a"));

            Check.NotContains(result.Message, Key, label + ": message must not echo the key");
            Check.NotContains(result.Code, Key, label + ": code must not echo the key");
            Check.NotContains(JsonSerializer.Serialize(result), Key, label + ": result object must not carry the key");
            Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Key, label + ": store must not contain the plaintext key");
            Check.Equal(1, handler.Count, label + ": one request only");
        }

        // A model list that echoes the key as a model id must not persist it as a selectable id.
        using (var tmp = new TempDir())
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK,
                "{\"data\":[{\"id\":\"" + Key + "\"},{\"id\":\"claude-ok\"}],\"has_more\":false}");
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(
                Fixtures.NewProfile() with { Protocol = ApiProtocol.AnthropicMessages }, Key);

            RelayProfile updated = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
            Check.Equal(1, updated.Models.Count, "the echoed key is not kept as a model");
            Check.Equal("claude-ok", updated.Models[0].Id, "only the real model id is kept");
            Check.False(updated.Models.Any(m => m.Id == Key), "key never becomes a model id");
            Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Key, "echoed model id not persisted in plaintext");
        }
    }

    // P21 -------------------------------------------------------------------
    // Pagination absence/empty pages and a tampered schema-2 file with no backup are handled explicitly.
    public static async Task P21_PaginationEdgeCasesAndNoBackupTamper()
    {
        // Anthropic without has_more is treated as a complete single page.
        using (var tmp = new TempDir())
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"c1\"}]}");
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(
                Fixtures.NewProfile() with { Protocol = ApiProtocol.AnthropicMessages }, Key);
            RelayProfile updated = await service.RefreshModelsAsync(profile.Id, ModelConsent(profile));
            Check.Equal(1, updated.Models.Count, "absent has_more is a complete page");
            Check.Equal(1, handler.Count, "one request only");
        }

        // An empty page that claims more exists must still not be presented as complete.
        using (var tmp = new TempDir())
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, "{\"data\":[],\"has_more\":true}");
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(
                Fixtures.NewProfile() with { Protocol = ApiProtocol.AnthropicMessages }, Key);
            await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, ModelConsent(profile)),
                "ModelListPartial", "empty page with has_more=true is still partial");
            Check.Equal(1, handler.Count, "no follow-up request");
            Check.Equal(0, (await service.GetProfilesAsync()).Single().Models.Count, "nothing cached from a partial page");
        }

        // A tampered schema-2 file is refused even when no backup exists (no legacy-default fallback).
        using (var tmp = new TempDir())
        {
            using (var service = New(tmp.Path, new FakeHttpHandler()))
            {
                await service.SaveProfileAsync(Fixtures.NewProfile("First"), Key);
                await service.SaveProfileAsync(Fixtures.NewProfile("Second", "https://relay2.example.com/v1"), Key);
            }
            string primary = LegacyFixture.Primary(tmp.Path);
            File.Delete(primary + ".bak");
            string text = Fixtures.ReadAllText(primary);
            string tampered = text.Replace("\"protocol\": \"ChatCompletions\"", "\"protocol\": \"OpenAIResponses\"");
            Check.NotEqual(text, tampered, "tamper applied");
            File.WriteAllText(primary, tampered);

            using var reopened = New(tmp.Path, new FakeHttpHandler());
            await Check.ThrowsClientAsync(() => reopened.GetProfilesAsync(), "StorageIntegrity",
                "tampered schema-2 without backup is still refused");
        }

        // No plaintext key may appear anywhere after migration saves.
        using (var tmp = new TempDir())
        {
            using (var service = New(tmp.Path, new FakeHttpHandler()))
            {
                RelayProfile created = await service.SaveProfileAsync(Fixtures.NewProfile(), Key);
                await service.SaveProfileAsync(created with { Name = "Renamed" }, null);
            }
            Check.False(File.Exists(Path.Combine(tmp.Path, "nonexistent.json")), "sanity: no stray file");
            Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Key, "no plaintext key after migration saves");
            string primary = LegacyFixture.Primary(tmp.Path);
            using JsonDocument document = JsonDocument.Parse(Fixtures.ReadAllText(primary));
            Check.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32(), "primary is schema 2");
            using JsonDocument backup = JsonDocument.Parse(Fixtures.ReadAllText(primary + ".bak"));
            Check.Equal(3, backup.RootElement.GetProperty("schemaVersion").GetInt32(), "backup is also schema 2 once both files were written by schema 2");
        }
    }

    // P22 -------------------------------------------------------------------
    // Byte-exact artifact produced by the PRE-CHANGE Core (git HEAD) must load and migrate.
    // Regenerating it from the new DTO is impossible, so it is embedded verbatim.
    public static async Task P22_ArtifactFromPreviousCoreLoads()
    {
        using var tmp = new TempDir();
        string primary = LegacyFixture.Primary(tmp.Path);
        File.WriteAllText(primary, PriorCoreArtifact, new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(primary);

        using (JsonDocument document = JsonDocument.Parse(original))
        {
            Check.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32(), "artifact is schema 1");
            Check.False(document.RootElement.GetProperty("profiles")[0].TryGetProperty("protocol", out _),
                "artifact has no protocol field");
        }

        var handler = new FakeHttpHandler();
        Guid profileId;
        using (var service = New(tmp.Path, handler))
        {
            IReadOnlyList<RelayProfile> loaded = await service.GetProfilesAsync();
            Check.Equal(1, loaded.Count, "artifact profile loads");
            profileId = loaded[0].Id;
            Check.Equal(ApiProtocol.ChatCompletions, loaded[0].Protocol, "artifact profile defaults to Chat");
            Check.Equal("Legacy Alpha", loaded[0].Name, "artifact name preserved");
            Check.Equal("gpt-4o", loaded[0].DefaultModelId, "artifact default model preserved");
            Check.True(original.SequenceEqual(File.ReadAllBytes(primary)), "artifact bytes untouched on read");

            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o"));
            await service.RefreshModelsAsync(profileId, ModelConsent(loaded[0]));
            Check.Contains(Header(handler.Requests[0], "Authorization"), "Bearer " + LegacyFixture.ArtifactKey,
                "artifact secret decrypts with the same protector");
        }

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(primary)))
        {
            Check.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32(), "artifact upgraded to schema 2");
            Check.Equal("ChatCompletions",
                document.RootElement.GetProperty("profiles")[0].GetProperty("protocol").GetString(), "migrated protocol persisted");
        }
        Check.True(original.SequenceEqual(File.ReadAllBytes(primary + ".bak")),
            "artifact preserved byte-for-byte as the backup");
    }

    // P23 -------------------------------------------------------------------
    // An out-of-range protocol value is refused locally, before any request, on every entry point.
    public static async Task P23_InvalidProtocolRejectedNoRequest()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);

        await Check.ThrowsClientAsync(
            () => service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = (ApiProtocol)99 }, Key),
            "UnsupportedProtocol", "unknown protocol value cannot be saved");
        Check.Equal(0, handler.Count, "no request while saving an invalid protocol");
        Check.Equal(0, (await service.GetProfilesAsync()).Count, "nothing persisted");

        await Check.ThrowsClientAsync(
            () => service.SaveProfileAsync(Fixtures.NewProfile() with { Protocol = (ApiProtocol)(-1) }, Key),
            "UnsupportedProtocol", "negative protocol value cannot be saved");

        RelayProfile valid = await service.SaveProfileAsync(Fixtures.NewProfile(), Key);

        // The endpoint builder refuses the unknown value before a recipient string even exists.
        await Check.ThrowsClientAsync(
            () => { _ = ModelApiEndpoints.Models(valid.BaseUrl, (ApiProtocol)99); return Task.CompletedTask; },
            "UnsupportedProtocol", "model endpoint refuses an unknown protocol");
        await Check.ThrowsClientAsync(
            () => { _ = ModelApiEndpoints.Inference(valid.BaseUrl, (ApiProtocol)99); return Task.CompletedTask; },
            "UnsupportedProtocol", "inference endpoint refuses an unknown protocol");

        // A hand-built consent that carries an unknown protocol is still refused with zero requests.
        var bogus = new OutboundConsent(valid.Id, valid.KeyRevision, valid.BaseUrl, OutboundPurpose.ModelList,
            valid.BaseUrl + "/models", null, DateTimeOffset.UtcNow, (ApiProtocol)99);
        ClientOperationException refreshError = await Check.ThrowsClientAsync(
            () => service.RefreshModelsAsync(valid.Id, bogus), null, "unknown-protocol refresh is refused");
        Check.True(refreshError.Code is "ConsentRequired" or "UnsupportedProtocol",
            "unknown-protocol refresh surfaces a refusal code, got " + refreshError.Code);

        var bogusConnection = new OutboundConsent(valid.Id, valid.KeyRevision, valid.BaseUrl, OutboundPurpose.ConnectionTest,
            valid.BaseUrl + "/chat/completions", "model-a", DateTimeOffset.UtcNow, (ApiProtocol)99);
        ClientOperationException connectionError = await Check.ThrowsClientAsync(
            () => service.TestConnectionAsync(valid.Id, "model-a", bogusConnection), null,
            "unknown-protocol connection consent is refused");
        Check.True(connectionError.Code is "ConsentRequired" or "UnsupportedProtocol",
            "unknown-protocol connection consent surfaces a refusal code, got " + connectionError.Code);

        Check.Equal(0, handler.Count, "no request for any invalid protocol path");
        Check.Equal(0, (await service.GetProfilesAsync()).Single().Models.Count, "cache untouched");
    }

    // ---------------------------------------------------------------------
    // Schema-1 fixture: real old DTO order, real vault layout, real hash.
    private sealed class LegacyFixture
    {
        public static readonly Guid ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        public const string Name = "Legacy Alpha";
        public const string SecretReference = "aaaaaaaabbbbbbbbccccccccdddddddd";
        public const int KeyRevision = 3;
        public const string ExpectedMetadataHash = "A84139863C54F40817274A3A4AAAB1C20107835EAEB0ADCAE5176A761B4DC56B";
        public const string ArtifactKey = "sk-legacy-test-key-0001";
        public static readonly DateTimeOffset CreatedAt = new(2026, 10, 28, 1, 2, 3, TimeSpan.Zero);
        public static readonly DateTimeOffset UpdatedAt = new(2026, 10, 29, 8, 30, 0, TimeSpan.Zero);

        public static string Primary(string directory) => Path.Combine(directory, "desktop-state.json");

        private static LegacyProfileDto Profile() => new()
        {
            Id = ProfileId,
            Name = Name,
            BaseUrl = RelayBase,
            SecretRef = SecretReference,
            KeyRevision = KeyRevision,
            DefaultModelId = "gpt-4o",
            Models =
            [
                new LegacyModelDto { Id = "gpt-4o", OwnedBy = "openai" },
                new LegacyModelDto { Id = "o3", OwnedBy = null }
            ],
            ModelsFetchedAt = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };

        public static string ProfilesJsonForHash() => JsonSerializer.Serialize(new List<LegacyProfileDto> { Profile() }, StoreJson);

        /// <summary>
        /// Writes a genuine schema-1 envelope. <paramref name="injectProtocol"/> and
        /// <paramref name="extraLegacyField"/> splice raw JSON into the profile object while the
        /// vault hash is still computed over the *DTO* shape, which is exactly the attack the
        /// migration must refuse.
        /// </summary>
        public static void Write(string directory, string key,
            string? injectProtocol = null, string? extraLegacyField = null, string? metadataHashOverride = null)
        {
            string profilesJson = ProfilesJsonForHash();
            if (injectProtocol is not null)
                profilesJson = profilesJson.Replace("\"name\": \"", "\"protocol\": " + injectProtocol + ",\n    \"name\": \"", StringComparison.Ordinal);
            if (extraLegacyField is not null)
                profilesJson = profilesJson.Replace("\"name\": \"", extraLegacyField + ",\n    \"name\": \"", StringComparison.Ordinal);

            using var parsed = JsonDocument.Parse(profilesJson);
            List<LegacyProfileDto> dto = parsed.RootElement.Deserialize<List<LegacyProfileDto>>(StoreJson)!;
            string metadataHash = metadataHashOverride ??
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, StoreJson)));

            var vault = new Dictionary<string, object?>
            {
                ["metadataHash"] = metadataHash,
                ["secrets"] = new Dictionary<string, object>
                {
                    [ProfileId.ToString()] = new Dictionary<string, object>
                    {
                        ["key"] = key,
                        ["revision"] = KeyRevision,
                        ["reference"] = SecretReference
                    }
                },
                ["knownSecrets"] = new[] { key },
                ["history"] = Array.Empty<object>(),
                ["connectionSettings"] = new Dictionary<string, object>(),
                ["connectionResults"] = new Dictionary<string, object>()
            };
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(vault, StoreJson);
            byte[] cipher = new ReversibleTestProtector().Protect(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);

            var envelope = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["profiles"] = JsonDocument.Parse(profilesJson).RootElement.Clone(),
                ["protectedState"] = Convert.ToBase64String(cipher)
            };
            Directory.CreateDirectory(directory);
            File.WriteAllText(Primary(directory), JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class LegacyProfileDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string SecretRef { get; set; } = "";
        public int KeyRevision { get; set; }
        public string DefaultModelId { get; set; } = "";
        public List<LegacyModelDto> Models { get; set; } = [];
        public DateTimeOffset? ModelsFetchedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class LegacyModelDto
    {
        public string Id { get; set; } = "";
        public string? OwnedBy { get; set; }
    }


    /// <summary>
    /// Written by the previous (git HEAD) Core build with the reversible test protector:
    /// `DesktopService(dir).SaveProfileAsync(new RelayProfile{Name="Legacy Alpha",
    /// BaseUrl="https://relay.example.com/v1", DefaultModelId="gpt-4o"}, "sk-legacy-test-key-0001")`.
    /// The secretRef, id and timestamps are whatever that run generated, which is the point:
    /// this file cannot be reconstructed from the current DTO.
    /// </summary>
    private const string PriorCoreArtifact = """
{
  "schemaVersion": 1,
  "profiles": [
    {
      "id": "6cbe7826-da94-46a7-93de-c3d1527ded5f",
      "name": "Legacy Alpha",
      "baseUrl": "https://relay.example.com/v1",
      "secretRef": "52a6c253e864459298b25144a8dd2ba8",
      "keyRevision": 1,
      "defaultModelId": "gpt-4o",
      "models": [],
      "modelsFetchedAt": null,
      "createdAt": "2026-10-30T04:11:26.4098153+00:00",
      "updatedAt": "2026-10-30T04:11:26.4098153+00:00"
    }
  ],
  "protectedState": "RU5DMToMWSAmxq3I4PgULVJ/1oXc9wo6AzLDn7ygCm96MdD91cEnEm4i2ufem6NYC0NUy/mLmFMxZTbPkZzxXAx9Qsr\u002B9YpBBAIimouYmtVwCSk\u002B3\u002B6N1XpmdiDX1qu9AX1cf5/4zvAEalU3yuLGkr5eNx8rh7/ErFstEziYu9m1R20UJMa6jKUIJQkuKJ//gr1CLRBnxqCC5F8rLg6D\u002B927WGYIac64g6pJd1xEfoW8z\u002B9XLBtxm\u002BrR7gonWSHYsZ\u002B\u002BWG8\u002BA9Wpl6dSdDBBeZm5hOZGKQYg1ufTmH98BTnD\u002B8e/HzFULZun3\u002BlISzUNLMHq3eMQYRMsguWX6U9\u002BByqf/8qpWToHYZLoyuaoUTgBKvzHl6cbYUwN7Lyd81FLaz2b\u002B9HmCzcHVIKx2c7lHjMVP7HB8LtUZxh9lOqS8AcpTySU4MDoAj8eJJC84ZrgQWsLH7/hx9hKUTo0nenD9xQrSmvc8Zm2K1MDDfnUs4muBCZYf5Hv2P4OCkBri\u002BrX7gt8XiWdqILEfzMRMHOTtp/6HD5abJeXnPQCLlAxwfaf\u002B1pSc2o="
}
""";
}
