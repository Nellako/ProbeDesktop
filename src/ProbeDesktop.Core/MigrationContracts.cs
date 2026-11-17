using System.Security.Cryptography;

namespace ProbeDesktop.Core;

/// <summary>Local, password-encrypted complete migration. No method sends network requests.</summary>
public interface IMigrationService
{
    bool MigrationRestartRequired { get; }
    Task ExportMigrationAsync(string destinationPath, string password, CancellationToken cancellationToken = default);
    Task<MigrationPreview> PreviewMigrationAsync(string sourcePath, string password, CancellationToken cancellationToken = default);
    Task ImportMigrationAsync(MigrationPreview preview, CancellationToken cancellationToken = default);
}

/// <summary>Verified in-memory ticket, bound to its service, single use, expires after ten minutes.
/// Dispose when the preview dialog closes. No credentials or addresses are exposed.</summary>
public sealed class MigrationPreview : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _payload;
    private readonly Timer _expiry;
    internal Guid Owner { get; }
    public DateTimeOffset ExportedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public int ProfileCount { get; }
    public int ProbeRecordCount { get; }
    public int ConnectionRecordCount { get; }
    public bool HasUnfinishedWork { get; }

    internal MigrationPreview(Guid owner, byte[] payload, MigrationPayload state)
    {
        Owner = owner;
        _payload = payload;
        ExportedAt = state.ExportedAt;
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        ProfileCount = state.Profiles.Count;
        ProbeRecordCount = state.Vault.History.Count;
        ConnectionRecordCount = state.Vault.ConnectionHistory.Count;
        HasUnfinishedWork = state.Vault.History.Any(r => r.ManualResumeRequired || DesktopService.IsMigrationActive(r.State)) ||
            state.Vault.LatestInspectionRound is { CompletedAt: null } || state.Vault.LatestIdentityInspectionRound is { CompletedAt: null };
        _expiry = new Timer(_ => Dispose(), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
    }

    internal byte[] Take(Guid owner)
    {
        lock (_gate)
        {
            if (Owner != owner || _payload is null || DateTimeOffset.UtcNow >= ExpiresAt)
                throw new ClientOperationException("MigrationPreviewExpired", "迁移预览已失效，请重新选择文件并验证。");
            byte[] bytes = _payload;
            _payload = null;
            _expiry.Dispose();
            return bytes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_payload is not null) CryptographicOperations.ZeroMemory(_payload);
            _payload = null;
            _expiry.Dispose();
        }
    }
}
