namespace ProbeDesktop.Core;

public enum ConnectionRecordSource { Manual, Automatic, RoundManual }
public enum InspectionAuthorizationState { PendingApproval, Authorized, Paused, NoCachedModels }
public enum InspectionTrigger { Automatic, Manual }

/// <summary>Exact credential revision, base path, protocol and cached model set disclosed by the UI.</summary>
public sealed record InspectionTarget(Guid ProfileId, int KeyRevision, string BaseUrl, ApiProtocol Protocol, IReadOnlyList<string> Models);
public sealed record InspectionTargetStatus(InspectionTarget Target, string ProfileName, InspectionAuthorizationState Authorization, DateTimeOffset? AuthorizedAt, DateTimeOffset? RetryNotBefore)
{
    /// <summary>Unchanged authorized models still run when newly cached models await approval.</summary>
    public IReadOnlyList<string> AuthorizedModels { get; init; } = [];
    public bool HasModelCache { get; init; }
}
public sealed record InspectionSettings
{
    public bool Enabled { get; init; }
    public int IntervalMinutes { get; init; } = 30;
}
/// <summary>Created only after explicit UI confirmation; scope must still match current cached targets.</summary>
public sealed record InspectionConsent(IReadOnlyList<InspectionTarget> Scope, int IntervalMinutes, DateTimeOffset AcceptedAt)
{
    /// <summary>Explicitly disclosed per-target attempt ceiling. Legacy consent defaults to one and cannot authorize retrying rounds.</summary>
    public int MaxAttempts { get; init; } = 1;
}

/// <summary>Shared round presentation rule: retain every attempt in history, display only the final completed attempt per exact target.</summary>
public static class InspectionRoundResults
{
    public const int MaxAttempts = 3;

    public static IReadOnlyList<ConnectionTestResult> FinalConnections(IEnumerable<ConnectionTestResult> history, Guid roundId) =>
        history.Where(r => r.RoundId == roundId && r.CompletedAt is not null &&
                r.Source is ConnectionRecordSource.Automatic or ConnectionRecordSource.RoundManual)
            .GroupBy(r => (r.ProfileId, r.ModelId, r.KeyRevision, r.BaseUrl, r.Protocol))
            .Select(g => g.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).First())
            .OrderByDescending(r => r.CompletedAt).ToArray();
}
public sealed record InspectionRoundSummary
{
    public Guid RoundId { get; init; } = Guid.NewGuid();
    public InspectionTrigger Trigger { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int PossibleSuccessCount { get; init; }
    public int UnknownCount { get; init; }
    public int Skipped { get; init; }
    public bool Cancelled { get; init; }
    public string? CurrentProfileName { get; init; }
    public string? CurrentModelId { get; init; }
}
public sealed record InspectionSnapshot(InspectionSettings Settings, IReadOnlyList<InspectionTargetStatus> Targets, InspectionRoundSummary? LatestRound)
{
    public IReadOnlyList<InspectionSelection> Selections { get; init; } = [];
}

public sealed record InspectionSelection(Guid ProfileId, bool IsSelected, IReadOnlyList<string> SelectedModelIds);
public interface IInspectionSelectionService
{
    Task<InspectionSnapshot> SaveInspectionSelectionsAsync(IReadOnlyList<InspectionSelection> selections, CancellationToken cancellationToken = default);
}
public interface IInspectionIntervalService
{
    Task<InspectionSnapshot> SaveInspectionIntervalAsync(int intervalMinutes, InspectionConsent? consent = null, CancellationToken cancellationToken = default);
}
public sealed record LatestModelResults(ConnectionTestResult? Connection, ProbeRecord? Probe);
public interface ILatestResultsService
{
    Task<LatestModelResults> GetLatestResultsAsync(Guid profileId, string modelId, CancellationToken cancellationToken = default);
}

public interface IGlobalInspectionService
{
    Task<InspectionSnapshot> GetInspectionSnapshotAsync(CancellationToken cancellationToken = default);
    /// <summary>Enabling or changing interval requires fresh exact-scope consent. Disabling never requires consent.</summary>
    Task<InspectionSnapshot> SaveInspectionSettingsAsync(InspectionSettings settings, InspectionConsent? consent = null, CancellationToken cancellationToken = default);
    /// <summary>Core owns bounded per-target connection retries, but no timer. Concurrent rounds are rejected with RoundBusy.</summary>
    Task<InspectionRoundSummary> RunInspectionRoundAsync(InspectionTrigger trigger = InspectionTrigger.Automatic, IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Bounded local query, newest first; history survives profile deletion.</summary>
    Task<IReadOnlyList<ConnectionTestResult>> GetConnectionHistoryAsync(CancellationToken cancellationToken = default);
    Task DeleteConnectionRecordAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task ClearConnectionHistoryAsync(CancellationToken cancellationToken = default);
    Task ExportConnectionHistoryAsync(string destinationPath, CancellationToken cancellationToken = default);
}
