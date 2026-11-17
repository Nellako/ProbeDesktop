using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProbeDesktop.Core;

public sealed partial class DesktopService : IMigrationService
{
    private readonly Guid _migrationOwner = Guid.NewGuid();
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private MigrationPreview? _migrationPreview;
    public bool MigrationRestartRequired => _store.MigrationRestartRequired;
    internal static bool IsMigrationActive(LocalRunState state) => IsActive(state);

    public async Task ExportMigrationAsync(string destinationPath, string password, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); MigrationPackage.ValidatePassword(password);
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? payload = null, package = null;
        string? temporary = null;
        try
        {
            string destination = ValidateMigrationDestination(destinationPath);
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StoreState snapshot = await _store.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                SanitizeMigrationSnapshot(snapshot);
                payload = JsonSerializer.SerializeToUtf8Bytes(new MigrationPayload { Profiles = snapshot.Profiles, Vault = snapshot.Vault }, MigrationValidation.Json);
                _ = MigrationValidation.Parse(payload);
            }
            finally { _store.Gate.Release(); }
            cancellationToken.ThrowIfCancellationRequested();
            package = MigrationPackage.Encrypt(payload, password);
            cancellationToken.ThrowIfCancellationRequested();
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".probe-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(package, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            // The rename is the success boundary: cancellation after it must not report a failed export.
        }
        catch (Exception ex) when (MigrationFailure(ex)) { throw MigrationError(); }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (package is not null) CryptographicOperations.ZeroMemory(package);
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _migrationGate.Release();
        }
    }

    public async Task<MigrationPreview> PreviewMigrationAsync(string sourcePath, string password, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); MigrationPackage.ValidatePassword(password);
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? package = null, payload = null;
        try
        {
            _migrationPreview?.Dispose(); _migrationPreview = null;
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _store.RequireEmptyAsync(cancellationToken).ConfigureAwait(false); }
            finally { _store.Gate.Release(); }
            string source = RequireLocalMigrationPath(sourcePath);
            using (FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous))
            {
                if (stream.Length is < MigrationPackage.Overhead or > MigrationPackage.MaximumPackageBytes) throw new JsonException();
                package = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(package, cancellationToken).ConfigureAwait(false);
                if (stream.ReadByte() != -1) throw new JsonException();
            }
            cancellationToken.ThrowIfCancellationRequested();
            payload = MigrationPackage.Decrypt(package, password);
            MigrationPayload state = MigrationValidation.Parse(payload);
            cancellationToken.ThrowIfCancellationRequested();
            MigrationPreview preview = new(_migrationOwner, payload, state);
            payload = null;
            _migrationPreview = preview;
            return preview;
        }
        catch (Exception ex) when (MigrationFailure(ex)) { throw MigrationError(); }
        finally
        {
            if (package is not null) CryptographicOperations.ZeroMemory(package);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            _migrationGate.Release();
        }
    }

    public async Task ImportMigrationAsync(MigrationPreview preview, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(preview);
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? payload = null;
        try
        {
            payload = preview.Take(_migrationOwner);
            MigrationPayload data = MigrationValidation.Parse(payload);
            StoreState state = new() { Profiles = data.Profiles, Vault = data.Vault };
            PrepareImportedState(state);
            await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _store.ImportAsync(state, cancellationToken).ConfigureAwait(false); }
            finally { _store.Gate.Release(); }
            // ImportAsync sets the write barrier immediately after commit. Nothing fallible follows.
        }
        catch (Exception ex) when (MigrationFailure(ex)) { throw MigrationError(); }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            _migrationGate.Release();
        }
    }

    private static void SanitizeMigrationSnapshot(StoreState state)
    {
        // Preserve keys, historical key set, IDs and relationships exactly. Only display summaries
        // are sanitized; never run convergence/retention while taking a migration snapshot.
        SensitiveText sanitizer = new(state.Vault.KnownSecrets.Concat(state.Vault.Secrets.Values.Select(s => s.Key)));
        state.Profiles = state.Profiles.Select(p => sanitizer.Profile(p) with
        { DefaultModelId = p.DefaultModelId, Models = p.Models.Select(m => m with { OwnedBy = sanitizer.Optional(m.OwnedBy, 256) }).ToList() }).ToList();
        state.Vault.History = state.Vault.History.Select(r => sanitizer.Record(r) with
        { ModelId = r.ModelId, RemoteRunId = r.RemoteRunId, ClaimedModel = r.ClaimedModel, BaseUrl = r.BaseUrl }).ToList();
        state.Vault.ConnectionHistory = state.Vault.ConnectionHistory.Select(r => SanitizeConnection(r, sanitizer) with { ModelId = r.ModelId, BaseUrl = r.BaseUrl }).ToList();
        state.Vault.ConnectionResults = state.Vault.ConnectionResults.ToDictionary(p => p.Key,
            p => SanitizeConnection(p.Value, sanitizer) with { ModelId = p.Value.ModelId, BaseUrl = p.Value.BaseUrl });
    }

    private static void PrepareImportedState(StoreState state)
    {
        SanitizeMigrationSnapshot(state);
        Vault v = state.Vault;
        v.InspectionSettings = v.InspectionSettings with { Enabled = false };
        v.IdentityInspectionSettings = v.IdentityInspectionSettings with { Enabled = false };
        v.InspectionGrants.Clear(); v.IdentityInspectionGrants.Clear(); v.InspectionNonce = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (v.LatestInspectionRound is { CompletedAt: null } round)
            v.LatestInspectionRound = round with { CompletedAt = now, Cancelled = true, CurrentProfileName = null, CurrentModelId = null,
                Skipped = Math.Max(round.Skipped, round.Total - round.Succeeded - round.Failed - round.PossibleSuccessCount - round.UnknownCount) };
        if (v.LatestIdentityInspectionRound is { CompletedAt: null } identity)
            v.LatestIdentityInspectionRound = identity with { CompletedAt = now, Cancelled = true, StopCode = "MigrationInterrupted",
                Attempts = identity.Attempts.Select(a => a.State == IdentityInspectionAttemptState.Pending
                    ? a with { State = IdentityInspectionAttemptState.Skipped, Code = "MigrationInterrupted" }
                    : a.State is IdentityInspectionAttemptState.IntentPersisted or IdentityInspectionAttemptState.Submitted
                        ? a with { State = IdentityInspectionAttemptState.Unknown, Code = "MigrationInterrupted" } : a).ToArray() };
        v.History = v.History.Select(r => IsMigrationActive(r.State) ? r with
        {
            State = string.IsNullOrEmpty(r.RemoteRunId) ? LocalRunState.SubmissionUnknown : LocalRunState.PollingInterrupted,
            ManualResumeRequired = true,
            Error = "从迁移包恢复的未完成任务，须手动确认后恢复查询；不会重新提交，亦不表示远端已取消。"
        } : r).ToList();
        ConnectionTestResult Interrupt(ConnectionTestResult r) => r.CompletedAt is null ? r with
        { CompletedAt = now, Stage = ConnectionTestStage.Unknown, ModelResponded = false, Code = "MigrationInterrupted", Message = "迁移时连接结果未知，不会重新发送。" } : r;
        v.ConnectionHistory = v.ConnectionHistory.Select(Interrupt).ToList();
        v.ConnectionResults = v.ConnectionResults.ToDictionary(p => p.Key, p => Interrupt(p.Value));
    }

    private static string RequireLocalMigrationPath(string path)
    {
        // Reject network/device syntax before any filesystem probe (including Exists).
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path)) throw new IOException();
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? throw new IOException();
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory ||
            full[root.Length..].Contains(':')) throw new IOException();
        // Refuse junctions/symlinks for reads too, rather than following an alias to a network or store path.
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        for (DirectoryInfo? current = new(Path.GetDirectoryName(full)!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        return full;
    }

    private string ValidateMigrationDestination(string path)
    {
        string destination = RequireLocalMigrationPath(path);
        string directory = Path.GetDirectoryName(destination) ?? throw new IOException();
        string store = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
        if (destination.Equals(store, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ClientOperationException("MigrationDestination", "迁移包不能保存在应用数据目录内，请另选本地目录。");
        // Reject reparse aliases and alternate streams, and never create destination directories.
        if (Path.GetFileName(destination).Contains(':') || File.Exists(destination) || Directory.Exists(destination))
            throw new ClientOperationException("MigrationDestination", "请选择尚不存在的新迁移文件，不能覆盖已有文件。");
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        return destination;
    }
    private static bool MigrationFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or CryptographicException or
        JsonException or FormatException or ArgumentException or OverflowException or System.Security.SecurityException;
    private static ClientOperationException MigrationError() => new("MigrationFailed", "迁移操作未完成：密码、文件格式或文件访问无效；未覆盖已有数据。请核对后重试。");
}

/// <summary>Binary format 1: magic, version, KDF id, cipher id, fixed iterations, salt, nonce, length;
/// all 56 header bytes are authenticated as GCM AAD. Followed by ciphertext and a 16-byte tag.</summary>
internal static class MigrationPackage
{
    private const int HeaderLength = 56;
    internal const int Overhead = HeaderLength + 16;
    internal const int MaximumPackageBytes = MigrationValidation.MaximumPayloadBytes + Overhead;
    private const int Iterations = 600_000;
    internal static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 12 or > 1024)
            throw new ClientOperationException("MigrationPassword", "迁移密码必须为 12 至 1024 个字符，空格会原样保留。");
    }
    private static byte[] Derive(string password, ReadOnlySpan<byte> salt)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static byte[] Encrypt(byte[] plaintext, string password)
    {
        if (plaintext.Length is 0 or > MigrationValidation.MaximumPayloadBytes) throw new JsonException();
        byte[] result = new byte[plaintext.Length + Overhead];
        byte[]? key = null;
        try
        {
            "PROBEMG1"u8.CopyTo(result);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), 1); // PBKDF2-HMAC-SHA256
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 1); // AES-256-GCM
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), Iterations);
            RandomNumberGenerator.Fill(result.AsSpan(24, 16));
            RandomNumberGenerator.Fill(result.AsSpan(40, 12));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(52), plaintext.Length);
            key = Derive(password, result.AsSpan(24, 16));
            using AesGcm aes = new(key, 16);
            aes.Encrypt(result.AsSpan(40, 12), plaintext, result.AsSpan(HeaderLength, plaintext.Length), result.AsSpan(result.Length - 16), result.AsSpan(0, HeaderLength));
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    internal static byte[] Decrypt(byte[] package, string password)
    {
        if (package.Length is < Overhead or > MaximumPackageBytes || !package.AsSpan(0, 8).SequenceEqual("PROBEMG1"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(8)) != 1 || BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(12)) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(16)) != 1 || BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(20)) != Iterations ||
            BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(52)) != package.Length - Overhead) throw new CryptographicException();
        byte[] plaintext = new byte[package.Length - Overhead];
        byte[]? key = null;
        try
        {
            key = Derive(password, package.AsSpan(24, 16));
            using AesGcm aes = new(key, 16);
            aes.Decrypt(package.AsSpan(40, 12), package.AsSpan(HeaderLength, plaintext.Length), package.AsSpan(package.Length - 16), plaintext, package.AsSpan(0, HeaderLength));
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}
