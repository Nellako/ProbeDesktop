using System.IO;
using System.Text.RegularExpressions;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.Services;

/// <summary>
/// 把异常转换成可展示的中文文本。Core 已负责脱敏；这里再做一层兜底遮蔽，且从不展示堆栈或内部异常。
/// </summary>
public static partial class UserMessages
{
    private const int MaxLength = 600;

    public static string Describe(Exception ex) => ex switch
    {
        ClientOperationException c => FormatClient(c),
        OperationCanceledException => "操作已取消或超时。",
        UnauthorizedAccessException => "没有访问该位置的权限，请换一个位置或检查文件权限。",
        PathTooLongException => "路径过长，请换一个较短的位置。",
        DirectoryNotFoundException => "目标文件夹不存在。",
        IOException => "文件读写失败，可能文件已存在、被占用或磁盘不可写。",
        _ => "发生未预期的错误，操作未完成。为避免泄漏敏感信息，未显示技术细节。",
    };

    public static string FormatClient(ClientOperationException ex)
    {
        var message = Redact(ex.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "操作失败。";
        }

        var code = Redact(ex.Code);
        var text = string.IsNullOrWhiteSpace(code) ? message : $"{message}（代码：{code}）";
        if (ex.RetryAfter is { } retry && retry > TimeSpan.Zero)
        {
            text += $"\n服务要求至少等待 {FormatDuration(retry)} 后再试。";
        }

        return text;
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 90)
        {
            return $"{Math.Ceiling(span.TotalSeconds):0} 秒";
        }

        if (span.TotalMinutes < 90)
        {
            return $"{Math.Ceiling(span.TotalMinutes):0} 分钟";
        }

        return $"{span.TotalHours:0.#} 小时";
    }

    /// <summary>兜底遮蔽：Bearer 令牌、sk- 类密钥及过长的不透明串。</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = BearerPattern().Replace(text, "Bearer ***");
        result = KeyLikePattern().Replace(result, "***");
        result = LongTokenPattern().Replace(result, "***");
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength] + "…";
        }

        return result;
    }

    [GeneratedRegex(@"Bearer\s+[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(sk|ak|rk|pk)-[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLikePattern();

    [GeneratedRegex(@"[A-Za-z0-9_\-+=]{40,}")]
    private static partial Regex LongTokenPattern();
}
