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

    // Construct exactly what a UI would disclose for the draft, rather than first writing
    // selections to storage. Some tests deliberately alter the resulting consent to test rejection.
    public static InspectionConsent InspectionApproval(InspectionSnapshot snapshot,
        IReadOnlyList<InspectionSelection> selections, int minutes = 30, int attempts = 3)
        => new(snapshot.Targets.Select(t => t.Target)
            .Select(t => t with { Models = t.Models.Where(m => selections.Any(s =>
                s.ProfileId == t.ProfileId && s.IsSelected && s.SelectedModelIds.Contains(m))).ToArray() })
            .Where(t => t.Models.Count > 0).ToArray(), minutes, DateTimeOffset.UtcNow)
        { MaxAttempts = attempts, SettingsRevision = snapshot.SettingsRevision };

    public static async Task<InspectionSnapshot> ConfirmInspectionAsync(IGlobalInspectionService service,
        IReadOnlyList<InspectionSelection> selections, int minutes = 30, int attempts = 3, bool enable = false)
    {
        var snapshot = await service.GetInspectionSnapshotAsync();
        var confirmed = await service.ConfirmInspectionSettingsAsync(selections, InspectionApproval(snapshot, selections, minutes, attempts));
        return enable ? await service.SaveInspectionSettingsAsync(new() { Enabled = true }) : confirmed;
    }

    public static IdentityInspectionConsent IdentityApproval(IdentityInspectionSnapshot snapshot,
        IReadOnlyList<IdentityInspectionSelection> selections, int minutes = 30)
        => new(snapshot.Targets.Select(t => t.Target).Where(t => selections.Any(s =>
            s.ProfileId == t.ProfileId && s.IsSelected && s.SelectedModelIds.Contains(t.ModelId))).ToArray(), minutes, DateTimeOffset.UtcNow)
        { SettingsRevision = snapshot.SettingsRevision };

    public static IdentityInspectionConsent ManualIdentityApproval(IdentityInspectionSnapshot snapshot)
        => new(snapshot.Targets.Where(t => t.Selected && t.Authorized && !t.Paused).Select(t => t.Target).ToArray(),
            snapshot.Settings.IntervalMinutes, DateTimeOffset.UtcNow) { SettingsRevision = snapshot.SettingsRevision };

    public static async Task<IdentityInspectionSnapshot> ConfirmIdentityAsync(IIdentityInspectionService service,
        IReadOnlyList<IdentityInspectionSelection> selections, int minutes = 30, bool enable = false)
    {
        var snapshot = await service.GetIdentityInspectionSnapshotAsync();
        var confirmed = await service.ConfirmIdentityInspectionSettingsAsync(selections, IdentityApproval(snapshot, selections, minutes));
        return enable ? await service.SaveIdentityInspectionSettingsAsync(new() { Enabled = true }) : confirmed;
    }

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
