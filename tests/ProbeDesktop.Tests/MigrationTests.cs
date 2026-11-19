using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class MigrationTests
{
    private const string Password = "  migration-password-123  ";
    private sealed class DeviceProtector(byte marker) : ISecretProtector
    {
        public bool Fail { get; set; }
        public Action? BeforeProtect { get; set; }
        public byte[] Protect(byte[] bytes)
        {
            BeforeProtect?.Invoke();
            if (Fail) throw new CryptographicException("test");
            byte[] result = new byte[bytes.Length + 1]; result[0] = marker;
            for (int i = 0; i < bytes.Length; i++) result[i + 1] = (byte)(bytes[i] ^ marker);
            return result;
        }
        public byte[] Unprotect(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes[0] != marker) throw new CryptographicException("test");
            return bytes.Skip(1).Select(b => (byte)(b ^ marker)).ToArray();
        }
    }
    private static DesktopService Service(string path, ISecretProtector protector) => new(path, Fixtures.Client(new FakeHttpHandler()), protector);
    private static async Task<StoreState> Seed(string directory, ISecretProtector protector)
    {
        using (var service = Service(directory, protector))
            await service.SaveProfileAsync(new RelayProfile { Name = "迁移假数据", BaseUrl = "https://relay.example/v1" }, Program.Key1);
        DesktopStore store = new(directory, protector);
        StoreState state = await store.ReadSnapshotAsync(default);
        var profile = state.Profiles.Single() with { Models = [new ModelEntry(new string('m', 240))] };
        state.Profiles[0] = profile;
        state.Vault.KnownSecrets.Add(Program.Key2);
        state.Vault.InspectionSelections = [new(profile.Id, true, [profile.Models[0].Id])];
        state.Vault.IdentityInspectionSelections = [new(profile.Id, true, [profile.Models[0].Id])];
        state.Vault.InspectionSettings = new() { Enabled = true, IntervalMinutes = 45 };
        state.Vault.IdentityInspectionSettings = new() { Enabled = true, IntervalMinutes = 50 };
        state.Vault.InspectionGrants = [new(new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, [profile.Models[0].Id]), DateTimeOffset.UtcNow, 45, MaxAttempts: 3)];
        state.Vault.IdentityInspectionGrants = [new(new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, profile.Models[0].Id), DateTimeOffset.UtcNow, 50)];
        var attempt = new IdentityInspectionAttempt { Target = new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, profile.Models[0].Id), State = IdentityInspectionAttemptState.Submitted };
        var round = new IdentityInspectionRoundSummary { Attempts = [attempt, new() { Target = attempt.Target with { ModelId = "pending" } }] };
        var record = new ProbeRecord { ProfileId = profile.Id, KeyRevision = profile.KeyRevision, BaseUrl = profile.BaseUrl, ModelId = profile.Models[0].Id,
            RemoteRunId = "migration-remote-id", State = LocalRunState.Running, IdentityInspectionAttemptId = attempt.AttemptId, IdentityInspectionRoundId = round.RoundId };
        state.Vault.History.Add(record);
        state.Vault.LatestIdentityInspectionRound = round with { Attempts = [attempt with { ProbeLocalId = record.LocalId }, round.Attempts[1]] };
        state.Vault.ConnectionHistory.Add(new() { ProfileId = Guid.NewGuid(), KeyRevision = 1, BaseUrl = profile.BaseUrl, ModelId = "deleted-profile-model", StartedAt = DateTimeOffset.UtcNow.AddDays(-90), CompletedAt = DateTimeOffset.UtcNow.AddDays(-90), Stage = ConnectionTestStage.Failed });
        // Write without ordinary retention so the migration must preserve even already-old histories.
        byte[] vault = JsonSerializer.SerializeToUtf8Bytes(state.Vault, DesktopStore.Json);
        state.Vault.MetadataHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state.Profiles, DesktopStore.Json)));
        CryptographicOperations.ZeroMemory(vault);
        await WriteState(directory, state, protector);
        return state;
    }
    private static async Task WriteState(string directory, StoreState state, ISecretProtector protector)
    {
        state.Vault.MetadataHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state.Profiles, DesktopStore.Json)));
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(state.Vault, DesktopStore.Json);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "desktop-state.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { schemaVersion = 3, profiles = state.Profiles, protectedState = Convert.ToBase64String(protector.Protect(plaintext)) }, DesktopStore.Json));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static async Task Roundtrip()
    {
        using TempDir root = new(); string source = Path.Combine(root.Path, "source"), target = Path.Combine(root.Path, "target");
        var oldProtector = new DeviceProtector(67); var newProtector = new DeviceProtector(91);
        StoreState original = await Seed(source, oldProtector);
        using var sourceService = Service(source, oldProtector); string package = Path.Combine(root.Path, "transfer.probe");
        byte[] sourceBefore = await File.ReadAllBytesAsync(Path.Combine(source, "desktop-state.json"));
        await sourceService.ExportMigrationAsync(package, Password);
        Check.True(sourceBefore.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(source, "desktop-state.json"))), "export does not repair source");
        Check.False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(package)).Contains(Program.Key1), "no plaintext key in package");
        using var targetService = Service(target, newProtector);
        using var preview = await targetService.PreviewMigrationAsync(package, Password);
        Check.Equal(1, preview.ProfileCount, "profile count"); Check.True(preview.HasUnfinishedWork, "unfinished disclosed");
        Check.False(File.Exists(Path.Combine(target, "desktop-state.json")), "preview does not write");
        await File.WriteAllTextAsync(package, "replaced after preview");
        await targetService.ImportMigrationAsync(preview);
        Check.True(targetService.MigrationRestartRequired, "restart barrier");
        await Check.ThrowsClientAsync(() => targetService.SaveNotificationPreferencesAsync(new()), "MigrationRestartRequired", "stale writes refused");
        DesktopStore targetStore = new(target, newProtector); StoreState result = await targetStore.ReadSnapshotAsync(default);
        Check.Equal(original.Profiles.Single().Id, result.Profiles.Single().Id, "profile IDs preserved");
        Check.True(result.Vault.KnownSecrets.SetEquals(original.Vault.KnownSecrets), "historical redaction keys preserved");
        Check.Equal(1, result.Vault.ConnectionHistory.Count, "old deleted-profile history retained");
        Check.Equal("migration-remote-id", result.Vault.History.Single().RemoteRunId, "remote ID preserved");
        Check.True(result.Vault.History.Single().ManualResumeRequired, "manual query flag persisted");
        Check.Equal(LocalRunState.PollingInterrupted, result.Vault.History.Single().State, "active blocking state preserved");
        Check.Equal(0, result.Vault.IdentityInspectionGrants.Count + result.Vault.InspectionGrants.Count, "all grants revoked");
        Check.False(result.Vault.InspectionSettings.Enabled || result.Vault.IdentityInspectionSettings.Enabled, "schedulers disabled");
        Check.Equal(45, result.Vault.InspectionSettings.IntervalMinutes, "interval preserved");
        Check.Equal(IdentityInspectionAttemptState.Skipped, result.Vault.LatestIdentityInspectionRound!.Attempts[1].State, "pending queue terminated");
        Check.NotEqual(original.Vault.InspectionNonce, result.Vault.InspectionNonce, "nonce renewed");
        using var wrongDevice = Service(target, oldProtector);
        await Check.ThrowsClientAsync(() => wrongDevice.GetProfilesAsync(), "StorageRead", "old device protector cannot decrypt target");
        using var reopened = Service(target, newProtector); _ = await reopened.GetProfilesAsync();
        var snapshot = await reopened.GetIdentityInspectionSnapshotAsync();
        var consent = Fixtures.ManualIdentityApproval(snapshot);
        await Check.ThrowsClientAsync(() => reopened.BeginIdentityInspectionRoundAsync(IdentityInspectionTrigger.Manual, consent), "ActiveRun", "imported active task blocks new POST");
    }

    public static async Task RejectionAndStrictParsing()
    {
        using TempDir root = new(); var protector = new DeviceProtector(71);
        string source = Path.Combine(root.Path, "source"), target = Path.Combine(root.Path, "target"), path = Path.Combine(root.Path, "transfer.probe");
        await Seed(source, protector); using var service = Service(source, protector); await service.ExportMigrationAsync(path, Password);
        byte[] valid = await File.ReadAllBytesAsync(path);
        using var destination = Service(target, protector);
        await Check.ThrowsClientAsync(() => destination.PreviewMigrationAsync(path, Password.Trim()), "MigrationFailed", "password not trimmed");
        foreach (int offset in new[] { 0, 8, 12, 16, 20, 24, 40, 52, 60, valid.Length - 1 })
        {
            byte[] changed = valid.ToArray(); changed[offset] ^= 1; await File.WriteAllBytesAsync(path, changed);
            await Check.ThrowsClientAsync(() => destination.PreviewMigrationAsync(path, Password), "MigrationFailed", "tampered header/cipher/tag rejected");
        }
        byte[] payload = MigrationPackage.Decrypt(valid, Password); string json = Encoding.UTF8.GetString(payload);
        var rootNode = JsonNode.Parse(json)!.AsObject();
        foreach (Action<JsonObject> change in new Action<JsonObject>[]
        {
            n => n["formatVersion"] = 2,
            n => n.Remove("exportedAt"),
            n => n["unexpected"] = true,
            n => n["profiles"]![0]!["protocol"] = 0,
            n => n["profiles"]![0]!["keyRevision"] = 99,
            n => n["vault"]!["history"]![0]!["state"] = "NotAState"
        })
        {
            var node = rootNode.DeepClone().AsObject(); change(node);
            await File.WriteAllBytesAsync(path, MigrationPackage.Encrypt(Encoding.UTF8.GetBytes(node.ToJsonString()), Password));
            await Check.ThrowsClientAsync(() => destination.PreviewMigrationAsync(path, Password), "MigrationFailed", "strict payload validation");
        }
        string duplicate = json.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(path, MigrationPackage.Encrypt(Encoding.UTF8.GetBytes(duplicate), Password));
        await Check.ThrowsClientAsync(() => destination.PreviewMigrationAsync(path, Password), "MigrationFailed", "duplicate fields rejected");
        Check.False(File.Exists(Path.Combine(target, "desktop-state.json")), "all bad input leaves target absent");
        await File.WriteAllBytesAsync(path, valid);
        using var preview = await destination.PreviewMigrationAsync(path, Password);
        await destination.SaveProfileAsync(new RelayProfile { Name = "concurrent", BaseUrl = "https://relay.example" }, Program.Key2);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(target, "desktop-state.json"));
        await Check.ThrowsClientAsync(() => destination.ImportMigrationAsync(preview), "MigrationTargetNotEmpty", "commit rechecks empty under gate");
        Check.True(before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(target, "desktop-state.json"))), "concurrent data unchanged");
        await Check.ThrowsClientAsync(() => service.ExportMigrationAsync(path, Password), "MigrationDestination", "no overwrite");
        await Check.ThrowsClientAsync(() => service.ExportMigrationAsync(Path.Combine(source, "backup.probe"), Password), "MigrationDestination", "not inside store");
        await Check.ThrowsClientAsync(() => service.ExportMigrationAsync(Path.Combine(root.Path, "short"), "short"), "MigrationPassword", "password minimum");
        CryptographicOperations.ZeroMemory(payload);
    }

    public static async Task FailureAtomicity()
    {
        using TempDir root = new(); var protector = new DeviceProtector(72);
        string source = Path.Combine(root.Path, "source"), target = Path.Combine(root.Path, "target"), path = Path.Combine(root.Path, "transfer.probe");
        await Seed(source, protector); using var sourceService = Service(source, protector); await sourceService.ExportMigrationAsync(path, Password);
        using var targetService = Service(target, protector);
        await targetService.SaveNotificationPreferencesAsync(new()); // First-start defaults are a logical empty store.
        string statePath = Path.Combine(target, "desktop-state.json"); byte[] baseline = await File.ReadAllBytesAsync(statePath);
        using (var preview = await targetService.PreviewMigrationAsync(path, Password))
        {
            protector.Fail = true;
            await Check.ThrowsClientAsync(() => targetService.ImportMigrationAsync(preview), "StorageWrite", "protection failure leaves old commit");
            protector.Fail = false;
        }
        Check.True(baseline.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(statePath)), "protect failure byte-exact preservation");
        using (var preview = await targetService.PreviewMigrationAsync(path, Password))
        using (var locked = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Check.ThrowsClientAsync(() => targetService.ImportMigrationAsync(preview), "StorageWrite", "atomic replacement denied by lock");
        Check.True(baseline.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(statePath)), "write failure byte-exact preservation");
        using (var preview = await targetService.PreviewMigrationAsync(path, Password))
        using (CancellationTokenSource cancellation = new())
        {
            protector.BeforeProtect = cancellation.Cancel;
            try { await targetService.ImportMigrationAsync(preview, cancellation.Token); throw new TestFailure("expected cancellation"); }
            catch (OperationCanceledException) { }
            finally { protector.BeforeProtect = null; }
        }
        Check.True(baseline.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(statePath)), "cancellation before commit preserves state");
        Check.Equal(0, Directory.GetFiles(target, "*.tmp").Length, "no transaction residue");
        using var reopened = Service(target, protector); Check.Equal(0, (await reopened.GetProfilesAsync()).Count, "reopen sees old complete state");
        await File.WriteAllTextAsync(statePath, "corrupt"); byte[] corrupt = await File.ReadAllBytesAsync(statePath);
        await Check.ThrowsClientAsync(() => targetService.PreviewMigrationAsync(path, Password), "MigrationStorageRead", "corruption never empty");
        Check.True(corrupt.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(statePath)), "preview does not repair corruption");
    }

    public static async Task<int> SeedUi(string directory)
    {
        string full = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!Path.IsPathRooted(directory) || !full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())) return 2;
        _ = await Seed(full, new WindowsDpapiSecretProtector());
        // UI fixtures must be safe to open with the real app: a started UI must not fire network work.
        // Read back the just-seeded fake state and neutralize automation the same way a real migration
        // import does (schedulers off, grants revoked, unfinished tasks flagged manual-resume) without
        // touching Seed()'s own semantics for the Core round-trip tests.
        var protector = new WindowsDpapiSecretProtector();
        {
            DesktopStore store = new(full, protector);
            StoreState state = await store.ReadSnapshotAsync(default);
            state.Vault.InspectionSettings = state.Vault.InspectionSettings with { Enabled = false };
            state.Vault.IdentityInspectionSettings = state.Vault.IdentityInspectionSettings with { Enabled = false };
            state.Vault.InspectionGrants.Clear();
            state.Vault.IdentityInspectionGrants.Clear();
            for (int i = 0; i < state.Vault.History.Count; i++)
            {
                ProbeRecord record = state.Vault.History[i];
                if (DesktopService.IsMigrationActive(record.State))
                    state.Vault.History[i] = record with { ManualResumeRequired = true };
            }
            await WriteState(full, state, protector);
        }
        Console.WriteLine("Seeded isolated migration UI fixture (fake credentials; schedulers off, no auto network).");
        return 0;
    }
}
