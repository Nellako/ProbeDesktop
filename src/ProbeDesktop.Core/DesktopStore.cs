using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProbeDesktop.Core;

internal sealed class DesktopStore
{
    internal const int SchemaVersion = 3;
    internal bool MigrationRestartRequired { get; private set; }
    internal void RequireWritable()
    {
        if (MigrationRestartRequired)
            throw new ClientOperationException("MigrationRestartRequired", "迁移数据已保存，请重启应用后继续；当前实例不会再写入数据。");
    }
    private const int MaximumFileBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ISecretProtector _protector;
    private readonly string _path;
    private bool _initialized;
    internal string DirectoryPath { get; }
    internal SemaphoreSlim Gate { get; }

    internal DesktopStore(string directory, ISecretProtector protector)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _path = Path.Combine(DirectoryPath, "desktop-state.json");
        _protector = protector;
        Gate = Gates.GetOrAdd(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar), _ => new SemaphoreSlim(1, 1));
        try
        {
            // Validate every existing ancestor before creating anything or changing permissions.
            for (DirectoryInfo? current = new(DirectoryPath); current is not null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存储路径不能包含重解析点。");
            if (Directory.Exists(DirectoryPath))
            {
                string[] entries = Directory.GetFileSystemEntries(DirectoryPath);
                bool recognizedStore = File.Exists(_path) || File.Exists(_path + ".bak");
                if (entries.Length != 0 && !recognizedStore && !entries.All(IsApplicationTemporaryFile))
                    throw new ClientOperationException("StorageSecurity",
                        "所选数据目录已包含非本应用文件；为避免修改已有目录权限，请选择空目录或已有 ProbeDesktop 数据目录。");
            }
            Directory.CreateDirectory(DirectoryPath);
            if ((new DirectoryInfo(DirectoryPath).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存储路径不能包含重解析点。");
            if (OperatingSystem.IsWindows())
            {
                RestrictDirectory(DirectoryPath);
                foreach (string existingFile in new[] { _path, _path + ".bak" })
                    if (File.Exists(existingFile)) RestrictFile(existingFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ClientOperationException("StorageSecurity", "无法建立仅当前用户可访问的本地存储目录。");
        }
    }

    private static bool IsApplicationTemporaryFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        string name = Path.GetFileName(path);
        const string prefix = "desktop-state.json.";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
            name.Length == prefix.Length + 32 + 4 && Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictDirectory(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException();
        DirectorySecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException();
        FileSecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    internal async Task<StoreState> LoadAsync(CancellationToken cancellationToken)
    {
        RequireWritable();
        StoreState state;
        bool hasStoredData = File.Exists(_path) || File.Exists(_path + ".bak");
        if (!hasStoredData) state = new();
        else
        {
            try { state = await ReadAsync(_path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (CanRecover(ex))
            {
                try
                {
                    state = await ReadAsync(_path + ".bak", cancellationToken).ConfigureAwait(false);
                    state.RecoveredFromBackup = true;
                }
                catch (Exception backupError) when (CanRecover(backupError))
                {
                    throw new ClientOperationException("StorageRead", "本地数据及备份无法读取或解密。请保留文件；不要覆盖现有凭据。");
                }
            }
        }
        bool recovered = state.RecoveredFromBackup;
        bool upgraded = UpgradeInspectionSettings(state.Vault);
        if (!_initialized || state.IsLegacy)
        {
            bool changed = upgraded && hasStoredData;
            if (state.Vault.LatestInspectionRound is { CompletedAt: null } interruptedConnection && !DesktopService.HasActiveConnectionInspection(DirectoryPath))
            {
                state.Vault.LatestInspectionRound = interruptedConnection with
                { CompletedAt = DateTimeOffset.UtcNow, Cancelled = true, CurrentModelId = null, CurrentProfileName = null };
                changed = true;
            }
            if (state.Vault.LatestIdentityInspectionRound is { CompletedAt: null } interrupted)
            {
                state.Vault.LatestIdentityInspectionRound = interrupted with
                {
                    CompletedAt = DateTimeOffset.UtcNow, StopCode = "ApplicationInterrupted",
                    Attempts = interrupted.Attempts.Select(a => a.State == IdentityInspectionAttemptState.Pending
                        ? a with { State = IdentityInspectionAttemptState.Skipped, Code = "ApplicationInterrupted" }
                        : a.State is IdentityInspectionAttemptState.IntentPersisted or IdentityInspectionAttemptState.Submitted
                            ? a with { State = IdentityInspectionAttemptState.Unknown, Code = "ApplicationInterrupted" } : a).ToArray()
                };
                changed = true;
            }
            for (int i = 0; i < state.Vault.History.Count; i++)
            {
                ProbeRecord record = state.Vault.History[i];
                if (record.State is LocalRunState.Submitting or LocalRunState.Running)
                {
                    state.Vault.History[i] = record with
                    {
                        State = record.State == LocalRunState.Submitting ? LocalRunState.SubmissionUnknown : LocalRunState.PollingInterrupted,
                        Error = record.State == LocalRunState.Submitting
                            ? "应用上次在提交期间中断；结果未知，请勿自动重新提交。"
                            : "上次轮询已中断，可恢复查询；不会重新提交任务。"
                    };
                    changed = true;
                }
            }
            if ((changed && !state.IsLegacy) || state.RecoveredFromBackup) await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        else if (recovered || (upgraded && hasStoredData && !state.IsLegacy)) await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (recovered)
            throw new ClientOperationException("StorageRecovered",
                "主存储损坏，已恢复上次可解密备份。备份可能缺少最近的密钥更新或提交记录；请重新加载并核对档案和历史，不要自动重新提交检测。");
        return state;
    }

    // Version zero is the old authorization contract, even when its attempt limit equals the new default.
    internal static bool UpgradeInspectionSettings(Vault vault)
    {
        bool changed = false;
        var now = DateTimeOffset.UtcNow;
        if (vault.InspectionSettingsVersion == 0)
        {
            vault.InspectionUpgradePending = vault.InspectionSettings.Enabled || vault.InspectionGrants.Count != 0 ||
                vault.InspectionSelections.Any(s => s.IsSelected && s.SelectedModelIds.Count != 0);
            vault.InspectionGrants.Clear();
            vault.InspectionSettings = vault.InspectionSettings with { MaxRetries = 0 };
            if (vault.LatestInspectionRound is { Cancelled: false, CompletedAt: { } ended } round && ended >= round.StartedAt && ended <= now)
            {
                long ran = (long)round.Succeeded + round.PossibleSuccessCount + round.Failed + round.UnknownCount;
                if (ran > 0 && ran + round.Skipped == round.Total) vault.LastInspectionCompletedAt = ended;
            }
            vault.InspectionSettingsVersion = 1;
            changed = true;
        }
        if (vault.IdentityInspectionSettingsVersion == 0)
        {
            vault.IdentityInspectionUpgradePending = vault.IdentityInspectionSettings.Enabled || vault.IdentityInspectionGrants.Count != 0 ||
                vault.IdentityInspectionSelections.Any(s => s.IsSelected && s.SelectedModelIds.Count != 0);
            vault.IdentityInspectionGrants.Clear();
            vault.LastIdentityInspectionCompletedAt = DesktopService.TrustedLegacyIdentityInspectionCompletedAt(vault);
            vault.IdentityInspectionSettingsVersion = 1;
            changed = true;
        }
        return changed;
    }

    private async Task<StoreState> ReadAsync(string path, CancellationToken cancellationToken, bool migrationRead = false)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        FileInfo info = new(path);
        if (info.Length > MaximumFileBytes) throw new IOException();
        byte[] encoded = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument metadata = JsonDocument.Parse(encoded);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
                !metadata.RootElement.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema))
                throw new ClientOperationException("StorageVersion", "本地文件缺少有效版本，已拒绝读取及覆盖。");
            if (schema is not (1 or 2 or SchemaVersion))
                throw new ClientOperationException("StorageVersion", "本地文件版本不受支持，请使用兼容版本；现有文件不会被覆盖。");
            HashSet<string> envelopeFields = new(StringComparer.Ordinal);
            foreach (JsonProperty field in metadata.RootElement.EnumerateObject())
                if ((field.Name != "schemaVersion" && field.Name != "profiles" && field.Name != "protectedState") || !envelopeFields.Add(field.Name))
                    throw new ClientOperationException("StorageIntegrity", "存储包含未知或重复字段，已拒绝读取及备份回退。");
            if (!metadata.RootElement.TryGetProperty("profiles", out JsonElement profileElements) ||
                profileElements.ValueKind != JsonValueKind.Array ||
                !metadata.RootElement.TryGetProperty("protectedState", out JsonElement protectedState) ||
                protectedState.ValueKind != JsonValueKind.String)
                throw new ClientOperationException("StorageIntegrity", "存储缺少必要字段或字段结构不兼容，已拒绝读取及备份回退。");
            if (schema == 1) ValidateLegacyProfiles(profileElements);
            else
            {
                foreach (JsonElement profile in profileElements.EnumerateArray())
                {
                    if (profile.ValueKind != JsonValueKind.Object)
                        throw new ClientOperationException("StorageIntegrity", "档案字段结构不兼容，已拒绝读取及备份回退。");
                    HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty field in profile.EnumerateObject())
                    {
                        if (!fields.Add(field.Name))
                            throw new ClientOperationException("StorageIntegrity", "档案包含重复字段，已拒绝读取及备份回退。");
                        if (!field.Name.Equals("protocol", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if (!Enum.IsDefined(field.Value.Deserialize<ApiProtocol>(Json))) throw new JsonException();
                        }
                        catch (JsonException)
                        {
                            throw new ClientOperationException("StorageProtocol", "本地档案包含未知协议，已拒绝读取及覆盖。");
                        }
                    }
                }
            }
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(encoded, Json) ?? throw new JsonException();
            if (envelope.Profiles is null) throw new JsonException();
            string expectedHash;
            if (schema == 1)
            {
                JsonElement profiles = metadata.RootElement.GetProperty("profiles");
                List<LegacyProfile> legacy = profiles.Deserialize<List<LegacyProfile>>(Json) ?? throw new JsonException();
                expectedHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy, Json)));
            }
            else expectedHash = HashProfiles(envelope.Profiles);
            byte[] ciphertext = Convert.FromBase64String(envelope.ProtectedState);
            byte[]? plaintext = null;
            try
            {
                plaintext = _protector.Unprotect(ciphertext);
                using JsonDocument vaultDocument = JsonDocument.Parse(plaintext);
                if (schema == SchemaVersion || migrationRead)
                {
                    try
                    {
                        MigrationValidation.ValidateShape(profileElements, typeof(List<RelayProfile>), requireAll: schema == SchemaVersion);
                        MigrationValidation.ValidateShape(vaultDocument.RootElement, typeof(Vault), requireAll: schema == SchemaVersion);
                    }
                    catch (JsonException)
                    {
                        throw new ClientOperationException("StorageIntegrity", "本地字段结构不兼容，已拒绝读取及备份回退；现有文件不会被覆盖。");
                    }
                }
                using MemoryStream migrated = new();
                using (Utf8JsonWriter writer = new(migrated))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in vaultDocument.RootElement.EnumerateObject())
                    {
                        if (!property.Name.Equals("notificationMemory", StringComparison.OrdinalIgnoreCase)) { property.WriteTo(writer); continue; }
                        writer.WritePropertyName(property.Name); writer.WriteStartArray();
                        foreach (JsonElement item in property.Value.EnumerateArray())
                        {
                            if (item.TryGetProperty("category", out var category) &&
                                ((category.ValueKind == JsonValueKind.String && string.Equals(category.GetString(), "Capability", StringComparison.OrdinalIgnoreCase)) ||
                                 (category.ValueKind == JsonValueKind.Number && category.TryGetInt32(out int number) && number == 1))) continue;
                            item.WriteTo(writer);
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                Vault vault;
                try { vault = JsonSerializer.Deserialize<Vault>(migrated.GetBuffer().AsSpan(0, checked((int)migrated.Length)), Json) ?? throw new JsonException(); }
                finally { CryptographicOperations.ZeroMemory(migrated.GetBuffer()); }
                if (envelope.Profiles is null || vault.Secrets is null || vault.KnownSecrets is null || vault.History is null ||
                    vault.ConnectionSettings is null || vault.ConnectionResults is null || vault.ConnectionHistory is null ||
                    vault.InspectionSettings is null || vault.InspectionGrants is null || vault.InspectionSelections is null || vault.InspectionHostDeadlines is null ||
                    vault.IdentityInspectionSettings is null || vault.IdentityInspectionSelections is null || vault.IdentityInspectionGrants is null || vault.NotificationPreferences is null || vault.NotificationMemory is null) throw new JsonException();
                vault.NotificationMemory.RemoveAll(m => m is not null && !Enum.IsDefined(m.Category));
                if (vault.IdentityInspectionSettings.IntervalMinutes is < 10 or > 480 || vault.InspectionSettings.IntervalMinutes is < 10 or > 480 ||
                    vault.InspectionSettings.MaxRetries is < 0 or > 2 || vault.InspectionSettingsVersion is not (0 or 1) ||
                    vault.IdentityInspectionSettingsVersion is not (0 or 1) ||
                    vault.LastInspectionCompletedAt > DateTimeOffset.MaxValue.AddMinutes(-480) || vault.LastIdentityInspectionCompletedAt > DateTimeOffset.MaxValue.AddMinutes(-480) ||
                    vault.IdentityInspectionSelections.Any(s => s is null || s.SelectedModelIds is null || s.SelectedModelIds.Any(m => m is null)) ||
                    vault.IdentityInspectionSelections.Select(s => s.ProfileId).Distinct().Count() != vault.IdentityInspectionSelections.Count ||
                    vault.IdentityInspectionGrants.Any(g => g is null || g.Target is null || !Enum.IsDefined(g.Target.Protocol)) ||
                    vault.NotificationMemory.Any(m => m is null || m.Target is null || !Enum.IsDefined(m.Target.Protocol))) throw new JsonException();
                if (vault.LatestIdentityInspectionRound is { } identityRound &&
                    (!Enum.IsDefined(identityRound.Trigger) || identityRound.Attempts is null || identityRound.PausedProfileIds is null ||
                     identityRound.Attempts.Any(a => a is null || a.Target is null || !Enum.IsDefined(a.Target.Protocol) || !Enum.IsDefined(a.State)) ||
                     identityRound.Attempts.Select(a => a.AttemptId).Distinct().Count() != identityRound.Attempts.Count ||
                     identityRound.Attempts.Select(a => a.Target).Distinct().Count() != identityRound.Attempts.Count)) throw new JsonException();
                if (vault.InspectionSelections.Any(s => s is null || s.SelectedModelIds is null || s.SelectedModelIds.Any(m => m is null)) ||
                    vault.InspectionSelections.Select(s => s.ProfileId).Distinct().Count() != vault.InspectionSelections.Count) throw new JsonException();
                if (vault.MetadataHash != expectedHash)
                    throw new ClientOperationException("StorageIntegrity", "档案元数据完整性校验失败，已拒绝读取及备份回退；现有文件不会被覆盖。");
                if (envelope.Profiles.Any(p => !Enum.IsDefined(p.Protocol)) || vault.History.Any(r => !Enum.IsDefined(r.Protocol)) ||
                    vault.ConnectionSettings.Values.Any(r => !Enum.IsDefined(r.Protocol)) || vault.ConnectionResults.Values.Any(r => !Enum.IsDefined(r.Protocol)) ||
                    vault.ConnectionHistory.Any(r => !Enum.IsDefined(r.Protocol)) || vault.InspectionGrants.Any(g => g is null || g.Target is null || g.Target.Models is null || !Enum.IsDefined(g.Target.Protocol)))
                    throw new ClientOperationException("StorageProtocol", "本地数据包含未知协议，已拒绝读取及覆盖。");
                if (envelope.Profiles.Select(p => p.Id).Distinct().Count() != envelope.Profiles.Count) throw new JsonException();
                foreach (RelayProfile profile in envelope.Profiles)
                    if (!vault.Secrets.TryGetValue(profile.Id, out SecretEntry? secret) || secret is null ||
                        secret.Revision != profile.KeyRevision || secret.Reference != profile.SecretRef || string.IsNullOrEmpty(secret.Key))
                        throw new JsonException();
                return new StoreState { Profiles = envelope.Profiles, Vault = vault, IsLegacy = schema == 1 };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    internal Task SaveAsync(StoreState state, CancellationToken cancellationToken) => CommitAsync(state, cancellationToken, applyRetention: true);

    // These migration reads deliberately do not recover backups, initialize or repair any state.
    // An unreadable primary (including a backup-only store) is never evidence of an empty target.
    internal async Task<StoreState> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        RequireWritable();
        if (!File.Exists(_path) && !File.Exists(_path + ".bak")) return new();
        try { return await ReadAsync(_path, cancellationToken, migrationRead: true).ConfigureAwait(false); }
        catch (Exception ex) when (CanRecover(ex))
        { throw new ClientOperationException("MigrationStorageRead", "无法安全读取迁移所需的本地数据；未修改现有文件。"); }
    }

    internal async Task RequireEmptyAsync(CancellationToken cancellationToken)
    {
        StoreState state = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Vault v = state.Vault;
        if (state.Profiles.Count != 0 || v.Secrets.Count != 0 || v.KnownSecrets.Count != 0 || v.History.Count != 0 ||
            v.ConnectionSettings.Count != 0 || v.ConnectionResults.Count != 0 || v.ConnectionHistory.Count != 0 ||
            v.InspectionSelections.Count != 0 || v.InspectionGrants.Count != 0 || v.InspectionHostDeadlines.Count != 0 ||
            v.IdentityInspectionSelections.Count != 0 || v.IdentityInspectionGrants.Count != 0 || v.NotificationMemory.Count != 0 ||
            v.LatestInspectionRound is not null || v.LatestIdentityInspectionRound is not null ||
            v.LastInspectionCompletedAt is not null || v.LastIdentityInspectionCompletedAt is not null ||
            v.InspectionSettings != new InspectionSettings() || v.IdentityInspectionSettings != new IdentityInspectionSettings() ||
            v.NotificationPreferences != new NotificationPreferences())
            throw new ClientOperationException("MigrationTargetNotEmpty", "只能向逻辑空库导入；当前目录含有业务数据或非默认设置。");
    }

    internal async Task ImportAsync(StoreState state, CancellationToken cancellationToken)
    {
        await RequireEmptyAsync(cancellationToken).ConfigureAwait(false);
        await CommitAsync(state, cancellationToken, applyRetention: false).ConfigureAwait(false);
        // No cancellation check or fallible work after the atomic commit.
        MigrationRestartRequired = true;
    }

    private async Task CommitAsync(StoreState state, CancellationToken cancellationToken, bool applyRetention)
    {
        RequireWritable();
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plaintext = null, ciphertext = null, encoded = null;
        try
        {
            if (applyRetention)
            {
                DesktopService.TrimConnectionHistory(state);
                DesktopService.ConvergeIdentityInspectionState(state);
                state.Vault.NotificationMemory = state.Vault.NotificationMemory.OrderByDescending(m => m.UpdatedAt).Take(4000).ToList();
            }
            state.Vault.MetadataHash = HashProfiles(state.Profiles);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(state.Vault, Json);
            ciphertext = _protector.Protect(plaintext);
            Envelope envelope = new() { Profiles = state.Profiles, ProtectedState = Convert.ToBase64String(ciphertext) };
            encoded = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (encoded.Length > MaximumFileBytes)
                throw new ClientOperationException("StorageFull", "本地历史文件已达容量上限，请先导出并删除不需要的记录。");
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // The single envelope is the transaction: metadata, key revisions and encrypted history commit together.
            // A failed write/protection never deletes or rewrites the previously committed key.
            if (state.RecoveredFromBackup)
            {
                File.Move(temporary, _path, overwrite: true); // Preserve the known-good backup, not the corrupt primary.
                state.RecoveredFromBackup = false;
            }
            else if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ClientOperationException("StorageWrite", "无法安全保存本地数据；先前已提交的数据未主动删除，请检查磁盘、权限与 Windows 用户。");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool CanRecover(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException;
    private static string HashProfiles(List<RelayProfile> profiles) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profiles, Json)));

    private static void ValidateLegacyProfiles(JsonElement profiles)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal)
        { "id", "name", "baseUrl", "secretRef", "keyRevision", "defaultModelId", "models", "modelsFetchedAt", "createdAt", "updatedAt" };
        if (profiles.ValueKind != JsonValueKind.Array) throw new JsonException();
        foreach (JsonElement profile in profiles.EnumerateArray())
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            if (profile.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (JsonProperty property in profile.EnumerateObject())
                if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                    throw new ClientOperationException("StorageIntegrity", "旧版档案含未知或重复字段，已拒绝读取及备份回退，现有文件不会被覆盖。");
            if (!seen.SetEquals(allowed)) throw new JsonException();
            if (!profile.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object) throw new JsonException();
                HashSet<string> modelFields = new(StringComparer.Ordinal);
                foreach (JsonProperty field in model.EnumerateObject())
                    if ((field.Name != "id" && field.Name != "ownedBy") || !modelFields.Add(field.Name))
                        throw new ClientOperationException("StorageIntegrity", "旧版模型缓存包含未知或重复字段，已拒绝读取。");
            }
        }
    }

    // Property order and serializer options exactly match the schema-1 RelayProfile hash contract.
    private sealed class LegacyProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string SecretRef { get; set; } = "";
        public int KeyRevision { get; set; }
        public string DefaultModelId { get; set; } = "";
        public List<ModelEntry> Models { get; set; } = [];
        public DateTimeOffset? ModelsFetchedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = DesktopStore.SchemaVersion;
        public List<RelayProfile> Profiles { get; set; } = [];
        public string ProtectedState { get; set; } = "";
    }
}

