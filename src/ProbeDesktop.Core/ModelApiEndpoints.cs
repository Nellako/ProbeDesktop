using System.Net.Http.Headers;

namespace ProbeDesktop.Core;

/// <summary>API roots are used exactly as supplied; no /v1 insertion or protocol fallback.</summary>
public static class ModelApiEndpoints
{
    public static string Models(string baseUrl, ApiProtocol protocol = ApiProtocol.ChatCompletions)
    {
        ValidateProtocol(protocol);
        return new Uri(OutboundSecurity.NormalizeBaseUrl(baseUrl) + "/models").AbsoluteUri;
    }

    public static string Inference(string baseUrl, ApiProtocol protocol)
    {
        ValidateProtocol(protocol);
        string suffix = protocol switch
        {
            ApiProtocol.OpenAIResponses => "/responses",
            ApiProtocol.AnthropicMessages => "/messages",
            _ => "/chat/completions"
        };
        return new Uri(OutboundSecurity.NormalizeBaseUrl(baseUrl) + suffix).AbsoluteUri;
    }

    public static string ProtocolLabel(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.ChatCompletions => "OpenAI Chat Completions",
        ApiProtocol.OpenAIResponses => "OpenAI Responses",
        ApiProtocol.AnthropicMessages => "Anthropic Messages",
        _ => throw new ClientOperationException("UnsupportedProtocol", "不支持此 API 协议，请重新选择。")
    };

    internal static void ValidateProtocol(ApiProtocol protocol) => _ = ProtocolLabel(protocol);

    internal static void Authenticate(HttpRequestMessage request, ApiProtocol protocol, string key)
    {
        ValidateProtocol(protocol);
        if (protocol == ApiProtocol.AnthropicMessages)
        {
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
