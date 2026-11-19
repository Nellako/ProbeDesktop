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
    public int MaxRetries { get; init; }
}
/// <summary>Created only after explicit UI confirmation; scope must still match current cached targets.</summary>
public sealed record InspectionConsent(IReadOnlyList<InspectionTarget> Scope, int IntervalMinutes, DateTimeOffset AcceptedAt)
{
    /// <summary>Explicit per-target request ceiling, including the initial attempt.</summary>
    public int MaxAttempts { get; init; } = 1;
    public Guid SettingsRevision { get; init; }
}

/// <summary>Retain every attempt; represent each exact target by its best completed result in this round, newest on ties.</summary>
public static class InspectionRoundResults
{
    public const int MaxAttempts = 3;

    /// <summary>Connection outcome priority only; marker checks do not change connection success. Raw Unknown remains a failure for presentation.</summary>
    public static int OutcomeRank(ConnectionTestResult result) => result.Stage switch
    {
        ConnectionTestStage.ModelResponded => 3,
        ConnectionTestStage.PossibleSuccess => 2,
        ConnectionTestStage.Failed or ConnectionTestStage.Unknown => 1,
        _ => result.ModelResponded ? 3 : 0
    };

    /// <summary>Callers supply attempts from one round and one exact target. Return the original record, including its own marker check.</summary>
    public static ConnectionTestResult? BestCompleted(IEnumerable<ConnectionTestResult> attempts) => attempts
        .Where(r => r.CompletedAt is not null)
        .OrderByDescending(OutcomeRank).ThenByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt)
        .FirstOrDefault();

    // Choose the newest attempt/round first, then rank only its exact target. Never let an older round's success mask a new failure.
    internal static ConnectionTestResult RepresentativeForLatest(IEnumerable<ConnectionTestResult> history, ConnectionTestResult latest) =>
        latest.RoundId is { } roundId
            ? BestCompleted(history.Where(r => r.RoundId == roundId && r.ProfileId == latest.ProfileId &&
                r.ModelId == latest.ModelId && r.KeyRevision == latest.KeyRevision && r.BaseUrl == latest.BaseUrl &&
                r.Protocol == latest.Protocol && r.Source == latest.Source && r.Stage != ConnectionTestStage.LocalValidationPassed)) ?? latest
            : latest;

    /// <summary>Compatibility name: the final representative is the best completed attempt, not necessarily the last attempt.</summary>
    public static IReadOnlyList<ConnectionTestResult> FinalConnections(IEnumerable<ConnectionTestResult> history, Guid roundId) =>
        history.Where(r => r.RoundId == roundId && r.CompletedAt is not null &&
                r.Source is ConnectionRecordSource.Automatic or ConnectionRecordSource.RoundManual)
            .GroupBy(r => (r.ProfileId, r.ModelId, r.KeyRevision, r.BaseUrl, r.Protocol))
            .Select(g => BestCompleted(g)!)
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
    /// <summary>Legacy persisted counter. New rounds include unconfirmed/Unknown outcomes in Failed.</summary>
    public int UnknownCount { get; init; }
    public int Skipped { get; init; }
    public bool Cancelled { get; init; }
    public int? IntervalMinutes { get; init; }
    public int? MaxRetries { get; init; }
    public string? CurrentProfileName { get; init; }
    public string? CurrentModelId { get; init; }
}
public sealed record InspectionSnapshot(InspectionSettings Settings, IReadOnlyList<InspectionTargetStatus> Targets, InspectionRoundSummary? LatestRound)
{
    public IReadOnlyList<InspectionSelection> Selections { get; init; } = [];
    public Guid SettingsRevision { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    // Only set when every selected, authorized target is cooling down.
    public DateTimeOffset? RetryNotBefore { get; init; }
    public bool NeedsUpgradeConfirmation { get; init; }
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
    /// <summary>Latest completed connection attempt's round/target, represented by its best completed attempt; standalone calls remain independent. Probe ordering is unchanged.</summary>
    Task<LatestModelResults> GetLatestResultsAsync(Guid profileId, string modelId, CancellationToken cancellationToken = default);
}

public interface IGlobalInspectionService
{
    Task<InspectionSnapshot> GetInspectionSnapshotAsync(CancellationToken cancellationToken = default);
    /// <summary>Sets only the switch. It cannot change the confirmed settings or grant permission.</summary>
    Task<InspectionSnapshot> SaveInspectionSettingsAsync(InspectionSettings settings, InspectionConsent? consent = null, CancellationToken cancellationToken = default);
    Task<InspectionSnapshot> ConfirmInspectionSettingsAsync(IReadOnlyList<InspectionSelection> selections, InspectionConsent consent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>Core owns bounded per-target connection retries, but no timer. Concurrent rounds are rejected with RoundBusy.</summary>
    Task<InspectionRoundSummary> RunInspectionRoundAsync(InspectionTrigger trigger = InspectionTrigger.Automatic, IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default);
    Task<InspectionRoundSummary> RunDueInspectionRoundAsync(IProgress<InspectionRoundSummary>? progress = null, CancellationToken cancellationToken = default) =>
        RunInspectionRoundAsync(InspectionTrigger.Automatic, progress, cancellationToken);
    /// <summary>Bounded local query, newest first; history survives profile deletion.</summary>
    Task<IReadOnlyList<ConnectionTestResult>> GetConnectionHistoryAsync(CancellationToken cancellationToken = default);
    Task DeleteConnectionRecordAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task ClearConnectionHistoryAsync(CancellationToken cancellationToken = default);
    Task ExportConnectionHistoryAsync(string destinationPath, CancellationToken cancellationToken = default);
}
