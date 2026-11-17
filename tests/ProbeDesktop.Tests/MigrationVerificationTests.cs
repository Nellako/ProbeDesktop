using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

/// <summary>
/// Independent verifier tests for v1.3 password-protected migration. Deliberately separate from
/// MigrationTests (implementer self-check) and scoped to cases that file does not assert:
/// ticket lifecycle, backup-only / corrupt-primary empty-store detection, malformed package
/// boundaries, commit barrier and zero-network reload. Uses a tripwire transport that throws if
/// any network call is attempted; all state lives in per-test temporary directories.
/// </summary>
public static class MigrationVerificationTests
{
    private const string Password = "verifier-migration-password-1";

    /// <summary>Independent reversible protector with an explicit device id. A blob from another
    /// device id cannot be unprotracted, which is the cross-device property under test.</summary>
    private sealed class MvProtector(byte device) : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
        {
            byte[] result = new byte[plaintext.Length + 1];
            result[0] = device;
            for (int i = 0; i < plaintext.Length; i++) result[i + 1] = (byte)(plaintext[i] ^ device ^ (byte)(i * 17 + 3));
            return result;
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length == 0 || ciphertext[0] != device) throw new CryptographicException("wrong device");
            byte[] result = new byte[ciphertext.Length - 1];
            for (int i = 0; i < result.Length; i++) result[i] = (byte)(ciphertext[i + 1] ^ device ^ (byte)(i * 17 + 3));
            return result;
        }
    }

    /// <summary>Any outbound request is a failure: migration must never touch the network.</summary>
    private sealed class TripwireHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            throw new InvalidOperationException("migration verification must not emit network requests: " + request.RequestUri);
        }
    }

    private static DesktopService Service(string directory, ISecretProtector protector, TripwireHandler handler)
        => new(directory, new HttpClient(handler, disposeHandler: false), protector);

    private static async Task<Guid> SeedSource(string directory, ISecretProtector protector, TripwireHandler handler)
    {
        using DesktopService service = Service(directory, protector, handler);
        RelayProfile profile = await service.SaveProfileAsync(
            new RelayProfile { Name = "verifier-source", BaseUrl = "https://relay.example.com/v1", DefaultModelId = "model-a" }, Program.Key1);
        return profile.Id;
    }

    private static string StatePath(string directory) => Path.Combine(directory, "desktop-state.json");

    // MV01 -------------------------------------------------------------------
    /// <summary>Preview tickets are service-bound, single-use and unusable after disposal.</summary>
    public static async Task TicketLifecycle()
    {
        using TempDir root = new();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        string other = Path.Combine(root.Path, "other");
        string package = Path.Combine(root.Path, "transfer.pdmig");
        var handler = new TripwireHandler();
        var protector = new MvProtector(11);
        await SeedSource(source, protector, handler);
        using (DesktopService exporter = Service(source, protector, handler))
            await exporter.ExportMigrationAsync(package, Password);

        using DesktopService service = Service(target, protector, handler);
        using DesktopService second = Service(other, protector, handler);

        // Cross-service ticket: verified for one service instance, must not be redeemed by another.
        // Owner mismatch and expiry share one guard, so either refusal code is acceptable; the
        // load-bearing property is that no state is written by the foreign instance.
        using (MigrationPreview foreign = await service.PreviewMigrationAsync(package, Password))
        {
            ClientOperationException refused = await Check.ThrowsClientAsync(
                () => second.ImportMigrationAsync(foreign), null, "ticket from another service instance is refused");
            Check.True(refused.Code is "MigrationFailed" or "MigrationPreviewExpired",
                "foreign ticket refusal is a definite client error, got " + refused.Code);
            Check.False(second.MigrationRestartRequired, "foreign ticket redemption does not set the barrier");
            Check.False(File.Exists(StatePath(other)), "foreign ticket redemption writes nothing");
        }

        // Double use of the same ticket.
        MigrationPreview preview = await service.PreviewMigrationAsync(package, Password);
        await service.ImportMigrationAsync(preview);
        Check.True(service.MigrationRestartRequired, "first import commits and sets the restart barrier");
        // The replay is refused by the barrier (or the single-use ticket): either way a definite client
        // error that must not change committed state.
        ClientOperationException replay = await Check.ThrowsClientAsync(
            () => service.ImportMigrationAsync(preview), null, "second use of the same ticket is refused");
        Check.True(replay.Code is "MigrationFailed" or "MigrationRestartRequired",
            "ticket replay refusal is a definite client error, got " + replay.Code);
        preview.Dispose();

        // Dispose before use.
        using (DesktopService fresh = Service(Path.Combine(root.Path, "fresh"), protector, handler))
        {
            MigrationPreview disposed = await fresh.PreviewMigrationAsync(package, Password);
            disposed.Dispose();
            ClientOperationException refused = await Check.ThrowsClientAsync(
                () => fresh.ImportMigrationAsync(disposed), null, "disposed ticket cannot be redeemed");
            Check.True(refused.Code is "MigrationFailed" or "MigrationPreviewExpired",
                "disposed ticket refusal is a definite client error, got " + refused.Code);
            Check.False(fresh.MigrationRestartRequired, "refused redemption does not set the barrier");
            Check.False(File.Exists(StatePath(Path.Combine(root.Path, "fresh"))), "refused redemption writes nothing");
        }

        // Expiry is ten minutes from validation; force the clock boundary through the ticket's own field
        // instead of sleeping, then confirm the guard rejects a still-undisposed ticket.
        using (DesktopService late = Service(Path.Combine(root.Path, "late"), protector, handler))
        {
            MigrationPreview expiring = await late.PreviewMigrationAsync(package, Password);
            TimeSpan window = expiring.ExpiresAt - DateTimeOffset.UtcNow;
            Check.True(window > TimeSpan.FromMinutes(9) && window <= TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(5),
                "preview lifetime is ten minutes, was " + window);
            FieldInfo field = typeof(MigrationPreview).GetField("<ExpiresAt>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("preview expiry backing field not found");
            field.SetValue(expiring, DateTimeOffset.UtcNow.AddSeconds(-1));
            // Expiry surfaces the dedicated preview code rather than the generic package failure.
            await Check.ThrowsClientAsync(() => late.ImportMigrationAsync(expiring), "MigrationPreviewExpired",
                "expired ticket is refused even though it was never used");
            Check.False(late.MigrationRestartRequired, "expired ticket does not commit anything");
            expiring.Dispose();
        }

        Check.Equal(0, handler.Count, "no migration step performed any network request");
    }

    // MV02 -------------------------------------------------------------------
    /// <summary>A backup-only or corrupt-primary store is not an empty target, and a non-empty target is untouched.</summary>
    public static async Task EmptyStoreAndBackupOnly()
    {
        using TempDir root = new();
        string source = Path.Combine(root.Path, "source");
        string package = Path.Combine(root.Path, "transfer.pdmig");
        var handler = new TripwireHandler();
        var protector = new MvProtector(23);
        await SeedSource(source, protector, handler);
        using (DesktopService exporter = Service(source, protector, handler))
            await exporter.ExportMigrationAsync(package, Password);

        // Primary deleted, only the rotated .bak remains. This must never be read as an empty store.
        string backupOnly = Path.Combine(root.Path, "backup-only");
        using (DesktopService seed = Service(backupOnly, protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "older", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        using (DesktopService seed = Service(backupOnly, protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "newer", BaseUrl = "https://relay2.example.com/v1" }, Program.Key2);
        Check.True(File.Exists(StatePath(backupOnly) + ".bak"), "a rotated backup exists before deletion");
        File.Delete(StatePath(backupOnly));
        byte[] backupBefore = await File.ReadAllBytesAsync(StatePath(backupOnly) + ".bak");
        using (DesktopService service = Service(backupOnly, protector, handler))
            await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationStorageRead",
                "backup-only store is not treated as empty and is not repaired");
        Check.True(backupBefore.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(StatePath(backupOnly) + ".bak")),
            "backup bytes unchanged by the refused preview");
        Check.False(File.Exists(StatePath(backupOnly)), "refused preview does not materialize a primary");

        // Corrupt primary with an intact backup: same rule, no backup fallback that could look "empty".
        string corrupt = Path.Combine(root.Path, "corrupt");
        using (DesktopService seed = Service(corrupt, protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "corrupt", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        using (DesktopService seed = Service(corrupt, protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "corrupt2", BaseUrl = "https://relay2.example.com/v1" }, Program.Key2);
        await File.WriteAllTextAsync(StatePath(corrupt), "{ not-json");
        byte[] corruptBefore = await File.ReadAllBytesAsync(StatePath(corrupt));
        using (DesktopService service = Service(corrupt, protector, handler))
            Check.Equal("MigrationStorageRead",
                (await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), null,
                    "corrupt primary with backup rejects preview")).Code,
                "corrupt primary surfaces the migration-specific read failure");
        Check.True(corruptBefore.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(StatePath(corrupt))),
            "corrupt primary is not repaired by preview");

        // A genuinely non-empty store rejects with the target-not-empty code and keeps its bytes.
        string occupied = Path.Combine(root.Path, "occupied");
        using (DesktopService seed = Service(occupied, protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "keep", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        byte[] occupiedBefore = await File.ReadAllBytesAsync(StatePath(occupied));
        using (DesktopService service = Service(occupied, protector, handler))
        {
            await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationTargetNotEmpty",
                "store with a profile is not an empty target");
            MigrationPreview? never = null;
            try
            {
                never = await service.PreviewMigrationAsync(package, Password);
                throw new TestFailure("expected MigrationTargetNotEmpty");
            }
            catch (ClientOperationException) { }
            finally { never?.Dispose(); }
            Check.True(occupiedBefore.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(StatePath(occupied))),
                "non-empty target bytes unchanged");
            Check.Equal(0, handler.Count, "no network requests while probing empty-store rules");
        }
    }

    // MV03 -------------------------------------------------------------------
    /// <summary>Malformed package boundaries (length, KDF cost, path rules) fail closed without expensive work.</summary>
    public static async Task MaliciousBoundaries()
    {
        using TempDir root = new();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        string package = Path.Combine(root.Path, "transfer.pdmig");
        var handler = new TripwireHandler();
        var protector = new MvProtector(37);
        await SeedSource(source, protector, handler);
        using DesktopService exporter = Service(source, protector, handler);
        await exporter.ExportMigrationAsync(package, Password);
        byte[] valid = await File.ReadAllBytesAsync(package);
        using DesktopService service = Service(target, protector, handler);

        // Truncations around the 72-byte overhead boundary.
        foreach (int length in new[] { 0, 1, 8, 55, 56, 71, 72, valid.Length - 17 })
        {
            await File.WriteAllBytesAsync(package, valid.Take(length).ToArray());
            await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationFailed",
                "package truncated to " + length + " bytes is refused");
        }

        // Trailing garbage beyond the authenticated payload length.
        await File.WriteAllBytesAsync(package, valid.Concat(new byte[] { 0 }).ToArray());
        await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationFailed",
            "appended byte fails the authenticated length");

        // KDF work factor is authenticated and must be rejected before any expensive derivation.
        byte[] expensive = valid.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(expensive, 20);
        await File.WriteAllBytesAsync(package, expensive);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationFailed",
            "attacker-chosen KDF iteration count is refused");
        sw.Stop();
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), "KDF parameter rejection is cheap, took " + sw.ElapsedMilliseconds + " ms");

        // Wrong algorithm identifiers and magic.
        foreach (int offset in new[] { 0, 12, 16 })
        {
            byte[] changed = valid.ToArray();
            changed[offset] ^= 0x01;
            await File.WriteAllBytesAsync(package, changed);
            await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password), "MigrationFailed",
                "algorithm/magic byte at " + offset + " is authenticated");
        }

        // Password boundaries: 11 rejected, 12 accepted, 1025 rejected; interior/trailing spaces are literal.
        await File.WriteAllBytesAsync(package, valid);
        await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, new string('p', 11)), "MigrationPassword",
            "11-character password rejected");
        await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, new string('p', 1025)), "MigrationPassword",
            "1025-character password rejected");
        await Check.ThrowsClientAsync(() => service.PreviewMigrationAsync(package, Password + " "), "MigrationFailed",
            "trailing space is not trimmed into a match");
        using (MigrationPreview ok = await service.PreviewMigrationAsync(package, Password))
            Check.Equal(1, ok.ProfileCount, "12+ character exact password still previews");

        // Destination path rules: inside the data directory, pre-existing file, and alternate data streams.
        using DesktopService exporter2 = Service(source, protector, handler);
        await Check.ThrowsClientAsync(() => exporter2.ExportMigrationAsync(Path.Combine(source, "inside.pdmig"), Password),
            "MigrationDestination", "destination inside the data directory refused");
        await Check.ThrowsClientAsync(() => exporter2.ExportMigrationAsync(Path.Combine(source, "sub", "deep.pdmig"), Password),
            "MigrationDestination", "missing destination directory is refused rather than created");
        Check.False(Directory.Exists(Path.Combine(source, "sub")), "no directory is created for a rejected export");
        string existing = Path.Combine(root.Path, "existing.pdmig");
        await File.WriteAllTextAsync(existing, "occupied");
        await Check.ThrowsClientAsync(() => exporter2.ExportMigrationAsync(existing, Password), "MigrationDestination",
            "existing destination is never overwritten");
        Check.Equal("occupied", await File.ReadAllTextAsync(existing), "existing destination content preserved");
        ClientOperationException ads = await Check.ThrowsClientAsync(
            () => exporter2.ExportMigrationAsync(Path.Combine(root.Path, "ads.pdmig:stream"), Password), null,
            "alternate data stream destination refused");
        Check.True(ads.Code is "MigrationDestination" or "MigrationFailed",
            "ADS destination is a definite client error, got " + ads.Code);

        // Escape via traversal normalizes back into the data directory and is still refused.
        await Check.ThrowsClientAsync(
            () => exporter2.ExportMigrationAsync(Path.Combine(source, "..", Path.GetFileName(source), "escape.pdmig"), Password),
            "MigrationDestination", "dot-dot path resolving inside the data directory is refused");

        // Failure must not leave a partial package behind under any of the rejected exports.
        Check.Equal(0, Directory.GetFiles(root.Path, "*.pdmig", SearchOption.AllDirectories)
            .Count(f => Path.GetFileName(f).StartsWith(".probe-migration-", StringComparison.Ordinal)),
            "no temporary package files remain after rejections");
        Check.Equal(0, handler.Count, "malformed-input handling performed no network request");
    }

    // MV04 -------------------------------------------------------------------
    /// <summary>Commit barrier: after import the instance refuses writes, and a reload performs no network I/O
    /// while unfinished tasks keep the unknown/manual-resume blocking semantics.</summary>
    public static async Task CommitBarrierAndZeroNetworkReload()
    {
        using TempDir root = new();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        string package = Path.Combine(root.Path, "transfer.pdmig");
        var sourceHandler = new TripwireHandler();
        var protector = new MvProtector(51);
        Guid profileId = await SeedSource(source, protector, sourceHandler);

        // Craft an unfinished identity-inspection-sourced task in the source store, then export it.
        DesktopStore sourceStore = new(source, protector);
        StoreState state = await sourceStore.ReadSnapshotAsync(default);
        RelayProfile profile = state.Profiles.Single();
        var seededRecord = new ProbeRecord
        {
            Source = ProbeRecordSource.IdentityInspection,
            ProfileId = profile.Id, KeyRevision = profile.KeyRevision, ProfileName = profile.Name,
            BaseUrl = profile.BaseUrl, ModelId = "model-a", Protocol = profile.Protocol,
            RemoteRunId = "verifier-remote-run", State = LocalRunState.Running
        };
        var attempt = new IdentityInspectionAttempt
        {
            Target = new(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, "model-a"),
            State = IdentityInspectionAttemptState.Submitted,
            ProbeLocalId = seededRecord.LocalId
        };
        var round = new IdentityInspectionRoundSummary { Attempts = [attempt] };
        // The record and its round attempt must agree on the cross-reference the validator checks.
        state.Vault.History.Add(seededRecord with { IdentityInspectionAttemptId = attempt.AttemptId, IdentityInspectionRoundId = round.RoundId });
        state.Vault.LatestIdentityInspectionRound = round;
        state.Vault.InspectionSettings = new InspectionSettings { Enabled = true, IntervalMinutes = 30 };
        state.Vault.InspectionGrants = [new(new InspectionTarget(profile.Id, profile.KeyRevision, profile.BaseUrl, profile.Protocol, ["model-a"]),
            DateTimeOffset.UtcNow, 30)];
        await WriteRawState(source, state, protector);
        using (DesktopService exporter = Service(source, protector, sourceHandler))
            await exporter.ExportMigrationAsync(package, Password);

        var targetHandler = new TripwireHandler();
        using (DesktopService service = Service(target, protector, targetHandler))
        {
            Check.Equal(0, targetHandler.Count, "constructing a target service performs no I/O on the network");
            using MigrationPreview preview = await service.PreviewMigrationAsync(package, Password);
            Check.True(preview.HasUnfinishedWork, "unfinished work is disclosed in the preview");
            await service.ImportMigrationAsync(preview);
            Check.True(service.MigrationRestartRequired, "import sets the restart barrier");

            // Barrier covers reads too: no stale in-memory state can be re-read and written back.
            await Check.ThrowsClientAsync(() => service.GetProfilesAsync(), "MigrationRestartRequired",
                "post-import read is refused, not served from stale memory");
            await Check.ThrowsClientAsync(
                () => service.SaveProfileAsync(new RelayProfile { Name = "late", BaseUrl = "https://relay.example.com/v1" }, Program.Key1),
                "MigrationRestartRequired", "post-import write is refused");
            await Check.ThrowsClientAsync(
                () => service.StartProbeAsync(profileId, "model-a", null,
                    new OutboundConsent(profileId, 1, "https://relay.example.com/v1", OutboundPurpose.Probe, Fixtures.ServiceRunUrl, "model-a", DateTimeOffset.UtcNow)),
                "MigrationRestartRequired", "post-import submission is refused before any consent/network path");
            Check.Equal(0, targetHandler.Count, "no target network request during or after import");
        }

        // Fresh process-equivalent reopen: reading state must not send anything, and the imported
        // unfinished record stays active + manual-resume so it keeps blocking new submissions.
        using (DesktopService reopened = Service(target, protector, targetHandler))
        {
            var profiles = await reopened.GetProfilesAsync();
            var history = await reopened.GetHistoryAsync();
            Check.Equal(1, profiles.Count, "imported profile is readable after reopen");
            Check.Equal(0, targetHandler.Count, "reopening and reading performs no network request");
            ProbeRecord record = history.Single();
            Check.True(record.ManualResumeRequired, "imported unfinished record requires manual resume");
            Check.Equal(LocalRunState.PollingInterrupted, record.State, "imported unfinished record stays blocking");
            Check.Equal("verifier-remote-run", record.RemoteRunId, "remote run id preserved for GET-only recovery");

            var vault = (await reopened.GetIdentityInspectionSnapshotAsync());
            Check.False(vault.Settings.Enabled, "identity inspection disabled after import");
            Check.Equal(30, vault.Settings.IntervalMinutes, "interval preserved while disabled");
            Check.Equal(0, targetHandler.Count, "identity snapshot read performs no network request");
            DesktopStore verifyStore = new(target, protector);
            StoreState persisted = await verifyStore.ReadSnapshotAsync(default);
            Check.Equal(0, persisted.Vault.IdentityInspectionGrants.Count, "identity grants cleared after import");
            Check.Equal(0, persisted.Vault.InspectionGrants.Count, "connection grants cleared after import");
            Check.False(persisted.Vault.InspectionSettings.Enabled, "connection inspection disabled after import");
        }

        Check.Equal(0, sourceHandler.Count, "source export path made no network request");
    }

    // MV05 -------------------------------------------------------------------
    /// <summary>Core observable the UI guard depends on after a successful import: the restart barrier is
    /// set immediately, committed state is durable, and the commit path performs no network request.
    /// This does NOT inject a UI-layer exception; the ViewModel catch branches remain unverified here.</summary>
    public static async Task PostCommitBarrierAndDurability()
    {
        using TempDir root = new();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        string package = Path.Combine(root.Path, "transfer.pdmig");
        var handler = new TripwireHandler();
        var protector = new MvProtector(61);
        await SeedSource(source, protector, handler);
        using (DesktopService exporter = Service(source, protector, handler))
            await exporter.ExportMigrationAsync(package, Password);

        using DesktopService service = Service(target, protector, handler);
        using MigrationPreview preview = await service.PreviewMigrationAsync(package, Password);
        await service.ImportMigrationAsync(preview);

        // The barrier must be set the instant a successful import returns, so any later UI-layer throw
        // (Info/OCE) can be classified against `committed || MigrationRestartRequired`. The UI branch
        // itself is not exercised here.
        Check.True(service.MigrationRestartRequired, "barrier is set immediately when a successful import returns");

        // The committed state is durable and readable by a fresh reader.
        DesktopStore store = new(target, protector);
        StoreState persisted = await store.ReadSnapshotAsync(default);
        Check.Equal(1, persisted.Profiles.Count, "committed data is durable and readable after import");
        Check.Equal(0, handler.Count, "commit path made no network request");
    }

    // MV06 -------------------------------------------------------------------
    /// <summary>Schema envelope behaviour of this version: writes schema 3, still loads a schema-2 store
    /// for upgrade compatibility, and rejects an out-of-range schema instead of silently downgrading.
    /// The prior-parser rejection of schema 3 is verified as source evidence recorded in the report, not
    /// asserted here (a git/HEAD-bound assertion would rot once 1.3 is committed and is not part of the runner).</summary>
    public static async Task SchemaEnvelopeBehaviour()
    {
        using TempDir root = new();
        var handler = new TripwireHandler();
        var protector = new MvProtector(71);
        string dir = Path.Combine(root.Path, "store");
        using (DesktopService service = Service(dir, protector, handler))
            await service.SaveProfileAsync(new RelayProfile { Name = "v3", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        string raw = await File.ReadAllTextAsync(StatePath(dir));
        Check.Contains(raw, "\"schemaVersion\": 3", "this version writes schema 3");
        using (DesktopService reopened = Service(dir, protector, handler))
            Check.Equal(1, (await reopened.GetProfilesAsync()).Count, "schema-3 store loads in this version");

        // Schema 2 (prior supported version) must still be readable for upgrade compatibility.
        string legacyDir = Path.Combine(root.Path, "legacy");
        Directory.CreateDirectory(legacyDir);
        using (DesktopService seed = Service(Path.Combine(root.Path, "seed"), protector, handler))
            await seed.SaveProfileAsync(new RelayProfile { Name = "s2", BaseUrl = "https://relay.example.com/v1" }, Program.Key1);
        string seeded = await File.ReadAllTextAsync(StatePath(Path.Combine(root.Path, "seed")));
        await File.WriteAllTextAsync(StatePath(legacyDir), seeded.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2", StringComparison.Ordinal));
        using (DesktopService legacy = Service(legacyDir, protector, handler))
        {
            var profiles = await legacy.GetProfilesAsync();
            Check.Equal(1, profiles.Count, "schema-2 store still loads (upgrade compatibility)");
            Check.Equal("s2", profiles[0].Name, "schema-2 profile preserved verbatim");
        }

        // A schema far outside the accepted set (4) is rejected rather than silently downgraded.
        string futureDir = Path.Combine(root.Path, "future");
        Directory.CreateDirectory(futureDir);
        await File.WriteAllTextAsync(StatePath(futureDir), seeded.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 4", StringComparison.Ordinal));
        using (DesktopService future = Service(futureDir, protector, handler))
            await Check.ThrowsClientAsync(() => future.GetProfilesAsync(), "StorageVersion", "unknown schema 4 is rejected");
        Check.Equal(0, handler.Count, "schema handling made no network request");
    }

    private static async Task WriteRawState(string directory, StoreState state, ISecretProtector protector)
    {
        state.Vault.MetadataHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state.Profiles, DesktopStore.Json)));
        byte[] plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state.Vault, DesktopStore.Json);
        try
        {
            await File.WriteAllBytesAsync(StatePath(directory), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { schemaVersion = 3, profiles = state.Profiles, protectedState = Convert.ToBase64String(protector.Protect(plaintext)) },
                DesktopStore.Json));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
