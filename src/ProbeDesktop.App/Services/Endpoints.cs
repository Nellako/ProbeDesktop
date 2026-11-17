namespace ProbeDesktop.App.Services;

/// <summary>出站目标地址的计算与界面侧前置校验（Core 仍为最终校验方）。</summary>
public static class Endpoints
{
    public const string ProbeRunUrl = "https://bazaarlink.ai/api/probe/run";

    /// <summary>
    /// 计算模型列表的完整请求地址：配置的 API Base 去掉末尾 "/" 后追加 "/models"。不自动补 /v1。
    /// </summary>
    public static bool TryBuildModelsUri(string? baseUrl, out string modelsUri, out string error)
    {
        modelsUri = "";
        if (!TryValidateBaseUrl(baseUrl, out var uri, out error))
        {
            return false;
        }

        var text = uri!.AbsoluteUri.TrimEnd('/');
        modelsUri = new Uri(text + "/models", UriKind.Absolute).AbsoluteUri;
        return true;
    }

    /// <summary>界面侧校验：仅允许 https，不含用户名密码、查询串或片段。</summary>
    public static bool TryValidateBaseUrl(string? baseUrl, out Uri? uri, out string error)
    {
        uri = null;
        error = "";
        var text = baseUrl?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "请填写 Base URL。";
            return false;
        }

        if (text.Length > 2048)
        {
            error = "Base URL 过长。";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "Base URL 不是有效的绝对地址，例如 https://relay.example.com/v1";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Base URL 必须使用 https://，不支持不安全的明文连接。";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Base URL 不能包含用户名或密码。";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Base URL 不能包含查询参数（?）或片段（#）。";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>非阻断提示：路径为空时提醒用户确认 API 根路径。</summary>
    public static string? BaseUrlAdvice(Uri uri) => BaseUrlAdvice(uri, ProbeDesktop.Core.ApiProtocol.ChatCompletions);

    public static string? BaseUrlAdvice(Uri uri, ProbeDesktop.Core.ApiProtocol protocol)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length == 0)
        {
            return "提示：该地址没有路径部分。多数中转的 API 根路径以 /v1 结尾，本程序不会自动补全，请确认填写正确。";
        }

        if (path.EndsWith("models", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("responses", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("messages", StringComparison.OrdinalIgnoreCase))
        {
            return $"提示：请填写 API 根路径（如 …/v1），不要包含 /models、/chat/completions、/responses 或 /messages；程序会按“{ProtocolInfo.Display(protocol)}”自动追加 {ProtocolInfo.PathSuffix(protocol)}。";
        }

        return null;
    }
}
