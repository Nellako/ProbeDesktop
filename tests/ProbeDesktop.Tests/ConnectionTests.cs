using System.Net;
using System.Text;
using System.Text.Json;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Regression tests for IDirectConnectionService (DesktopService).
/// FakeHttpHandler only: no socket is ever opened and no real relay/BazaarLink host is contacted.
/// Codes asserted here are the codes the shipped Core contract actually produces.
/// </summary>
public static class ConnectionTests
{
    // A single direct POST to the confirmed relay: {base}/chat/completions with Bearer + target key.
    private const string ChatUrl = "https://relay.example.com/v1/chat/completions";

    private static DesktopService New(string dir, FakeHttpHandler handler, ISecretProtector? protector = null)
        => new(dir, Fixtures.Client(handler), protector ?? new ReversibleTestProtector());

    private static OutboundConsent Consent(RelayProfile profile, string modelId, DateTimeOffset? acceptedAt = null)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(profile.BaseUrl), modelId, acceptedAt ?? DateTimeOffset.UtcNow);

    private static ConnectionScheduleConsent Schedule(RelayProfile profile, string modelId, int intervalMinutes, DateTimeOffset? acceptedAt = null)
        => new(Consent(profile, modelId, acceptedAt), intervalMinutes);

    private static ConnectionTestSettings Settings(RelayProfile profile, string modelId, int intervalMinutes, bool enabled)
        => new() { ProfileId = profile.Id, ModelId = modelId, IntervalMinutes = intervalMinutes, Enabled = enabled };

    private static string ChatJson(string content = "OK", string role = "assistant", string model = "model-a")
        => "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"model\":\"" + model +
           "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"" + role + "\",\"content\":\"" + content +
           "\"},\"finish_reason\":\"stop\"}]}";

    private static string[] IdentityMembers() => typeof(ConnectionTestResult).GetProperties()
        .Where(p => p.Name.Contains("Inferred", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Verdict", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Family", StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Name)
        .ToArray();

    // C01 -------------------------------------------------------------------
    // Local validation never touches the network, and an invalid local state is never reported as success.
    public static async Task C01_ValidateLocalOnly()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionValidationResult ok = await service.ValidateConnectionAsync(profile.Id, "model-a");
        Check.True(ok.Passed, "valid local config passes local validation");
        Check.Equal("LocalValidationPassed", ok.Code, "success code is local-only");
        Check.Contains(ok.Message, "尚未联网", "success message states no network happened yet");
        Check.Equal(0, handler.Count, "local validation sends no request");

        // A 256-character identifier is the documented boundary and must pass without redaction.
        string boundary = new('a', 256);
        ConnectionValidationResult atLimit = await service.ValidateConnectionAsync(profile.Id, boundary);
        Check.True(atLimit.Passed, "256-char model id accepted at the boundary");

        var rejected = new (string Label, string ModelId, string Code)[]
        {
            ("empty", "", "InvalidModel"),
            ("whitespace only", "   ", "InvalidModel"),
            ("leading space", " model-a", "InvalidModel"),
            ("trailing space", "model-a ", "InvalidModel"),
            ("control char", "model\ta", "InvalidModel"),
            ("257 chars", new string('b', 257), "InvalidModel"),
            ("current secret as model", Program.Key1, "SecretInModel"),
        };
        foreach ((string label, string modelId, string code) in rejected)
        {
            ConnectionValidationResult result = await service.ValidateConnectionAsync(profile.Id, modelId);
            Check.False(result.Passed, "local validation rejects " + label);
            Check.Equal(code, result.Code, "local validation code for " + label);
            Check.True(result.Message.Length > 0, "rejection message for " + label);
        }

        ConnectionValidationResult missing = await service.ValidateConnectionAsync(Guid.NewGuid(), "model-a");
        Check.False(missing.Passed, "unknown profile is not a pass");
        Check.Equal("ProfileNotFound", missing.Code, "unknown profile code");

        // A relay address that is not a plain public HTTPS base can never become a connection-test target.
        foreach (string badUrl in new[]
                 {
                     "http://relay.example.com/v1", "relay.example.com/v1", "",
                     "https://user:pass@relay.example.com/v1", "https://relay.example.com/v1?x=1",
                     "https://relay.example.com/v1#frag", "https://localhost/v1", "https://127.0.0.1/v1",
                     "https://10.0.0.5/v1", "https://192.168.1.9/v1", "https://relay.local/v1"
                 })
        {
            await Check.ThrowsClientAsync(
                () => service.SaveProfileAsync(Fixtures.NewProfile("Bad", badUrl), Program.Key1),
                "InvalidBaseUrl", "local validation refuses base url '" + badUrl + "'");
        }

        Check.Equal(0, handler.Count, "no request for any local validation outcome");
    }

    // C02 -------------------------------------------------------------------
    // Exactly one POST to {relay base}/chat/completions, Bearer target key, synthetic prompt, no BazaarLink.
    public static async Task C02_TestConnectionHappyPath()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, ChatJson());
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));

        Check.Equal(1, handler.Count, "exactly one request");
        RecordedRequest request = handler.Requests[0];
        Check.Equal("POST", request.Method, "method POST");
        Check.Equal(ChatUrl, request.Uri.AbsoluteUri, "recipient is relay base + /chat/completions");
        Check.Equal("relay.example.com", request.Uri.Host, "relay host, not a service host");
        Check.False(request.Uri.Host.Contains("bazaarlink", StringComparison.OrdinalIgnoreCase), "never the BazaarLink origin");
        Check.NotContains(request.Body, "bazaarlink", "body carries no BazaarLink reference");
        Check.Contains(request.Headers["Authorization"].Single(), "Bearer " + Program.Key1, "Bearer target key");
        Check.False(request.Headers.ContainsKey("X-Api-Key"), "key only in Authorization header");
        Check.NotContains(request.Body, Program.Key1, "key is never placed in the body");
        Check.Contains(request.Headers["Content-Type"].Single(), "application/json", "request declares a JSON body");
        Check.Contains(string.Join(",", request.Headers["Accept"]), "application/json", "JSON fallback accepted");
        Check.Contains(string.Join(",", request.Headers["Accept"]), "text/event-stream", "SSE accepted");

        using var doc = JsonDocument.Parse(request.Body);
        JsonElement root = doc.RootElement;
        Check.Equal("model-a", root.GetProperty("model").GetString(), "body model");
        JsonElement messages = root.GetProperty("messages");
        Check.Equal(1, messages.GetArrayLength(), "single synthetic prompt message");
        Check.Equal("user", messages[0].GetProperty("role").GetString(), "prompt role");
        Check.Contains(messages[0].GetProperty("content").GetString()!, "FIRST=", "reading check prompt");
        Check.Equal(64, root.GetProperty("max_tokens").GetInt32(), "room for three markers");
        Check.True(root.GetProperty("stream").GetBoolean(), "stream true");
        Check.False(root.TryGetProperty("apiKey", out _), "no apiKey field in the relay body");

        Check.True(result.ApiReached, "API reached");
        Check.True(result.ModelResponded, "model responded");
        Check.True(result.LocalValidationPassed, "local validation recorded");
        Check.Equal(ConnectionTestStage.ModelResponded, result.Stage, "stage ModelResponded");
        Check.Equal("ModelResponded", result.Code, "code ModelResponded");
        Check.Equal<int?>(200, result.HttpStatusCode, "http status captured");
        Check.Equal(profile.Id, result.ProfileId, "result bound to profile");
        Check.Equal(profile.KeyRevision, result.KeyRevision, "result bound to key revision");
        Check.Equal(ChatUrl, ConnectionTestEndpoints.Recipient(result.BaseUrl), "result records the confirmed target");
        Check.NotNull(result.CompletedAt, "completion timestamp set");
        Check.Equal(0, IdentityMembers().Length,
            "connection summary has no identity/verdict fields; got " + string.Join(",", IdentityMembers()));
        Check.Contains(result.Message, "不证明", "message disclaims identity/capability proof");
    }

    // C03 -------------------------------------------------------------------
    // Any mismatched consent must send zero requests.
    public static async Task C03_ConsentGating()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        OutboundConsent good = Consent(profile, "model-a");

        var bad = new (string Label, OutboundConsent? Consent)[]
        {
            ("null", null),
            ("wrong profile", good with { ProfileId = Guid.NewGuid() }),
            ("wrong revision", good with { KeyRevision = profile.KeyRevision + 1 }),
            ("wrong base", good with { BaseUrl = "https://other.example.com/v1" }),
            ("wrong purpose", good with { Purpose = OutboundPurpose.ModelList }),
            ("wrong purpose probe", good with { Purpose = OutboundPurpose.Probe }),
            ("wrong recipient (models url)", good with { Recipient = profile.BaseUrl + "/models" }),
            ("wrong recipient (bazaarlink)", good with { Recipient = Fixtures.ServiceRunUrl }),
            ("null model", good with { ModelId = null }),
            ("wrong model", good with { ModelId = "model-b" }),
            ("model case differs", good with { ModelId = "MODEL-A" }),
            ("expired", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }),
            ("future", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(5) }),
        };
        foreach ((string label, OutboundConsent? consent) in bad)
        {
            await Check.ThrowsClientAsync(
                () => service.TestConnectionAsync(profile.Id, "model-a", consent!),
                "ConsentRequired", "test connection rejects " + label);
        }

        Check.Equal(0, handler.Count, "no request for any invalid consent");
        Check.Null(await service.GetLastConnectionResultAsync(profile.Id), "invalid consent stores no result");
    }

    // C04 -------------------------------------------------------------------
    // A 200 response is only a model response when choices[].message.content is a nonempty string.
    public static async Task C04_NonModelResponses()
    {
        var cases = new (string Label, Action<FakeHttpHandler> Script, string Code, ConnectionTestStage Stage)[]
        {
            ("arbitrary json", h => h.EnqueueJson(HttpStatusCode.OK, "{\"foo\":\"bar\"}"), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("html page", h => h.EnqueueRaw(HttpStatusCode.OK, "text/html", "<html>sign in</html>"), "UnexpectedContent", ConnectionTestStage.Unknown),
            ("plain text page", h => h.EnqueueRaw(HttpStatusCode.OK, "text/plain", "OK"), "UnexpectedContent", ConnectionTestStage.Unknown),
            ("empty choices", h => h.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[]}"), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("error object", h => h.EnqueueJson(HttpStatusCode.OK, "{\"error\":{\"message\":\"quota exceeded\"}}"), "ApiError", ConnectionTestStage.Failed),
            ("empty content", h => h.EnqueueJson(HttpStatusCode.OK, ChatJson(content: "")), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("whitespace content", h => h.EnqueueJson(HttpStatusCode.OK, ChatJson(content: "   ")), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("non-string content", h => h.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\"}]}}]}"), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("wrong role", h => h.EnqueueJson(HttpStatusCode.OK, ChatJson(role: "user")), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("no message object", h => h.EnqueueJson(HttpStatusCode.OK, "{\"choices\":[{\"index\":0,\"text\":\"OK\"}]}"), "PossibleSuccess", ConnectionTestStage.PossibleSuccess),
            ("malformed json", h => h.EnqueueRaw(HttpStatusCode.OK, "application/json", "{broken"), "MalformedJson", ConnectionTestStage.Unknown),
        };

        foreach ((string label, Action<FakeHttpHandler> script, string code, ConnectionTestStage stage) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            script(handler);
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

            ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));

            Check.False(result.ModelResponded, label + ": must not claim a model response");
            Check.Equal(code, result.Code, label + ": code");
            Check.Equal(stage, result.Stage, label + ": stage");
            Check.True(result.ApiReached, label + ": API was reached (a 200 arrived)");
            Check.Equal(1, handler.Count, label + ": exactly one request, no retry or protocol switch");
            Check.True(result.Message.Length > 0, label + ": message is meaningful");
            Check.NotEqual(ConnectionTestStage.ModelResponded, result.Stage, label + ": never ModelResponded");
        }
    }

    // C05 -------------------------------------------------------------------
    // The relay may answer with an alias in "model"; a valid chat message still counts, without identity claims.
    public static async Task C05_ModelAliasNoIdentityClaim()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, ChatJson(model: "totally-different-alias-2024-01-01"));
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));

        Check.True(result.ModelResponded, "alias in the model field still yields a model response");
        Check.Equal("ModelResponded", result.Code, "code ModelResponded");
        Check.Equal(1, handler.Count, "one request only");
        Check.Equal(0, IdentityMembers().Length, "no inferred-model/identity member on the summary");
        Check.NotContains(JsonSerializer.Serialize(result), "totally-different-alias-2024-01-01",
            "the relay's model alias is not retained");
        Check.Contains(result.Message, "不证明", "explicitly not a proof of true identity");
        Check.NotContains(result.Message, "身份匹配", "no identity match claim in the message");
        Check.NotContains(result.Message, "模型匹配", "no model alias match claim in the message");
        Check.Equal(ConnectionContextCheck.Mismatch, result.ContextCheck, "OK is a reading mismatch regardless of returned model alias");

        ConnectionTestResult? stored = await service.GetLastConnectionResultAsync(profile.Id);
        Check.NotNull(stored, "summary persisted");
        Check.NotContains(JsonSerializer.Serialize(stored), "totally-different-alias-2024-01-01",
            "persisted summary keeps no relay model alias");
    }

    // C06 -------------------------------------------------------------------
    // HTTP failures surface a meaningful code and stage, and are never retried.
    public static async Task C06_HttpErrorCodes()
    {
        var cases = new (string Label, HttpStatusCode Status, double? RetrySeconds, string Code, ConnectionTestStage Stage, string Body)[]
        {
            ("401", HttpStatusCode.Unauthorized, null, "Http401", ConnectionTestStage.Failed, ""),
            ("403", HttpStatusCode.Forbidden, null, "Http403", ConnectionTestStage.Failed, ""),
            ("404", HttpStatusCode.NotFound, null, "Http404", ConnectionTestStage.Failed, ""),
            ("429", (HttpStatusCode)429, 42, "Http429", ConnectionTestStage.Failed, ""),
            ("400 unsupported max_tokens", HttpStatusCode.BadRequest, null, "Http400", ConnectionTestStage.Failed,
                "{\"error\":{\"message\":\"unsupported parameter: max_tokens\"}}"),
            ("503", HttpStatusCode.ServiceUnavailable, null, "Http503", ConnectionTestStage.Unknown, ""),
        };

        foreach ((string label, HttpStatusCode status, double? retrySeconds, string code, ConnectionTestStage stage, string body) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (retrySeconds is { } seconds)
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return response;
            });
            using var service = New(tmp.Path, handler);
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

            ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));

            Check.Equal(code, result.Code, label + ": code");
            Check.Equal(stage, result.Stage, label + ": stage");
            Check.True(result.ApiReached, label + ": API reached");
            Check.False(result.ModelResponded, label + ": not a model response");
            Check.Equal<int?>((int)status, result.HttpStatusCode, label + ": http status recorded");
            Check.Equal(1, handler.Count, label + ": one request, no automatic retry");
            Check.True(result.Message.Length > 5, label + ": message is meaningful");

            if (retrySeconds is { } expected)
            {
                Check.NotNull(result.RetryNotBefore, label + ": Retry-After deadline recorded");
                TimeSpan remaining = result.RetryNotBefore!.Value - DateTimeOffset.UtcNow;
                Check.True(remaining > TimeSpan.FromSeconds(30) && remaining <= TimeSpan.FromSeconds(expected),
                    label + ": deadline honours Retry-After, remaining " + remaining.TotalSeconds.ToString("F1") + "s");
            }
            else
            {
                Check.Null(result.RetryNotBefore, label + ": no Retry-After deadline invented");
            }

            if (label.StartsWith("400", StringComparison.Ordinal))
                Check.Contains(result.Message, "不接受", "400 explains the request shape was not accepted");
            if (label == "404")
                Check.Contains(result.Message, "不会自动追加", "404 explains no /v1 guessing");
        }
    }

    // C07 -------------------------------------------------------------------
    // A 429 Retry-After blocks the next attempt locally: zero requests until the deadline.
    public static async Task C07_RetryAfterBlocksNext()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueStatus((HttpStatusCode)429,
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionTestResult limited = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));
        Check.Equal("Http429", limited.Code, "first attempt is rate limited");
        Check.Equal(1, handler.Count, "one request so far");

        ClientOperationException blocked = await Check.ThrowsClientAsync(
            () => service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a")),
            "ConnectionRateLimited", "second attempt waits for Retry-After");
        Check.NotNull(blocked.RetryAfter, "blocked attempt exposes remaining wait");
        Check.True(blocked.RetryAfter!.Value > TimeSpan.FromSeconds(30), "remaining wait is close to the advertised Retry-After");
        Check.Equal(1, handler.Count, "no request sent before Retry-After");

        // Scheduled path also refuses, and still sends nothing.
        await Check.ThrowsClientAsync(() => service.TestScheduledConnectionAsync(profile.Id),
            "ScheduleNotAuthorized", "scheduled test is not authorized without an enabled session");
        Check.Equal(1, handler.Count, "scheduled refusal sends no request");
    }

    // C08 -------------------------------------------------------------------
    // Timeout or cancellation is an unknown outcome: exactly one attempt, no automatic retry.
    public static async Task C08_TimeoutSingleAttempt()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueException(new TaskCanceledException("simulated client timeout"));
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionTestResult result = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));

        Check.Equal("CancelledOrTimeout", result.Code, "timeout code");
        Check.Equal(ConnectionTestStage.Unknown, result.Stage, "timeout outcome is unknown, not failed");
        Check.False(result.ModelResponded, "timeout is not a model response");
        Check.False(result.ApiReached, "no API acknowledgement was received");
        Check.Contains(result.Message, "不会自动重试", "message states no automatic retry");
        Check.Equal(1, handler.Count, "exactly one attempt, no resubmission");

        // Waiting for the store to settle must not add a request.
        await Task.Delay(120);
        Check.Equal(1, handler.Count, "no background retry after the timeout");

        ConnectionTestResult? stored = await service.GetLastConnectionResultAsync(profile.Id);
        Check.NotNull(stored, "unknown outcome persisted as a summary");
        Check.Equal(ConnectionTestStage.Unknown, stored!.Stage, "persisted stage stays Unknown");

        // Cancelling while the body is still streaming must abort promptly and report an unknown outcome.
        using var tmp2 = new TempDir();
        var stall = new FakeHttpHandler();
        stall.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowBodyContent() });
        using var cancelling = New(tmp2.Path, stall);
        RelayProfile profile2 = await cancelling.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ConnectionTestResult cancelled = await cancelling.TestConnectionAsync(
            profile2.Id, "model-a", Consent(profile2, "model-a"), cts.Token);
        sw.Stop();
        Check.Equal("CancelledOrTimeout", cancelled.Code, "cancellation code");
        Check.Equal(ConnectionTestStage.Unknown, cancelled.Stage, "cancellation outcome is unknown");
        Check.False(cancelled.ModelResponded, "cancelled call is not a model response");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(15), "cancellation was prompt, took " + sw.ElapsedMilliseconds + " ms");
        Check.Equal(1, stall.Count, "cancelled call still made exactly one attempt");
    }

    // C09 -------------------------------------------------------------------
    // The summary is a bounded, protected record: no echoed secret, no raw model output, no probe history.
    public static async Task C09_SummaryRedactionAndIsolation()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        var protector = new ReversibleTestProtector();
        using var service = New(tmp.Path, handler, protector);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        handler.EnqueueRaw(HttpStatusCode.BadRequest, "application/json",
            "{\"error\":{\"message\":\"invalid key " + Program.Key1 + "\"}}");
        ConnectionTestResult failed = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));
        Check.Equal("Http400", failed.Code, "error status code");
        Check.NotContains(failed.Message, Program.Key1, "error message never echoes the key");
        Check.NotContains(JsonSerializer.Serialize(failed), Program.Key1, "result object never echoes the key");

        const string ModelOutput = "MODEL-OUTPUT-MARKER-9f3a";
        handler.EnqueueJson(HttpStatusCode.OK, ChatJson(content: ModelOutput));
        ConnectionTestResult ok = await service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));
        Check.True(ok.ModelResponded, "model responded");
        Check.NotContains(JsonSerializer.Serialize(ok), ModelOutput, "summary never stores the raw model output");

        string store = Fixtures.ReadAllStoreText(tmp.Path);
        Check.NotContains(store, Program.Key1, "store never holds the plaintext key");
        Check.NotContains(store, ModelOutput, "store never holds the raw model output");
        Check.NotContains(store, "chatcmpl-1", "store never holds the raw response id");

        Check.Equal(0, (await service.GetHistoryAsync()).Count, "connection tests do not create probe history");

        ConnectionTestResult? stored = await service.GetLastConnectionResultAsync(profile.Id);
        Check.NotNull(stored, "summary persisted");
        Check.Equal("ModelResponded", stored!.Code, "persisted code");
        Check.Equal("model-a", stored.ModelId, "persisted model id");
        Check.Equal(profile.BaseUrl, stored.BaseUrl, "persisted base url");

        // Reopen with the same protector: the summary lives in the protected vault and survives restart.
        using var reopened = New(tmp.Path, handler, protector);
        ConnectionTestResult? afterRestart = await reopened.GetLastConnectionResultAsync(profile.Id);
        Check.NotNull(afterRestart, "summary survives restart via the protected vault");
        Check.Equal("ModelResponded", afterRestart!.Code, "summary code survives restart");
        Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Program.Key1, "store still holds no plaintext key");
    }

    // C10 -------------------------------------------------------------------
    // Single-flight: a concurrent manual test is refused without a second request.
    public static async Task C10_SingleFlight()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        handler.Enqueue(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatJson(), Encoding.UTF8, "application/json")
            };
        });
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        Task<ConnectionTestResult> first = service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a"));
        Check.True(started.Wait(TimeSpan.FromSeconds(15)), "first request was dispatched");
        Check.Equal(1, handler.Count, "exactly one in-flight request");

        await Check.ThrowsClientAsync(
            () => service.TestConnectionAsync(profile.Id, "model-a", Consent(profile, "model-a")),
            "ConnectionBusy", "concurrent manual test is refused");
        Check.Equal(1, handler.Count, "refused concurrent call sent no request");

        release.Set();
        ConnectionTestResult result = await first;
        Check.Equal("ModelResponded", result.Code, "the in-flight request completes normally");
        Check.Equal(1, handler.Count, "still exactly one request in total");
    }

    // C11 -------------------------------------------------------------------
    // Defaults are off with a 60-minute cadence; the accepted interval range is 1..1440.
    public static async Task C11_SettingsDefaultsAndIntervalBounds()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        ConnectionTestSettings defaults = await service.GetConnectionSettingsAsync(profile.Id);
        Check.Equal(profile.Id, defaults.ProfileId, "settings bound to profile");
        Check.False(defaults.Enabled, "scheduling is off by default");
        Check.Equal(60, defaults.IntervalMinutes, "default interval is 60 minutes");
        Check.Equal("", defaults.ModelId, "no scheduled model by default");
        Check.Null(defaults.ConsentAt, "no consent timestamp by default");

        foreach (int invalid in new[] { 0, -5, 1441, 99999 })
            await Check.ThrowsClientAsync(
                () => service.SaveConnectionSettingsAsync(Settings(profile, "model-a", invalid, enabled: false)),
                "InvalidInterval", "interval " + invalid + " rejected");

        foreach (int valid in new[] { 1, 60, 1440 })
        {
            ConnectionTestSettings saved = await service.SaveConnectionSettingsAsync(Settings(profile, "model-a", valid, enabled: false));
            Check.Equal(valid, saved.IntervalMinutes, "interval " + valid + " accepted");
            ConnectionTestSettings roundtrip = await service.GetConnectionSettingsAsync(profile.Id);
            Check.Equal(valid, roundtrip.IntervalMinutes, "interval " + valid + " round-trips");
            Check.Equal("model-a", roundtrip.ModelId, "model id round-trips");
            Check.False(roundtrip.Enabled, "saving while off keeps it off");
        }

        await Check.ThrowsClientAsync(
            () => service.SaveConnectionSettingsAsync(Settings(profile, "model-a", 60, enabled: true)),
            "ConsentRequired", "enabling without consent is refused");
        await Check.ThrowsClientAsync(
            () => service.SaveConnectionSettingsAsync(Settings(profile, "", 60, enabled: true), Schedule(profile, "", 60)),
            "InvalidModel", "enabling with an empty model is refused");

        Check.Equal(0, handler.Count, "settings operations never send a request");
    }

    // C12 -------------------------------------------------------------------
    // Enabling needs fresh consent for the exact target and interval; disabling needs none.
    public static async Task C12_EnableConsentAndRoundtrip()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        const int interval = 5;

        var bad = new (string Label, ConnectionScheduleConsent? Consent)[]
        {
            ("no consent", null),
            ("interval mismatch", new ConnectionScheduleConsent(Consent(profile, "model-a"), interval + 1)),
            ("interval zero", new ConnectionScheduleConsent(Consent(profile, "model-a"), 0)),
            ("wrong purpose", new ConnectionScheduleConsent(Consent(profile, "model-a") with { Purpose = OutboundPurpose.ModelList }, interval)),
            ("probe purpose", new ConnectionScheduleConsent(Consent(profile, "model-a") with { Purpose = OutboundPurpose.Probe }, interval)),
            ("wrong recipient", new ConnectionScheduleConsent(Consent(profile, "model-a") with { Recipient = profile.BaseUrl + "/models" }, interval)),
            ("null model", new ConnectionScheduleConsent(Consent(profile, "model-a") with { ModelId = null }, interval)),
            ("other model", new ConnectionScheduleConsent(Consent(profile, "model-b"), interval)),
            ("stale consent", new ConnectionScheduleConsent(Consent(profile, "model-a", DateTimeOffset.UtcNow.AddMinutes(-30)), interval)),
            ("future consent", new ConnectionScheduleConsent(Consent(profile, "model-a", DateTimeOffset.UtcNow.AddMinutes(5)), interval)),
            ("wrong profile", new ConnectionScheduleConsent(Consent(profile, "model-a") with { ProfileId = Guid.NewGuid() }, interval)),
        };
        foreach ((string label, ConnectionScheduleConsent? consent) in bad)
        {
            await Check.ThrowsClientAsync(
                () => service.SaveConnectionSettingsAsync(Settings(profile, "model-a", interval, enabled: true), consent),
                "ConsentRequired", "enabling rejects " + label);
            ConnectionTestSettings stillOff = await service.GetConnectionSettingsAsync(profile.Id);
            Check.False(stillOff.Enabled, "still off after " + label);
        }

        ConnectionTestSettings enabled = await service.SaveConnectionSettingsAsync(
            Settings(profile, "model-a", interval, enabled: true), Schedule(profile, "model-a", interval));
        Check.True(enabled.Enabled, "valid consent enables scheduling");
        Check.NotNull(enabled.ConsentAt, "consent timestamp recorded");
        Check.Equal(interval, enabled.IntervalMinutes, "interval recorded");
        Check.Equal(profile.KeyRevision, enabled.KeyRevision, "key revision bound");

        ConnectionTestSettings live = await service.GetConnectionSettingsAsync(profile.Id);
        Check.True(live.Enabled, "enabled state visible in this session");
        Check.Equal("model-a", live.ModelId, "scheduled model round-trips");
        Check.Equal(interval, live.IntervalMinutes, "scheduled interval round-trips");

        ConnectionTestSettings disabled = await service.SaveConnectionSettingsAsync(
            Settings(profile, "model-a", interval, enabled: false), consent: null);
        Check.False(disabled.Enabled, "disabling needs no consent");
        Check.Null(disabled.ConsentAt, "disabling clears the consent timestamp");

        Check.Equal(0, handler.Count, "settings operations never send a request");
    }

    // C13 -------------------------------------------------------------------
    // Enabled is session-only: a new service starts with scheduling off but keeps the stored settings.
    public static async Task C13_RestartTurnsSchedulingOff()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        Guid profileId;
        using (var service = New(tmp.Path, handler))
        {
            RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
            profileId = profile.Id;
            ConnectionTestSettings enabled = await service.SaveConnectionSettingsAsync(
                Settings(profile, "model-a", 7, enabled: true), Schedule(profile, "model-a", 7));
            Check.True(enabled.Enabled, "enabled before restart");
        }

        using var restarted = New(tmp.Path, handler);
        ConnectionTestSettings afterRestart = await restarted.GetConnectionSettingsAsync(profileId);
        Check.False(afterRestart.Enabled, "scheduling is off after restart");
        Check.Null(afterRestart.ConsentAt, "no consent survives restart");
        Check.Equal("model-a", afterRestart.ModelId, "stored model id is retained");
        Check.Equal(7, afterRestart.IntervalMinutes, "stored interval is retained");

        await Check.ThrowsClientAsync(() => restarted.TestScheduledConnectionAsync(profileId),
            "ScheduleNotAuthorized", "scheduled test refused after restart");
        Check.Equal(0, handler.Count, "no request after restart");
    }

    // C14 -------------------------------------------------------------------
    // A scheduled test right after enabling is not due and sends nothing; the wait equals the cadence.
    public static async Task C14_ScheduledNotDue()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        const int interval = 5;
        await service.SaveConnectionSettingsAsync(Settings(profile, "model-a", interval, enabled: true),
            Schedule(profile, "model-a", interval));

        ClientOperationException notDue = await Check.ThrowsClientAsync(
            () => service.TestScheduledConnectionAsync(profile.Id),
            "ConnectionNotDue", "immediately after enabling the test is not due");
        Check.Equal(0, handler.Count, "not-due sends no request");
        Check.NotNull(notDue.RetryAfter, "not-due reports the remaining wait");
        TimeSpan remaining = notDue.RetryAfter!.Value;
        Check.True(remaining > TimeSpan.FromMinutes(interval - 1) && remaining <= TimeSpan.FromMinutes(interval),
            "remaining wait equals the configured cadence, got " + remaining.TotalSeconds.ToString("F1") + "s");

        // Repeating the call must not drift into a request.
        for (int i = 0; i < 3; i++)
            await Check.ThrowsClientAsync(() => service.TestScheduledConnectionAsync(profile.Id),
                "ConnectionNotDue", "repeated not-due check " + i);
        Check.Equal(0, handler.Count, "repeated not-due checks send nothing");
    }

    // C15 -------------------------------------------------------------------
    // Turning off, or changing key/base/model, revokes the schedule: zero requests afterwards.
    // Also: revoking one profile must not touch a different profile's live schedule.
    public static async Task C15_ScheduleRevocation()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);
        const int interval = 5;

        async Task EnableAsync(RelayProfile current)
            => await service.SaveConnectionSettingsAsync(Settings(current, "model-a", interval, enabled: true),
                Schedule(current, "model-a", interval));

        async Task AssertNotDueAsync(RelayProfile current, string label)
        {
            await Check.ThrowsClientAsync(() => service.TestScheduledConnectionAsync(current.Id),
                "ConnectionNotDue", label + ": authorized and merely not due");
            Check.Equal(0, handler.Count, label + ": still no request");
        }

        async Task AssertRevokedAsync(RelayProfile current, string label)
        {
            await Check.ThrowsClientAsync(() => service.TestScheduledConnectionAsync(current.Id),
                "ScheduleNotAuthorized", label + ": schedule revoked");
            Check.Equal(0, handler.Count, label + ": no request after revocation");
        }

        // 1. Enabled, then explicitly turned off.
        await EnableAsync(profile);
        await AssertNotDueAsync(profile, "enabled baseline");
        await service.SaveConnectionSettingsAsync(Settings(profile, "model-a", interval, enabled: false), consent: null);
        await AssertRevokedAsync(profile, "turning off");

        // 2. Key rotation revokes.
        await EnableAsync(profile);
        await AssertNotDueAsync(profile, "re-enabled after off");
        RelayProfile rotated = await service.SaveProfileAsync(profile with { }, Program.Key2);
        Check.True(rotated.KeyRevision > profile.KeyRevision, "key rotation bumped the revision");
        await AssertRevokedAsync(profile, "key revision change");

        // 3. Base URL change revokes.
        await EnableAsync(rotated);
        await AssertNotDueAsync(profile, "re-enabled after rotation");
        RelayProfile moved = await service.SaveProfileAsync(rotated with { BaseUrl = "https://relay2.example.com/v1" }, null);
        Check.Equal("https://relay2.example.com/v1", moved.BaseUrl, "base url changed");
        await AssertRevokedAsync(profile, "base url change");

        // 4. Scheduled model change revokes.
        await EnableAsync(moved);
        await AssertNotDueAsync(profile, "re-enabled after base change");
        await service.SaveConnectionSettingsAsync(Settings(moved, "model-b", interval, enabled: false), consent: null);
        await AssertRevokedAsync(profile, "model change");

        // 5. Cross-profile: one service session holds one approved scheduled target (documented contract).
        RelayProfile other = await service.SaveProfileAsync(
            Fixtures.NewProfile("Other", "https://relay-other.example.com/v1"), Program.Key1);
        await EnableAsync(moved);
        await EnableAsync(other);
        await AssertNotDueAsync(other, "second profile enabled");

        // 5a. A stale/late "off" for a DIFFERENT profile must not revoke the newly approved scope.
        await service.SaveConnectionSettingsAsync(Settings(moved, "model-a", interval, enabled: false), consent: null);
        await AssertNotDueAsync(other, "old off for another profile does not revoke the new scope");

        // 5b. A scheduled attempt for a profile that is NOT the approved target must be refused, but the
        //     refusal must only affect that profile. Refusing A must not revoke B's live authorization.
        //     GetConnectionSettingsAsync and SaveConnectionSettingsAsync both guard their session clearing
        //     with "ProfileId == profile.Id"; the scheduled refusal path clears unconditionally.
        await AssertRevokedAsync(moved, "first profile no longer the approved session target");
        await AssertNotDueAsync(other, "second profile still authorized after probing the unapproved first profile");

        // And the unaffected profile can still be turned off explicitly.
        await service.SaveConnectionSettingsAsync(Settings(other, "model-a", interval, enabled: false), consent: null);
        await AssertRevokedAsync(other, "second profile turned off explicitly");

        Check.Equal(0, handler.Count, "no revocation path ever sent a request");
    }

    // --connection-due-wait --------------------------------------------------
    // Real cadence check, run as a separate opt-in mode because the minimum interval is 1 minute and the
    // in-suite watchdog is 60s. Still FakeHttpHandler only: the "network" is a scripted in-memory response.
    // Proves that after the configured interval actually elapses, the scheduled call dispatches exactly once.
    public static async Task<int> RunDueWaitModeAsync()
    {
        const int interval = 1;
        var log = new List<string>();
        void Say(string line)
        {
            log.Add(line);
            Console.WriteLine(line);
            Console.Out.Flush();
        }

        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        // One scripted success is queued up front. A not-due call sends nothing, so this stays unconsumed
        // until the real interval elapses; any accidental early dispatch would show up in the count checks.
        handler.EnqueueJson(HttpStatusCode.OK, ChatJson());
        using var service = New(tmp.Path, handler);
        RelayProfile profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Program.Key1);

        Say("DUE_MODE=connection-due-wait (FakeHttpHandler only, no external network)");
        Say("INTERVAL_MINUTES=" + interval);

        var enabledAt = System.Diagnostics.Stopwatch.StartNew();
        ConnectionTestSettings enabled = await service.SaveConnectionSettingsAsync(
            Settings(profile, "model-a", interval, enabled: true), Schedule(profile, "model-a", interval));
        if (!enabled.Enabled) { Say("FAIL enabling scheduling did not take effect"); return 1; }
        Say("ENABLED_AT=" + DateTimeOffset.UtcNow.ToString("O"));

        // Not due immediately after enabling: this must stay a local refusal with no request.
        try
        {
            await service.TestScheduledConnectionAsync(profile.Id);
            Say("FAIL immediate scheduled call should not have dispatched");
            return 1;
        }
        catch (ClientOperationException ex) when (ex.Code == "ConnectionNotDue")
        {
            Say("IMMEDIATE_NOT_DUE_OK remaining=" + (ex.RetryAfter?.TotalSeconds.ToString("F1") ?? "?") + "s");
        }
        if (handler.Count != 0) { Say("FAIL immediate call sent " + handler.Count + " request(s)"); return 1; }

        // Wait for the real minute to elapse (interval + 15s margin), then attempt the scheduled dispatch.
        var waitDeadline = TimeSpan.FromSeconds(interval * 60 + 15);
        var sinceEnable = System.Diagnostics.Stopwatch.StartNew();
        while (sinceEnable.Elapsed < waitDeadline)
        {
            try
            {
                await service.TestScheduledConnectionAsync(profile.Id);
                await Task.Delay(50);
                if (handler.Count > 0) break;
            }
            catch (ClientOperationException ex) when (ex.Code == "ConnectionNotDue")
            {
                await Task.Delay(500);
                continue;
            }
        }
        sinceEnable.Stop();

        if (handler.Count == 0)
        {
            TimeSpan waited = sinceEnable.Elapsed;
            enabledAt.Stop();
            Say("NOT_DISPATCHED_AFTER=" + waited.TotalSeconds.ToString("F1") + "s");
            Say($"DUE_WAIT=FAILED reason=no request after {waited.TotalSeconds:F1}s (no public clock injection available; reported, not asserted as pass)");
            return 1;
        }

        Say("DISPATCHED_AFTER=" + sinceEnable.Elapsed.TotalSeconds.ToString("F1") + "s");
        if (sinceEnable.Elapsed < TimeSpan.FromSeconds(interval * 60 - 2))
        {
            Say("FAIL dispatched before the configured interval elapsed");
            return 1;
        }

        ConnectionTestResult? due = await service.GetLastConnectionResultAsync(profile.Id);
        if (due is null) { Say("FAIL no stored result after due dispatch"); return 1; }
        Say("RESULT code=" + due.Code + " stage=" + due.Stage + " responded=" + due.ModelResponded
            + " http=" + (due.HttpStatusCode?.ToString() ?? "-"));

        bool ok = due.Code == "ModelResponded" && due.ModelResponded
            && handler.Count == 1
            && handler.Requests[0].Method == "POST"
            && handler.Requests[0].Uri.AbsoluteUri == ChatUrl
            && string.Equals(handler.Requests[0].Headers["Authorization"].Single(), "Bearer " + Program.Key1, StringComparison.Ordinal)
            && handler.Requests[0].Uri.Host.Equals("relay.example.com", StringComparison.OrdinalIgnoreCase)
            && !handler.Requests[0].Body.Contains("bazaarlink", StringComparison.OrdinalIgnoreCase);
        Say("REQUEST method=" + handler.Requests[0].Method + " url=" + handler.Requests[0].Uri.AbsoluteUri
            + " count=" + handler.Count);
        if (!ok) { Say("DUE_WAIT=FAILED reason=dispatch did not match the confirmed relay chat/completions contract"); return 1; }

        // After a real dispatch the next scheduled call becomes not-due again, with no extra request.
        try
        {
            await service.TestScheduledConnectionAsync(profile.Id);
            Say("DUE_WAIT=FAILED reason=second scheduled call dispatched immediately");
            return 1;
        }
        catch (ClientOperationException ex) when (ex.Code == "ConnectionNotDue")
        {
            Say("NEXT_NOT_DUE_OK remaining=" + (ex.RetryAfter?.TotalSeconds.ToString("F1") ?? "?") + "s");
        }
        if (handler.Count != 1) { Say("DUE_WAIT=FAILED reason=extra request, count=" + handler.Count); return 1; }

        enabledAt.Stop();
        Say("DUE_WAIT=OK total=" + enabledAt.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }
}
