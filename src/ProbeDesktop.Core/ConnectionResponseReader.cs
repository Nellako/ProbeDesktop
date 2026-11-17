using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProbeDesktop.Core;

/// <summary>Connection-only reader. Never relaxes the shared Probe/model-list JSON contract.</summary>
internal static class ConnectionResponseReader
{
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumEventChars = 128 * 1024;
    private const int MaximumEvents = 4096;
    private const int MaximumLines = 16384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ConnectionTestStage> ReadAsync(HttpResponseMessage response, ApiProtocol protocol, CancellationToken token, ConnectionTextCapture? capture = null)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using JsonDocument json = await OutboundSecurity.ReadJsonAsync(response, token).ConfigureAwait(false);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ConnectionTestStage.Unknown;
            if (IsError(root)) return ConnectionTestStage.Failed;
            if (root.TryGetProperty("html", out _) || Text(root, "content_type") == "text/html") return ConnectionTestStage.Unknown;
            if (protocol == ApiProtocol.OpenAIResponses && Text(root, "status") is "in_progress" or "queued")
                return ConnectionTestStage.Unknown;
            if (IsTruncated(root, protocol)) return ConnectionTestStage.PossibleSuccess;
            capture?.Append(JsonText(root, protocol, capture));
            return HasJsonText(root, protocol) ? ConnectionTestStage.ModelResponded : ConnectionTestStage.PossibleSuccess;
        }
        if (response.Content.Headers.ContentLength > OutboundSecurity.MaximumResponseBytes) throw Limit();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = new byte[8192];
        using MemoryStream line = new();
        StringBuilder data = new();
        string eventName = "";
        int bytes = 0, lines = 0, events = 0;
        bool completed = false, text = false, error = false, incomplete = false, chatAssistant = true;
        bool pendingCr = false, contradictoryStatus = false;
        void Dispatch()
        {
            if (data.Length == 0 && eventName.Length == 0) return;
            if (++events > MaximumEvents) throw Limit();
            string value = data.ToString().TrimEnd('\n');
            if (eventName == "error") error = true;
            if (value == "[DONE]")
            {
                if (protocol == ApiProtocol.ChatCompletions) completed = true;
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
                    JsonElement root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw Malformed();
                    string type = Text(root, "type") ?? eventName;
                    if (IsError(root) || type is "error" or "response.failed" or "response.cancelled") error = true;
                    if (root.TryGetProperty("response", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object && IsError(nested)) error = true;
                    incomplete |= IsTruncated(root, protocol);
                    switch (protocol)
                    {
                        case ApiProtocol.ChatCompletions:
                            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
                            {
                                JsonElement? selected = ChatChoice(choices, capture);
                                if (selected is { } chosen && chosen.TryGetProperty("delta", out var firstDelta) && firstDelta.ValueKind == JsonValueKind.Object && Text(firstDelta, "content") is { } fragment)
                                {
                                    capture?.ObserveSource(chosen.TryGetProperty("index", out _) ? "chat:0" : "chat:unindexed");
                                    capture?.Append(fragment);
                                }
                                foreach (JsonElement choice in choices.EnumerateArray())
                                    if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
                                    {
                                        if (Text(delta, "role") is { } role && role != "assistant") chatAssistant = false;
                                        text |= chatAssistant && !string.IsNullOrWhiteSpace(Text(delta, "content"));
                                    }
                            }
                            break;
                        case ApiProtocol.OpenAIResponses:
                            if (type == "response.output_text.delta")
                            {
                                text |= !string.IsNullOrWhiteSpace(Text(root, "delta"));
                                ObserveResponseSource(root, capture);
                                capture?.Append(Text(root, "delta"));
                            }
                            if (type == "response.incomplete") { completed = true; incomplete = true; }
                            if (type == "response.completed")
                            {
                                completed = true;
                                if (root.TryGetProperty("response", out JsonElement full) && full.ValueKind == JsonValueKind.Object)
                                {
                                    incomplete |= Text(full, "status") == "incomplete";
                                    contradictoryStatus |= Text(full, "status") is "in_progress" or "queued";
                                    text |= HasJsonText(full, protocol);
                                    // A missing output is only a terminal signal; an explicit
                                    // output (including []) is the authoritative snapshot.
                                    if (full.TryGetProperty("output", out _))
                                        capture?.Final(JsonText(full, protocol, capture));
                                }
                            }
                            break;
                        case ApiProtocol.AnthropicMessages:
                            if (type == "message_stop") completed = true;
                            if (type == "content_block_delta" && root.TryGetProperty("delta", out JsonElement block) && block.ValueKind == JsonValueKind.Object && Text(block, "type") == "text_delta")
                            {
                                text |= !string.IsNullOrWhiteSpace(Text(block, "text"));
                                capture?.Append(Text(block, "text"));
                            }
                            if (type == "content_block_start" && root.TryGetProperty("content_block", out JsonElement initial) && initial.ValueKind == JsonValueKind.Object && Text(initial, "type") == "text")
                            {
                                text |= !string.IsNullOrWhiteSpace(Text(initial, "text"));
                                capture?.Append(Text(initial, "text"));
                            }
                            break;
                    }
                }
                catch (JsonException) { throw Malformed(); }
            }
            data.Clear();
            eventName = "";
        }
        void ReadLine()
        {
            if (++lines > MaximumLines) throw Limit();
            string value;
            try { value = StrictUtf8.GetString(line.GetBuffer(), 0, (int)line.Length); }
            catch (DecoderFallbackException) { throw Malformed(); }
            line.SetLength(0);
            if (lines == 1) value = value.TrimStart('\uFEFF');
            if (value.Length == 0) { Dispatch(); return; }
            if (value[0] == ':') return;
            int colon = value.IndexOf(':');
            string field = colon < 0 ? value : value[..colon];
            string content = colon < 0 ? "" : value[(colon + 1)..];
            if (content.StartsWith(' ')) content = content[1..];
            if (field == "event") eventName = content;
            if (field == "data")
            {
                if (data.Length + content.Length + 1 > MaximumEventChars) throw Limit();
                data.Append(content).Append('\n');
            }
        }
        try
        {
            // Read to EOF even after completion: a trailing protocol error wins, and transport failure is unknown.
            while (true)
            {
                int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                bytes += count;
                if (bytes > OutboundSecurity.MaximumResponseBytes) throw Limit();
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[i];
                    if (pendingCr) { pendingCr = false; if (b == 10) continue; }
                    if (b is 10 or 13) { ReadLine(); pendingCr = b == 13; }
                    else
                    {
                        if (line.Length >= MaximumLineBytes) throw Limit();
                        line.WriteByte(b);
                    }
                }
            }
            // SSE dispatch requires a blank line. An unterminated trailing event is not a completion.
            bool partialEvent = line.Length != 0 || data.Length != 0 || eventName.Length != 0;
            token.ThrowIfCancellationRequested();
            return error ? ConnectionTestStage.Failed : partialEvent || !completed || contradictoryStatus ? ConnectionTestStage.Unknown :
                text && !incomplete && (protocol != ApiProtocol.ChatCompletions || chatAssistant) ? ConnectionTestStage.ModelResponded : ConnectionTestStage.PossibleSuccess;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(line.GetBuffer());
        }
    }

    private static bool IsTruncated(JsonElement root, ApiProtocol protocol)
    {
        if (protocol == ApiProtocol.ChatCompletions && root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            return choices.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && Text(c, "finish_reason") is "length" or "content_filter");
        if (protocol == ApiProtocol.AnthropicMessages)
            return Text(root, "stop_reason") == "max_tokens" ||
                (root.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object && Text(delta, "stop_reason") == "max_tokens");
        return protocol == ApiProtocol.OpenAIResponses && Text(root, "status") == "incomplete";
    }

    private static bool IsError(JsonElement root) =>
        (root.TryGetProperty("error", out JsonElement error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) ||
        Text(root, "type") == "error" || Text(root, "status") is "failed" or "cancelled" ||
        (root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False);

    private static string? Text(JsonElement root, string name) => ProbeResponseAdapter.String(root, name);
    private static bool ContentText(JsonElement root, string type)
    {
        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) return false;
        return content.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.Object && Text(p, "type") == type && !string.IsNullOrWhiteSpace(Text(p, "text")));
    }
    private static bool HasJsonText(JsonElement root, ApiProtocol protocol)
    {
        if (protocol == ApiProtocol.ChatCompletions && root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            return choices.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.Object && Text(m, "role") == "assistant" && !string.IsNullOrWhiteSpace(Text(m, "content")));
        if (protocol == ApiProtocol.OpenAIResponses && Text(root, "status") == "completed" && root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
            return output.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.Object && Text(m, "type") == "message" && Text(m, "role") == "assistant" && ContentText(m, "output_text"));
        return protocol == ApiProtocol.AnthropicMessages && Text(root, "type") == "message" && Text(root, "role") == "assistant" && ContentText(root, "text");
    }
    private static JsonElement? ChatChoice(JsonElement choices, ConnectionTextCapture? capture)
    {
        JsonElement? selected = null;
        HashSet<int> indices = [];
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object) { capture?.Invalidate(); continue; }
            if (!choice.TryGetProperty("index", out var index))
            {
                if (choices.GetArrayLength() != 1) capture?.Invalidate();
                else selected = choice;
            }
            else if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int n) || n < 0 || !indices.Add(n))
                capture?.Invalidate();
            else if (n == 0) selected = choice;
        }
        return selected;
    }

    private static void ObserveResponseSource(JsonElement root, ConnectionTextCapture? capture)
    {
        if (capture is null) return;
        string item = "";
        if (root.TryGetProperty("item_id", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) capture.Invalidate();
            else item = id.GetString()!;
        }
        int? Index(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n >= 0) return n;
            capture.Invalidate();
            return null;
        }
        int? output = Index("output_index"), content = Index("content_index");
        capture.ObserveSource($"responses:{output?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}:{item}", content);
    }

    private static string? JsonText(JsonElement root, ApiProtocol protocol, ConnectionTextCapture? capture)
    {
        if (protocol == ApiProtocol.ChatCompletions && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            if (ChatChoice(choices, capture) is { } choice && choice.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object && Text(message, "role") == "assistant")
                return Text(message, "content");
            return null;
        }
        var parts = new List<string>();
        void Content(JsonElement message, string type)
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && Text(part, "type") == type && Text(part, "text") is { } value)
                        parts.Add(value);
        }
        if (protocol == ApiProtocol.OpenAIResponses && root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            int messages = 0;
            foreach (var message in output.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object && Text(message, "type") == "message" && Text(message, "role") == "assistant")
                {
                    if (++messages > 1) { capture?.Invalidate(); break; }
                    Content(message, "output_text");
                }
        }
        else if (protocol == ApiProtocol.AnthropicMessages && Text(root, "type") == "message" && Text(root, "role") == "assistant")
            Content(root, "text");
        return parts.Count == 0 ? null : string.Concat(parts);
    }
    private static ClientOperationException Limit() => new("ResponseTooLarge", "连接响应超过安全读取限制，结果未知。");
    private static ClientOperationException Malformed() => new("MalformedJson", "连接响应格式无效，结果未知。");
}
