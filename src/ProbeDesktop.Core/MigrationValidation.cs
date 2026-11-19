using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProbeDesktop.Core;

internal sealed class MigrationPayload
{
    public int FormatVersion { get; set; } = 1;
    public int StoreSchemaVersion { get; set; } = DesktopStore.SchemaVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RelayProfile> Profiles { get; set; } = [];
    public Vault Vault { get; set; } = new();
}

internal static class MigrationValidation
{
    internal const int MaximumPayloadBytes = 24 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(DesktopStore.Json)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static MigrationPayload Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumPayloadBytes) throw new JsonException();
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateShape(document.RootElement, typeof(MigrationPayload));
        MigrationPayload payload = JsonSerializer.Deserialize<MigrationPayload>(bytes, Json) ?? throw new JsonException();
        if (payload.FormatVersion != 1 || payload.StoreSchemaVersion != DesktopStore.SchemaVersion) throw new JsonException();
        Validate(payload);
        return payload;
    }

    // Walk the actual typed contract before deserialization: no duplicate/unknown/missing fields,
    // integer enum aliases, null collections or permissive case-insensitive last-wins parsing.
    internal static void ValidateShape(JsonElement value, Type type, bool requireAll = true)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) { if (value.ValueKind == JsonValueKind.Null) return; type = nullable; }
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (type == typeof(string)) return; // Semantic mandatory strings are checked below.
            throw new JsonException();
        }
        if (type == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 8192) throw new JsonException();
            return;
        }
        if (type.IsEnum)
        {
            if (value.ValueKind != JsonValueKind.String || !Enum.GetNames(type).Contains(value.GetString(), StringComparer.Ordinal)) throw new JsonException();
            return;
        }
        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTimeOffset))
        {
            _ = value.Deserialize(type, Json) ?? throw new JsonException();
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            Type[] arguments = type.GetGenericArguments();
            foreach (JsonProperty p in value.EnumerateObject())
            {
                if (!keys.Add(p.Name) || keys.Count > 100_000 || p.Name.Length > 8192) throw new JsonException();
                if (arguments[0] == typeof(Guid) && (!Guid.TryParseExact(p.Name, "D", out Guid id) || id == Guid.Empty)) throw new JsonException();
                ValidateShape(p.Value, arguments[1], requireAll);
            }
            return;
        }
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100_000) throw new JsonException();
            Type item = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            foreach (JsonElement element in value.EnumerateArray()) ValidateShape(element, item, requireAll);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        Dictionary<string, PropertyInfo> properties = type.GetProperties().Where(p => p.GetMethod is not null &&
            p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .ToDictionary(p => Json.PropertyNamingPolicy!.ConvertName(p.Name), StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty p in value.EnumerateObject())
        {
            if (!seen.Add(p.Name) || !properties.TryGetValue(p.Name, out PropertyInfo? property)) throw new JsonException();
            if (p.Value.ValueKind == JsonValueKind.Null && new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable) continue;
            ValidateShape(p.Value, property.PropertyType, requireAll);
        }
        // Older migration packages predate the independent connection reading check.
        // Missing new summary fields mean "not checked", never inferred success.
        if (type == typeof(ConnectionTestResult))
        {
            properties.Remove("contextCheck");
            properties.Remove("promptVersion");
            seen.Remove("contextCheck");
            seen.Remove("promptVersion");
        }
        // Additive fields in existing schema 3: accept old absence, but still validate every
        // present value above. No version/revision means a legacy grant cannot be used.
        string[] additive = type == typeof(Vault) ?
        ["lastInspectionCompletedAt", "inspectionSettingsVersion", "inspectionSettingsRevision", "inspectionUpgradePending",
         "lastIdentityInspectionCompletedAt", "identityInspectionSettingsVersion", "identityInspectionSettingsRevision", "identityInspectionUpgradePending"] :
            type == typeof(InspectionSettings) ? ["maxRetries"] :
            type == typeof(InspectionGrant) ? ["maxAttempts"] :
            type == typeof(InspectionRoundSummary) ? ["intervalMinutes", "maxRetries"] :
            type == typeof(IdentityInspectionRoundSummary) ? ["intervalMinutes"] : [];
        if (requireAll && properties.Keys.Any(p => !seen.Contains(p) && !additive.Contains(p, StringComparer.Ordinal))) throw new JsonException();
    }

    private static void Unique<T>(IEnumerable<T> values)
    {
        HashSet<T> seen = [];
        foreach (T value in values) if (value is null || !seen.Add(value)) throw new JsonException();
    }
    private static void Id(Guid id) { if (id == Guid.Empty) throw new JsonException(); }
    private static void Text(string? text, int max, bool empty = false)
    {
        if (text is null || (!empty && string.IsNullOrWhiteSpace(text)) || text.Length > max || text.Any(char.IsControl)) throw new JsonException();
    }
    private static void Target(Guid profileId, int revision, string url, string model)
    {
        Id(profileId); if (revision < 1) throw new JsonException(); Text(url, 2048); Text(model, 256);
        // Passive URI checks only. Do not resolve DNS or normalize/mutate source data.
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) throw new JsonException();
    }
    internal static void Validate(MigrationPayload p)
    {
        Vault v = p.Vault;
        if (p.Profiles.Count > 1000 || v.InspectionSettings.IntervalMinutes is < 10 or > 480 ||
            v.IdentityInspectionSettings.IntervalMinutes is < 10 or > 480 || v.InspectionSettings.MaxRetries is < 0 or > 2 ||
            v.InspectionSettingsVersion is not (0 or 1) || v.IdentityInspectionSettingsVersion is not (0 or 1) ||
            v.LastInspectionCompletedAt > DateTimeOffset.MaxValue.AddMinutes(-480) || v.LastIdentityInspectionCompletedAt > DateTimeOffset.MaxValue.AddMinutes(-480)) throw new JsonException();
        SensitiveText exactSecrets = new(v.KnownSecrets.Concat(v.Secrets.Values.Select(s => s.Key)));
        void SafeIdentifier(string? text)
        {
            if (text is not null && exactSecrets.CleanAddress(text) != text) throw new JsonException();
        }
        // Structural identifiers are never silently rewritten (that would break references or remote queries).
        // A secret-bearing identifier is instead rejected with the generic migration error.
        foreach (RelayProfile profile in p.Profiles)
        {
            SafeIdentifier(profile.BaseUrl); SafeIdentifier(profile.SecretRef); SafeIdentifier(profile.DefaultModelId);
            foreach (ModelEntry model in profile.Models) SafeIdentifier(model.Id);
        }
        foreach (ProbeRecord record in v.History)
        { SafeIdentifier(record.BaseUrl); SafeIdentifier(record.ModelId); SafeIdentifier(record.ClaimedModel); SafeIdentifier(record.RemoteRunId); }
        foreach (ConnectionTestResult record in v.ConnectionHistory.Concat(v.ConnectionResults.Values))
        { SafeIdentifier(record.BaseUrl); SafeIdentifier(record.ModelId); }
        foreach (var settings in v.ConnectionSettings.Values) { SafeIdentifier(settings.BaseUrl); SafeIdentifier(settings.ModelId); }
        foreach (var selection in v.InspectionSelections) foreach (string model in selection.SelectedModelIds) SafeIdentifier(model);
        foreach (var selection in v.IdentityInspectionSelections) foreach (string model in selection.SelectedModelIds) SafeIdentifier(model);
        foreach (var memory in v.NotificationMemory)
        { SafeIdentifier(memory.Target.BaseUrl); SafeIdentifier(memory.Target.ModelId); SafeIdentifier(memory.ProblemType); }
        if (v.LatestIdentityInspectionRound is { } safeRound) foreach (var attempt in safeRound.Attempts)
        { SafeIdentifier(attempt.Target.BaseUrl); SafeIdentifier(attempt.Target.ModelId); SafeIdentifier(attempt.Code); }
        Unique(p.Profiles.Select(x => x.Id)); Unique(p.Profiles.Select(x => x.SecretRef));
        if (v.Secrets.Count != p.Profiles.Count) throw new JsonException();
        foreach (string secret in v.KnownSecrets) Text(secret, 8192);
        foreach (RelayProfile profile in p.Profiles)
        {
            Target(profile.Id, profile.KeyRevision, profile.BaseUrl, "placeholder"); Text(profile.Name, 100); Text(profile.SecretRef, 256);
            Text(profile.DefaultModelId, 256, true);
            if (profile.Models.Count > 2000) throw new JsonException();
            Unique(profile.Models.Select(m => m.Id));
            foreach (ModelEntry model in profile.Models) Text(model.Id, 256);
            if (!v.Secrets.TryGetValue(profile.Id, out SecretEntry? secret) || secret.Revision != profile.KeyRevision ||
                secret.Reference != profile.SecretRef || !v.KnownSecrets.Contains(secret.Key)) throw new JsonException();
            Text(secret.Key, 8192);
        }
        Unique(v.History.Select(r => r.LocalId)); Unique(v.ConnectionHistory.Select(r => r.RecordId));
        foreach (ProbeRecord r in v.History)
        {
            Id(r.LocalId); Target(r.ProfileId, r.KeyRevision, r.BaseUrl, r.ModelId);
            if (r.RemoteRunId is not null && !OutboundSecurity.IsSafeRunId(r.RemoteRunId)) throw new JsonException();
            if (r.ServiceOrigin != OutboundSecurity.ServiceOrigin || r.Reasoning.Any(x => x is null)) throw new JsonException();
        }
        foreach (ConnectionTestResult r in v.ConnectionHistory.Concat(v.ConnectionResults.Values))
        { Id(r.RecordId); Target(r.ProfileId, r.KeyRevision, r.BaseUrl, r.ModelId); }
        foreach (var pair in v.ConnectionResults) if (pair.Key != pair.Value.ProfileId) throw new JsonException();
        foreach (var pair in v.ConnectionSettings)
        {
            if (pair.Key != pair.Value.ProfileId || !p.Profiles.Any(x => x.Id == pair.Key) || pair.Value.IntervalMinutes is < 1 or > 1440) throw new JsonException();
            Target(pair.Value.ProfileId, pair.Value.KeyRevision, pair.Value.BaseUrl, pair.Value.ModelId);
        }
        Unique(v.InspectionSelections.Select(s => s.ProfileId)); Unique(v.IdentityInspectionSelections.Select(s => s.ProfileId));
        foreach (var s in v.InspectionSelections) Selection(s.ProfileId, s.SelectedModelIds, p.Profiles);
        foreach (var s in v.IdentityInspectionSelections) Selection(s.ProfileId, s.SelectedModelIds, p.Profiles);
        foreach (var g in v.InspectionGrants)
        {
            if (g.IntervalMinutes is < 10 or > 480 || g.MaxAttempts is < 1 or > 3) throw new JsonException();
            Id(g.Target.ProfileId); if (g.Target.KeyRevision < 1) throw new JsonException();
            Unique(g.Target.Models); foreach (string m in g.Target.Models) Target(g.Target.ProfileId, g.Target.KeyRevision, g.Target.BaseUrl, m);
        }
        foreach (var g in v.IdentityInspectionGrants)
        { if (g.IntervalMinutes is < 10 or > 480) throw new JsonException(); Target(g.Target.ProfileId, g.Target.KeyRevision, g.Target.BaseUrl, g.Target.ModelId); }
        foreach (var m in v.NotificationMemory) Target(m.Target.ProfileId, m.Target.KeyRevision, m.Target.BaseUrl, m.Target.ModelId);
        if (v.LatestInspectionRound is { } connectionRound)
        {
            Id(connectionRound.RoundId);
            if (new[] { connectionRound.Total, connectionRound.Succeeded, connectionRound.Failed, connectionRound.PossibleSuccessCount,
                connectionRound.UnknownCount, connectionRound.Skipped }.Any(x => x < 0)) throw new JsonException();
        }
        if (v.LatestIdentityInspectionRound is { } round)
        {
            Id(round.RoundId); Unique(round.Attempts.Select(a => a.AttemptId)); Unique(round.Attempts.Select(a => a.Target));
            foreach (var a in round.Attempts)
            {
                Id(a.AttemptId); Target(a.Target.ProfileId, a.Target.KeyRevision, a.Target.BaseUrl, a.Target.ModelId);
                if (a.ProbeLocalId is Guid probeId)
                {
                    var record = v.History.SingleOrDefault(r => r.LocalId == probeId);
                    // A user may have deleted its history record; only extant associations must agree.
                    if (record is not null && (record.IdentityInspectionAttemptId != a.AttemptId || record.IdentityInspectionRoundId != round.RoundId ||
                        record.ProfileId != a.Target.ProfileId || record.ModelId != a.Target.ModelId || record.KeyRevision != a.Target.KeyRevision ||
                        record.BaseUrl != a.Target.BaseUrl || record.Protocol != a.Target.Protocol)) throw new JsonException();
                }
            }
            foreach (ProbeRecord record in v.History.Where(r => r.IdentityInspectionRoundId == round.RoundId))
                if (!round.Attempts.Any(a => a.AttemptId == record.IdentityInspectionAttemptId && a.ProbeLocalId == record.LocalId)) throw new JsonException();
        }
    }
    private static void Selection(Guid id, IReadOnlyList<string> models, List<RelayProfile> profiles)
    {
        Id(id); Unique(models);
        // Old selections for deleted profiles may remain until normal convergence; preserve, never grant.
        foreach (string model in models) Text(model, 256);
        RelayProfile? profile = profiles.Find(p => p.Id == id);
        if (profile is not null && models.Any(m => !profile.Models.Any(c => c.Id == m))) throw new JsonException();
    }
}
