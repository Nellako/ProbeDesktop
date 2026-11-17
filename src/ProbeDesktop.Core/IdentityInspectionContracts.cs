namespace ProbeDesktop.Core;

// Numeric values and the legacy name are a persisted history contract; legacy never enables automation.
public enum ProbeRecordSource { Manual = 0, CapabilityAutomatic = 1, IdentityInspection = 2 }
public sealed record IdentityInspectionTarget(Guid ProfileId, int KeyRevision, string BaseUrl, ApiProtocol Protocol, string ModelId);
public sealed record IdentityInspectionSelection(Guid ProfileId, bool IsSelected, IReadOnlyList<string> SelectedModelIds);
public sealed record IdentityInspectionSettings
{
    public bool Enabled { get; init; }
    public int IntervalMinutes { get; init; } = 30;
}
public sealed record IdentityInspectionConsent(IReadOnlyList<IdentityInspectionTarget> Scope, int IntervalMinutes, DateTimeOffset AcceptedAt);
public sealed record IdentityInspectionTargetStatus(IdentityInspectionTarget Target, string ProfileName, bool Selected, bool Authorized, bool Paused);
public enum IdentityInspectionTrigger { Manual, Automatic }
public enum IdentityInspectionAttemptState { Pending, IntentPersisted, Submitted, Completed, Skipped, Unknown }
public sealed record IdentityInspectionAttempt
{
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public IdentityInspectionTarget Target { get; init; } = new(Guid.Empty, 0, "", ApiProtocol.ChatCompletions, "");
    public IdentityInspectionAttemptState State { get; init; }
    public Guid? ProbeLocalId { get; init; }
    public string? Code { get; init; }
}
public sealed record IdentityInspectionRoundSummary
{
    public IdentityInspectionTrigger Trigger { get; init; } = IdentityInspectionTrigger.Automatic;
    public DateTimeOffset? ManualConsentAt { get; init; }
    public Guid RoundId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<IdentityInspectionAttempt> Attempts { get; init; } = [];
    public int Total => Attempts.Count;
    public int Completed => Attempts.Count(a => a.State == IdentityInspectionAttemptState.Completed);
    public int Skipped => Attempts.Count(a => a.State == IdentityInspectionAttemptState.Skipped);
    public bool Cancelled { get; init; }
    public string? StopCode { get; init; }
    public IReadOnlyList<Guid> PausedProfileIds { get; init; } = [];
}
public sealed record IdentityInspectionSnapshot(IdentityInspectionSettings Settings, IReadOnlyList<IdentityInspectionSelection> Selections, IReadOnlyList<IdentityInspectionTargetStatus> Targets, IdentityInspectionRoundSummary? LatestRound);
/// <summary>No GET is performed by this service. The host waits through its shared Probe coordinator before recording the persisted terminal result.</summary>
public sealed record IdentityInspectionStep(IdentityInspectionRoundSummary Round, ProbeRecord? Probe, bool HasMore, bool WaitingForResult, string? Code = null);
public interface IIdentityInspectionService
{
    Task<IdentityInspectionSnapshot> GetIdentityInspectionSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IdentityInspectionSnapshot> SaveIdentityInspectionSelectionsAsync(IReadOnlyList<IdentityInspectionSelection> selections, CancellationToken cancellationToken = default);
    Task<IdentityInspectionSnapshot> SaveIdentityInspectionSettingsAsync(IdentityInspectionSettings settings, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default);
    Task<IdentityInspectionSnapshot> SaveIdentityInspectionIntervalAsync(int intervalMinutes, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default);
    Task<IdentityInspectionRoundSummary> BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger trigger = IdentityInspectionTrigger.Automatic, IdentityInspectionConsent? consent = null, CancellationToken cancellationToken = default);
    // Claims at most one frozen target, persists intent, revalidates and uses the shared final POST gate. Never retries an intent.
    Task<IdentityInspectionStep> TrySubmitNextIdentityInspectionTargetAsync(Guid roundId, CancellationToken cancellationToken = default);
    // localPollingLimitReached stops this round without claiming the remote run has ended (host limit: ten minutes).
    Task<IdentityInspectionStep> RecordIdentityInspectionResultAsync(Guid roundId, Guid probeLocalId, bool localPollingLimitReached = false, CancellationToken cancellationToken = default);
    Task<IdentityInspectionRoundSummary> EndIdentityInspectionRoundAsync(Guid roundId, bool cancelled = false, CancellationToken cancellationToken = default);
}
