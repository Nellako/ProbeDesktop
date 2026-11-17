using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProbeDesktop.Core;

/// <summary>Exact secret replacement precedes bounded heuristic redaction. No raw response objects are persisted.</summary>
internal sealed class SensitiveText
{
    private const string Hidden = "[已脱敏]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly Regex Bearer = Pattern(@"(?i)\b(?:bearer|basic)\s+[^\s\""'<> ,;]+|\bauthorization\s*[:=]\s*[^\r\n]+", RegexOptions.None);
    private static readonly Regex Assignments = Pattern(@"(?i)[\""']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|secret|password|cookie|set-cookie|token)[\""']?\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;&}]+)", RegexOptions.None);
    private static readonly Regex Urls = Pattern(@"(?i)https?://[^\s\""'<>]+", RegexOptions.None);
    private static readonly Regex Token = Pattern(@"(?i)\b(?:sk|rk|pk|sess|ghp|gho|xoxb|xoxp)[-_][a-z0-9_\-]{8,}\b|\beyJ[a-z0-9_\-]+\.[a-z0-9_\-]+(?:\.[a-z0-9_\-]+)?\b|(?<![\p{L}\p{N}_])[a-z0-9_+/=\-]{28,}(?![\p{L}\p{N}_])", RegexOptions.None);
    private static readonly Regex ExplicitToken = Pattern(@"(?i)\b(?:sk|rk|pk|sess|ghp|gho|xoxb|xoxp)[-_][a-z0-9_\-]{8,}\b|\beyJ[a-z0-9_\-]+\.[a-z0-9_\-]+(?:\.[a-z0-9_\-]+)?\b", RegexOptions.None);
    private readonly string[] _secrets;

    internal SensitiveText(IEnumerable<string> secrets)
    {
        // Include serialized/escaped variants; old keys are retained encrypted in the vault after key rotation.
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (string key in secrets.Where(x => !string.IsNullOrEmpty(x)))
        {
            values.Add(key);
            values.Add(Uri.EscapeDataString(key));
            values.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)));
            string serialized = JsonSerializer.Serialize(key);
            if (serialized.Length >= 2) values.Add(serialized[1..^1]);
        }
        _secrets = values.OrderByDescending(x => x.Length).ToArray();
    }

    internal string Text(string? value, int maximum = 1024) => Clean(value, maximum, identifier: false);

    // Structured model identifiers may legitimately be long/high-entropy; exact known secrets and
    // explicit credential formats are still removed, without treating every long identifier as a token.
    internal string Identifier(string? value, int maximum = 256) => Clean(value, maximum, identifier: true);
    internal string? OptionalIdentifier(string? value) => value is null ? null : Identifier(value);

    private string Clean(string? value, int maximum, bool identifier)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string clean = value;
        // String replacement rather than regex ensures punctuation, short keys, and repeated/nested occurrences are covered.
        foreach (string secret in _secrets) clean = clean.Replace(secret, Hidden, StringComparison.Ordinal);
        try
        {
            clean = Bearer.Replace(clean, Hidden);
            clean = Assignments.Replace(clean, Hidden);
            clean = Urls.Replace(clean, "[链接已隐藏]");
            clean = (identifier ? ExplicitToken : Token).Replace(clean, Hidden);
        }
        catch (RegexMatchTimeoutException) { return Hidden; }
        clean = new string(clean.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return clean.Length <= maximum ? clean : clean[..maximum] + "…";
    }

    internal string? Optional(string? value, int maximum = 1024) => value is null ? null : Text(value, maximum);

    internal RelayProfile Profile(RelayProfile profile) => profile with
    {
        Name = Text(profile.Name, 100),
        DefaultModelId = Identifier(profile.DefaultModelId),
        Models = profile.Models.Select(m => new ModelEntry(Identifier(m.Id), Optional(m.OwnedBy, 256))).ToList()
    };

    internal ProbeRecord Record(ProbeRecord record) => record with
    {
        ProfileName = Text(record.ProfileName, 100),
        BaseUrl = CleanAddress(record.BaseUrl),
        RemoteRunId = record.RemoteRunId is { } runId && CleanAddress(runId) == runId ? runId : null,
        ModelId = Identifier(record.ModelId),
        ClaimedModel = OptionalIdentifier(record.ClaimedModel),
        RemoteStatus = Optional(record.RemoteStatus, 64),
        Phase = Optional(record.Phase, 256),
        Error = Optional(record.Error, 2048),
        IdentityStatus = Optional(record.IdentityStatus, 64),
        VerdictStatus = Optional(record.VerdictStatus, 64),
        InferredFamily = Optional(record.InferredFamily, 256),
        InferredModel = Optional(record.InferredModel, 256),
        Confidence = Optional(record.Confidence, 128),
        Reasoning = record.Reasoning.Take(100).Select(x => Text(x, 2048)).ToList(),
        Warnings = record.Warnings.Take(100).Select(w => new ProbeWarning(Text(w.Label, 256), Text(w.Severity, 64), Text(w.Evidence, 2048))).ToList(),
        Items = record.Items.Take(500).Select(item => item with
        {
            ProbeId = Text(item.ProbeId, 128),
            Label = Text(item.Label, 256),
            Group = Text(item.Group, 128),
            Status = Text(item.Status, 64),
            Passed = Optional(item.Passed, 32),
            Detail = Optional(item.Detail, 2048)
        }).ToList(),
        ReportUrl = CleanReport(record.ReportUrl)
    };

    internal string CleanAddress(string address)
    {
        foreach (string key in _secrets)
            if (address.Contains(key, StringComparison.Ordinal)) return Hidden;
        return address;
    }

    internal string? CleanReport(string? address)
    {
        string? safe = OutboundSecurity.ReportUrl(address);
        return safe is not null && CleanAddress(safe) == safe ? safe : null;
    }

    private static Regex Pattern(string pattern, RegexOptions options) => new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);
}
