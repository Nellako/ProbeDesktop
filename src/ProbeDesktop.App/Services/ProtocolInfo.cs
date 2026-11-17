using ProbeDesktop.Core;

namespace ProbeDesktop.App.Services;

public sealed record ProtocolOption(ApiProtocol Value, string Label);

/// <summary>API 协议的界面文案与派生信息。请求地址以 Core 的 ModelApiEndpoints 为准，这里只做展示。</summary>
public static class ProtocolInfo
{
    /// <summary>下拉顺序：Responses、Claude Messages、Chat Completions（兼容）。</summary>
    public static IReadOnlyList<ProtocolOption> Options { get; } =
    [
        new(ApiProtocol.OpenAIResponses, "OpenAI Responses"),
        new(ApiProtocol.AnthropicMessages, "Claude Messages"),
        new(ApiProtocol.ChatCompletions, "Chat Completions（兼容）"),
    ];

    /// <summary>界面新建配置的默认协议；旧配置按已保存值加载，不在此改写。</summary>
    public const ApiProtocol DefaultForNew = ApiProtocol.OpenAIResponses;

    public static string Display(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIResponses => "OpenAI Responses",
        ApiProtocol.AnthropicMessages => "Claude Messages",
        ApiProtocol.ChatCompletions => "Chat Completions（兼容）",
        _ => $"未知协议（{(int)protocol}）",
    };

    public static string PathSuffix(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIResponses => "/responses",
        ApiProtocol.AnthropicMessages => "/messages",
        _ => "/chat/completions",
    };

    /// <summary>连接测试请求体里的输出上限参数（用于确认文案）。</summary>
    public static string TokenParam(ApiProtocol protocol)
        => protocol == ApiProtocol.OpenAIResponses ? "max_output_tokens=64" : "max_tokens=64";

    /// <summary>直连连接测试输入说明：约256 token（非256k），实际分词随模型不同。</summary>
    public const string ConnectionInputNote = "约256 token合成英文文本（实际 token 数因模型分词不同而异，非256k上下文压力测试），含首、中、尾三个随机标记";

    public static string ContextLabel(ConnectionContextCheck check) => check switch
    {
        ConnectionContextCheck.Passed => "通过",
        ConnectionContextCheck.Mismatch => "不匹配",
        ConnectionContextCheck.Inconclusive => "无法判断",
        _ => "未检验",
    };

    public static string ContextBadgeText(ConnectionContextCheck check) => $"标记：{ContextLabel(check)}";

    public static string ContextTone(ConnectionContextCheck check) => check switch
    {
        ConnectionContextCheck.Passed => "ok",
        ConnectionContextCheck.Mismatch => "possible",
        _ => "bad",
    };

    public static string ContextNote(ConnectionContextCheck check) => check switch
    {
        ConnectionContextCheck.Passed => "本次已按顺序和格式返回首、中、尾三个标记；仅说明本次读回标记，不证明模型真实身份或能力，也不是256k上下文压力测试。",
        ConnectionContextCheck.Mismatch => "已收到模型响应（连接成功），但没有按要求格式返回三个标记；不等于断连或降智，不因此新增重试或问题通知，也不证明真实身份或能力。",
        ConnectionContextCheck.Inconclusive => "响应未完整确认、超限或存在多输出歧义，无法判断标记读取情况；不等于断连或降智。",
        _ => "旧记录无标记校验证据，显示为未检验，不对旧结果追溯补判。",
    };

    /// <summary>
    /// 在 Core 返回的摘要说明基础上补充标记校验边界说明；若 Core 消息已包含“标记读取”结论，则只补充不重复的边界提示。
    /// </summary>
    public static string AppendContextDetail(string? message, ConnectionContextCheck check)
    {
        var baseText = message?.Trim() ?? "";
        if (check == ConnectionContextCheck.NotChecked)
        {
            var legacyNote = ContextNote(check);
            return baseText.Length == 0 ? legacyNote : baseText + "\n" + legacyNote;
        }

        if (baseText.Contains("标记读取", StringComparison.Ordinal))
        {
            var tail = check switch
            {
                ConnectionContextCheck.Passed => "仅说明本次按格式读回三个标记，不证明模型真实身份或能力，也不是256k上下文压力测试。",
                ConnectionContextCheck.Mismatch => "标记校验独立于连接状态，不改变重试、统计或问题通知，也不是256k上下文压力测试。",
                _ => "响应未完整确认、超限或存在歧义时不做标记判定。",
            };
            return baseText + "\n" + tail;
        }

        var full = $"标记读取：{ContextLabel(check)}。{ContextNote(check)}";
        return baseText.Length == 0 ? full : baseText + "\n" + full;
    }

    /// <summary>费用说明用的“预计输出上限”文字，不是成本上限。</summary>
    public static string TokenLimitNote(ApiProtocol protocol)
        => $"请求参数 {TokenParam(protocol)}（预计的输出上限，不是成本上限）";

    public static string AuthHeaderText(ApiProtocol protocol) => protocol == ApiProtocol.AnthropicMessages
        ? "x-api-key 与 anthropic-version: 2023-06-01 请求头"
        : "Authorization: Bearer 请求头";

    /// <summary>
    /// BazaarLink 官方公开前端提供三种 upstreamFormat（Chat Completions、OpenAI Responses、Anthropic Messages）。
    /// 三种协议的配置都可以提交，不再按协议禁用；但本程序尚未对三种格式做真实端到端验证，这一点须始终如实说明。
    /// 提交时具体声明哪个 upstreamFormat 由 Core 集中映射，App 只负责展示所选协议，不自行拼装。
    /// </summary>
    public static string ProbeProtocolNote(ApiProtocol protocol)
        => $"该配置的协议是 {Display(protocol)}。BazaarLink 官方公开前端支持 Chat Completions、OpenAI Responses、Anthropic Messages 三种 upstreamFormat，" +
           "本程序会按所选协议提交，不会改用其他协议。但三种格式都尚未用真实中转站做过端到端验证：提交可能被网站拒绝、返回不兼容的结果，或得到与预期不同的报告，" +
           "遇到这些情况请以网站官方页面为准。";

    /// <summary>外发确认框里用的一句话版本：明确本次提交使用的协议与未验证状态。</summary>
    public static string ProbeProtocolConfirmLine(ApiProtocol protocol)
        => $"提交协议：{Display(protocol)}，将向 BazaarLink 声明 upstreamFormat = {TryUpstreamFormat(protocol) ?? "（未知协议，无法映射）"}。" +
           "三种格式均尚未用真实中转站端到端验证，可能被拒绝或返回不兼容的结果";

    /// <summary>Core 集中映射的 upstreamFormat；未知协议时返回 null，由界面如实提示而不是抛异常。</summary>
    public static string? TryUpstreamFormat(ApiProtocol protocol)
    {
        try
        {
            return ProbeProtocolMapping.UpstreamFormat(protocol);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? TryModelsUrl(string baseUrl, ApiProtocol protocol)
    {
        try
        {
            return ModelApiEndpoints.Models(baseUrl, protocol);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? TryInferenceUrl(string baseUrl, ApiProtocol protocol)
    {
        try
        {
            return ModelApiEndpoints.Inference(baseUrl, protocol);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
