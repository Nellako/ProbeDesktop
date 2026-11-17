using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace ProbeDesktop.Core;

internal static class OutboundSecurity
{
    internal const string ServiceOrigin = "https://bazaarlink.ai";
    internal const string StartPath = "/api/probe/run";
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;

    internal static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = ConnectAsync
        };
        // TLS validation remains the framework default. The callback changes TCP routing, not TLS identity checks.
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90) };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("目标未解析为公开网络地址。");
        // No HTTP-level retries, and no second address is silently tried after a failed connection.
        IPAddress selected = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        Socket socket = new(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(selected, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static string NormalizeBaseUrl(string? input)
    {
        string value = input?.Trim() ?? "";
        if (value.Length is 0 or > 2048 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrEmpty(uri.Host) || uri.Port == 0 ||
            value.Contains('?') || value.Contains('#'))
            throw InvalidUrl();
        string host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && !IsPublicAddress(address)))
            throw InvalidUrl();
        string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        string[] endpoints = ["/chat/completions", "/responses", "/messages", "/models"];
        if (endpoints.Any(endpoint => path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase)))
            throw new ClientOperationException("InvalidBaseUrl", "请填写 API 根地址而非完整端点：移除末尾 /messages、/responses、/chat/completions 或 /models，保留原有 /v1 或自定义前缀；不会自动添加 /v1。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static ClientOperationException InvalidUrl() => new("InvalidBaseUrl",
        "API Base URL 必须是 HTTPS 公网地址，不含用户名、密码、查询参数或片段；不会自动补充 /v1。");

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99))) ||
                (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) ||
                (b[0] == 203 && b[1] == 0 && b[2] == 113)) return false;
            return true;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // Accept only global unicast, excluding documentation, transition and special-purpose blocks.
        return (b[0] & 0xe0) == 0x20 &&
               !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 0x02 || (b[2] == 0x0d && b[3] == 0xb8))) &&
               !(b[0] == 0x20 && b[1] == 0x02) &&
               !(b[0] == 0x3f && b[1] == 0xff);
    }

    internal static void RequireConsent(RelayProfile profile, OutboundConsent? consent, OutboundPurpose purpose, string? modelId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string recipient = purpose switch
        {
            OutboundPurpose.ModelList => ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol),
            OutboundPurpose.ConnectionTest => ConnectionTestEndpoints.Recipient(profile.BaseUrl, profile.Protocol),
            _ => ServiceOrigin + StartPath
        };
        if (consent is null || consent.ProfileId != profile.Id || consent.KeyRevision != profile.KeyRevision || consent.Protocol != profile.Protocol ||
            !string.Equals(consent.BaseUrl, profile.BaseUrl, StringComparison.Ordinal) || consent.Purpose != purpose ||
            !string.Equals(consent.Recipient, recipient, StringComparison.Ordinal) ||
            !string.Equals(consent.ModelId, modelId, StringComparison.Ordinal) ||
            consent.AcceptedAt < now.AddMinutes(-10) || consent.AcceptedAt > now.AddSeconds(30))
            throw new ClientOperationException("ConsentRequired", "请重新确认当前档案、密钥版本、完整目标地址、操作用途及模型；确认有效期为 10 分钟。");
    }

    internal static bool IsSafeRunId(string? id) => id is { Length: > 0 and <= 200 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static string? ReportUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            value.Contains('\\') || value.Contains('%') || value.Contains('?') || value.Contains('#') ||
            value.Contains("..", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) return null;
        string candidate = value.StartsWith('/') ? ServiceOrigin + value : value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("bazaarlink.ai", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        string path = uri.AbsolutePath;
        string[] prefixes = ["/api/probe/report/", "/probe/", "/report/", "/reports/"];
        string? prefix = prefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null) return null;
        string suffix = path[prefix.Length..];
        if (suffix.Length is 0 or > 300 || !suffix.Split('/').All(IsSafeRunId)) return null;
        return uri.AbsoluteUri;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw HttpError(response);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            throw new ClientOperationException("UnexpectedContent", "服务返回了非 JSON 内容（可能是网页或验证页面），不能确认任务结果。");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new ClientOperationException("ResponseTooLarge", "服务响应超过安全大小限制，已停止读取。");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream bytes = new();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > MaximumResponseBytes)
                    throw new ClientOperationException("ResponseTooLarge", "服务响应超过安全大小限制，已停止读取。");
                bytes.Write(buffer, 0, count);
            }
            try
            {
                bytes.Position = 0;
                // ParseAsync(Stream) owns its JSON backing storage; the source buffer can now be zeroed safely.
                return await JsonDocument.ParseAsync(bytes, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { throw new ClientOperationException("MalformedJson", "服务返回的 JSON 格式无效，不能确认任务结果。"); }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes.GetBuffer().AsSpan());
        }
    }

    internal static ClientOperationException HttpError(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        string message = status switch
        {
            401 => "服务拒绝了凭据（401），请检查密钥；不会自动重试。",
            403 => "服务拒绝访问（403），可能需要权限或人工验证；不会自动重试。",
            404 => "任务或接口不存在（404）；异步任务可能已经过期。",
            410 => "任务已过期或被服务移除（410）。",
            429 => "请求受到限流（429），请按服务建议稍后再操作；不会自动重试。",
            502 => "服务网关异常（502），结果可能未知；不会重新提交。",
            503 => "服务暂时不可用（503），请稍后查询；不会重新提交。",
            >= 300 and < 400 => "服务要求跳转；为保护密钥，客户端拒绝重定向。",
            _ => $"服务返回 HTTP {status}，本次操作未得到可确认结果。"
        };
        TimeSpan? retry = ParseRetryAfter(response.Headers.RetryAfter);
        return new ClientOperationException("Http" + status, message, retry);
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? value)
    {
        TimeSpan? result = value?.Delta ?? (value?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
        return result is null ? null : result < TimeSpan.Zero ? TimeSpan.Zero : result;
    }
}
