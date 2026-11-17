using System.Net.Http;
using System.Text;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class Fixtures
{
    public const string RelayBase = "https://relay.example.com/v1";
    public const string ServiceRunUrl = "https://bazaarlink.ai/api/probe/run";

    public static RelayProfile NewProfile(string name = "Primary", string baseUrl = RelayBase, string defaultModel = "model-a")
        => new() { Name = name, BaseUrl = baseUrl, DefaultModelId = defaultModel };

    /// <summary>Consent per agreed contract: Recipient is the full URL, ModelId null for model list.</summary>
    public static OutboundConsent ModelConsent(RelayProfile profile)
        => new(
            profile.Id,
            profile.KeyRevision,
            profile.BaseUrl,
            OutboundPurpose.ModelList,
            new Uri(profile.BaseUrl.TrimEnd('/') + "/models").AbsoluteUri,
            null,
            DateTimeOffset.UtcNow);

    /// <summary>Consent per agreed contract: Recipient is the fixed probe start URL, ModelId is this run's model.</summary>
    public static OutboundConsent ProbeConsent(RelayProfile profile, string modelId)
        => new(
            profile.Id,
            profile.KeyRevision,
            profile.BaseUrl,
            OutboundPurpose.Probe,
            ServiceRunUrl,
            modelId,
            DateTimeOffset.UtcNow);

    public static HttpClient Client(FakeHttpHandler handler) => new(handler, disposeHandler: false);

    public static string ModelsJson(params string[] ids)
    {
        var sb = new StringBuilder("{\"object\":\"list\",\"data\":[");
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(ids[i]).Append("\",\"object\":\"model\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    public static string ReadAllStoreText(string directory)
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(directory)) return sb.ToString();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            sb.AppendLine(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        return sb.ToString();
    }

    public static string ReadAllText(string filePath) => Encoding.UTF8.GetString(File.ReadAllBytes(filePath));

    /// <summary>Official submission acknowledgment: only a runId. No status field is documented on start.</summary>
    public static string RunIdOnly(string runId)
        => $"{{\"runId\":\"{runId}\"}}";

    /// <summary>Poll response with an explicit documented status (running/completed/failed/aborted).</summary>
    public static string ProbeStatusJson(string runId, string status)
        => $"{{\"runId\":\"{runId}\",\"status\":\"{status}\"}}";
}