internal sealed class StoreState
{
    public List<RelayProfile> Profiles { get; set; } = [];
    public Vault Vault { get; set; } = new();
    public bool RecoveredFromBackup { get; set; }
    public bool IsLegacy { get; set; }
}

internal sealed class Vault
{
    public string MetadataHash { get; set; } = "";
    public Dictionary<Guid, SecretEntry> Secrets { get; set; } = [];
    // Historical secrets remain encrypted for redaction even after rotation/deletion.
    public HashSet<string> KnownSecrets { get; set; } = new(StringComparer.Ordinal);
    public List<ProbeRecord> History { get; set; } = [];
    public Dictionary<Guid, ConnectionTestSettings> ConnectionSettings { get; set; } = [];
    public Dictionary<Guid, ConnectionTestResult> ConnectionResults { get; set; } = [];
    public List<ConnectionTestResult> ConnectionHistory { get; set; } = [];
    public IdentityInspectionSettings IdentityInspectionSettings { get; set; } = new();
    public List<IdentityInspectionSelection> IdentityInspectionSelections { get; set; } = [];
    public List<IdentityInspectionGrant> IdentityInspectionGrants { get; set; } = [];
    public IdentityInspectionRoundSummary? LatestIdentityInspectionRound { get; set; }
    public DateTimeOffset? LastIdentityInspectionCompletedAt { get; set; }
    public int IdentityInspectionSettingsVersion { get; set; }
    public Guid IdentityInspectionSettingsRevision { get; set; }
    public bool IdentityInspectionUpgradePending { get; set; }
    public NotificationPreferences NotificationPreferences { get; set; } = new();
    public List<NotificationMemory> NotificationMemory { get; set; } = [];
    public InspectionSettings InspectionSettings { get; set; } = new();
    public List<InspectionGrant> InspectionGrants { get; set; } = [];
    public List<InspectionSelection> InspectionSelections { get; set; } = [];
    public Dictionary<string, DateTimeOffset> InspectionHostDeadlines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Guid InspectionNonce { get; set; } = Guid.NewGuid();
    public InspectionRoundSummary? LatestInspectionRound { get; set; }
    public DateTimeOffset? LastInspectionCompletedAt { get; set; }
    public int InspectionSettingsVersion { get; set; }
    public Guid InspectionSettingsRevision { get; set; }
    public bool InspectionUpgradePending { get; set; }
}

internal sealed record SecretEntry(string Key, int Revision, string Reference);
