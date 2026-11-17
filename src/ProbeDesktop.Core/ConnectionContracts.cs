namespace ProbeDesktop.Core;

public enum ConnectionTestStage { LocalValidationPassed, ApiReached, ModelResponded, Failed, Unknown, PossibleSuccess }

public enum ConnectionContextCheck { NotChecked, Passed, Mismatch, Inconclusive }

/// <summary>Local checks do not resolve DNS or send any network request.</summary>
public sealed record ConnectionValidationResult(bool Passed, string Code, string Message);

/// <summary>Only a bounded summary is retained, never the model response or response headers.</summary>
public sealed record ConnectionTestResult
{
    public Guid RecordId { get; init; } = Guid.NewGuid();
    public Guid? RoundId { get; init; }
    public ConnectionRecordSource Source { get; init; } = ConnectionRecordSource.Manual;
    public string ProfileName { get; init; } = "";
    public double? ElapsedMilliseconds { get; init; }
    public ApiProtocol Protocol { get; init; } = ApiProtocol.ChatCompletions;
    public Guid ProfileId { get; init; }
    public int KeyRevision { get; init; }
    public string BaseUrl { get; init; } = "";
    public string ModelId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public ConnectionTestStage Stage { get; init; }
    public bool LocalValidationPassed { get; init; }
    public bool ApiReached { get; init; }
    public bool ModelResponded { get; init; }
    public ConnectionContextCheck ContextCheck { get; init; }
    public int PromptVersion { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public int? HttpStatusCode { get; init; }
    public DateTimeOffset? RetryNotBefore { get; init; }
}

/// <summary>Enabled is session-only: a new DesktopService always starts with scheduling off.</summary>
public sealed record ConnectionTestSettings
{
    public ApiProtocol Protocol { get; init; } = ApiProtocol.ChatCompletions;
    public Guid ProfileId { get; init; }
    public string ModelId { get; init; } = "";
    public int IntervalMinutes { get; init; } = 60;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Enabled { get; init; }
    public int KeyRevision { get; init; }
    public string BaseUrl { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? ConsentAt { get; init; }
}

/// <summary>The UI must disclose repeated direct model requests, possible charges and the exact interval.</summary>
public sealed record ConnectionScheduleConsent(OutboundConsent Outbound, int IntervalMinutes);

public interface IDirectConnectionService
{
    Task<ConnectionValidationResult> ValidateConnectionAsync(Guid profileId, string modelId, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionAsync(Guid profileId, string modelId, OutboundConsent consent, CancellationToken cancellationToken = default);
    Task<ConnectionTestSettings> GetConnectionSettingsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ConnectionTestSettings> SaveConnectionSettingsAsync(ConnectionTestSettings settings, ConnectionScheduleConsent? consent = null, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult?> GetLastConnectionResultAsync(Guid profileId, CancellationToken cancellationToken = default);
    /// <summary>No timer is owned by Core. This checks session consent, current configuration, interval and Retry-After before sending.</summary>
    Task<ConnectionTestResult> TestScheduledConnectionAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public static class ConnectionTestEndpoints
{
    public static string Recipient(string baseUrl) => Recipient(baseUrl, ApiProtocol.ChatCompletions);
    public static string Recipient(string baseUrl, ApiProtocol protocol) => ModelApiEndpoints.Inference(baseUrl, protocol);
}
