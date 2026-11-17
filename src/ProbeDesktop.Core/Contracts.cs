namespace ProbeDesktop.Core;

public enum ApiProtocol { ChatCompletions = 0, OpenAIResponses = 1, AnthropicMessages = 2 }

public sealed record ModelEntry(string Id, string? OwnedBy = null);

public sealed record RelayProfile
{
    public ApiProtocol Protocol { get; init; } = ApiProtocol.ChatCompletions;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string SecretRef { get; init; } = "";
    public int KeyRevision { get; init; }
    public string DefaultModelId { get; init; } = "";
    public List<ModelEntry> Models { get; init; } = [];
    public DateTimeOffset? ModelsFetchedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum OutboundPurpose { ModelList, Probe, ConnectionTest }

public sealed record OutboundConsent(
    Guid ProfileId,
    int KeyRevision,
    string BaseUrl,
    OutboundPurpose Purpose,
    string Recipient,
    string? ModelId,
    DateTimeOffset AcceptedAt,
    ApiProtocol Protocol = ApiProtocol.ChatCompletions,
    bool AllowModelPagination = false);

public enum LocalRunState
{
    Submitting, SubmissionUnknown, Running, PollingInterrupted,
    Completed, Failed, Aborted, Expired, IncompatibleResponse
}

public sealed record ProbeItem
{
    public string ProbeId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Passed { get; init; }
    public string? Detail { get; init; }
    public double? DurationMs { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
}

public sealed record ProbeWarning(string Label, string Severity, string Evidence);

public sealed record ProbeRecord
{
    public ProbeRecordSource Source { get; init; } = ProbeRecordSource.Manual;
    public Guid? CapabilityRecordId { get; init; }
    public Guid? CapabilityRoundId { get; init; } // Legacy history only.
    public Guid? IdentityInspectionAttemptId { get; init; }
    public Guid? IdentityInspectionRoundId { get; init; }
    public IdentityInspectionTrigger? IdentityInspectionTrigger { get; init; }
    public ApiProtocol Protocol { get; init; } = ApiProtocol.ChatCompletions;
    public Guid LocalId { get; init; } = Guid.NewGuid();
    public Guid ProfileId { get; init; }
    public int KeyRevision { get; init; }
    public string ProfileName { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? ClaimedModel { get; init; }
    public string? RemoteRunId { get; init; }
    /// <summary>Imported unfinished task: host must not resume polling automatically.</summary>
    public bool ManualResumeRequired { get; init; }
    public string ServiceOrigin { get; init; } = "https://bazaarlink.ai";
    public string AdapterVersion { get; init; } = "1";
    public LocalRunState State { get; init; } = LocalRunState.Submitting;
    public string? RemoteStatus { get; init; }
    public DateTimeOffset ConsentAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? LastPolledAt { get; init; }
    public string? Phase { get; init; }
    public string? Error { get; init; }
    public string? IdentityStatus { get; init; }
    public string? VerdictStatus { get; init; }
    public string? InferredFamily { get; init; }
    public string? InferredModel { get; init; }
    public string? Confidence { get; init; }
    public List<string> Reasoning { get; init; } = [];
    public List<ProbeWarning> Warnings { get; init; } = [];
    public List<ProbeItem> Items { get; init; } = [];
    public long? TotalInputTokens { get; init; }
    public long? TotalOutputTokens { get; init; }
    public string? ReportUrl { get; init; }
}

public interface IDesktopService : IDisposable
{
    string DataDirectory { get; }
    Task<IReadOnlyList<RelayProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<RelayProfile> SaveProfileAsync(RelayProfile profile, string? replacementKey, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<RelayProfile> RefreshModelsAsync(Guid profileId, OutboundConsent consent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProbeRecord>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<ProbeRecord> StartProbeAsync(Guid profileId, string modelId, string? claimedModel, OutboundConsent consent, CancellationToken cancellationToken = default);
    Task<ProbeRecord> PollProbeAsync(Guid localId, CancellationToken cancellationToken = default);
    Task DeleteRecordAsync(Guid localId, CancellationToken cancellationToken = default);
    Task ExportRecordAsync(Guid localId, string destinationPath, CancellationToken cancellationToken = default);
}

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class ClientOperationException : Exception
{
    public string Code { get; }
    public TimeSpan? RetryAfter { get; }
    public ClientOperationException(string code, string message, TimeSpan? retryAfter = null) : base(message)
    {
        Code = code;
        RetryAfter = retryAfter;
    }
}
