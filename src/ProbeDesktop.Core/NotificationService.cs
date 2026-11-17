namespace ProbeDesktop.Core;

public enum NotificationCategory { Connection = 0, Identity = 2 }
public sealed record NotificationTarget(Guid ProfileId, int KeyRevision, string BaseUrl, ApiProtocol Protocol, string ModelId);
public enum NotificationObservationState { Unknown, Healthy, Problem }
public sealed record NotificationPreferences
{
    public bool Connection { get; init; } = true;
    public bool Identity { get; init; } = true;
}
/// <summary>ProblemType must be a stable non-sensitive code, never response text. Only automatic observations may notify.</summary>
public sealed record NotificationObservation(NotificationCategory Category, NotificationTarget Target, bool Automatic, NotificationObservationState State, string? ProblemType = null);
public sealed record NotificationDecision(bool ShouldNotify, NotificationCategory Category, NotificationTarget Target, string? ProblemType);
public interface INotificationService
{
    Task<NotificationPreferences> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default);
    Task<NotificationPreferences> SaveNotificationPreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default);
    Task<NotificationDecision> ObserveNotificationAsync(NotificationObservation observation, CancellationToken cancellationToken = default);
}
internal sealed record NotificationMemory(NotificationCategory Category, NotificationTarget Target, string? ProblemType, DateTimeOffset UpdatedAt);
public sealed partial class DesktopService : INotificationService
{
    private static bool NotificationProfileMatches(NotificationTarget t, RelayProfile p) => t.ProfileId == p.Id && t.KeyRevision == p.KeyRevision && t.BaseUrl == p.BaseUrl && t.Protocol == p.Protocol;
    public async Task<NotificationPreferences> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await _store.LoadAsync(cancellationToken).ConfigureAwait(false)).Vault.NotificationPreferences; }
        finally { _store.Gate.Release(); }
    }
    public async Task<NotificationPreferences> SaveNotificationPreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences); ThrowIfDisposed(); await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); state.Vault.NotificationPreferences = preferences; await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false); return preferences; }
        finally { _store.Gate.Release(); }
    }
    public async Task<NotificationDecision> ObserveNotificationAsync(NotificationObservation observation, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(observation.Category) || !Enum.IsDefined(observation.State)) throw new ClientOperationException("InvalidNotification", "通知类别无效。");
        if (observation.State == NotificationObservationState.Problem && (string.IsNullOrEmpty(observation.ProblemType) || observation.ProblemType.Length > 80 || observation.ProblemType.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))) throw new ClientOperationException("InvalidNotification", "问题类型须为稳定代码。");
        NotificationDecision decision = new(false, observation.Category, observation.Target, observation.ProblemType);
        if (!observation.Automatic || observation.State == NotificationObservationState.Unknown) return decision;
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!state.Profiles.Any(p => NotificationProfileMatches(observation.Target, p))) throw new ClientOperationException("NotificationTargetChanged", "通知目标已变化。");
            NotificationMemory? previous = state.Vault.NotificationMemory.Find(m => m.Category == observation.Category && m.Target == observation.Target);
            string? problem = observation.State == NotificationObservationState.Healthy ? null : observation.ProblemType;
            bool enabled = observation.Category switch { NotificationCategory.Connection => state.Vault.NotificationPreferences.Connection, _ => state.Vault.NotificationPreferences.Identity };
            bool notify = enabled && problem is not null && (previous is null || previous.ProblemType != problem);
            state.Vault.NotificationMemory.RemoveAll(m => m.Category == observation.Category && m.Target == observation.Target);
            state.Vault.NotificationMemory.Add(new(observation.Category, observation.Target, problem, DateTimeOffset.UtcNow));
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return decision with { ShouldNotify = notify };
        }
        finally { _store.Gate.Release(); }
    }
}
