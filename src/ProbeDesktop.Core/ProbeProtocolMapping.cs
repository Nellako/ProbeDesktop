namespace ProbeDesktop.Core;

/// <summary>Official public Probe frontend upstreamFormat values; not an end-to-end compatibility guarantee.</summary>
public static class ProbeProtocolMapping
{
    public static string UpstreamFormat(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.ChatCompletions => "openai",
        ApiProtocol.OpenAIResponses => "openai-responses",
        ApiProtocol.AnthropicMessages => "anthropic",
        _ => throw new ClientOperationException("InvalidProtocol", "未知 API 协议。")
    };
}
