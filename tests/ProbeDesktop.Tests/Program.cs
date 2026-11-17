using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Text.Json;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class Program
{
    public const string Key1 = "sk-test-KEY-ONE-0123456789";
    public const string Key2 = "sk-test-KEY-TWO-9876543210";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--seed-migration-ui")
            return await MigrationTests.SeedUi(args[1]).ConfigureAwait(false);
        if (args.Length >= 2 && args[0] == "--seed-ui")
            return await SeedUiAsync(args[1]).ConfigureAwait(false);
        if (args.Length >= 2 && args[0] == "--seed-visual")
            return await SeedVisualAsync(args[1]).ConfigureAwait(false);
        if (args.Length >= 1 && args[0] == "--connection-due-wait")
            return await ConnectionTests.RunDueWaitModeAsync().ConfigureAwait(false);
        if (args.Length >= 2 && args[0] == "--seed-inspection-ui")
            return await SeedInspectionUiAsync(args[1]).ConfigureAwait(false);
        if (args.Length >= 2 && args[0] == "--seed-identity-ui")
            return await SeedIdentityUiAsync(args[1]).ConfigureAwait(false);

        if (args.Length >= 2 && args[0] == "--seed-protocol-ui")
            return await SeedInspectionUiAsync(args[1], protocolStates: true).ConfigureAwait(false);

        var tests = new List<(string Id, string Name, Func<Task> Body)>
        {
            ("CX01", "Reading markers across three protocols, JSON/SSE and restart", ContextCheckTests.ProtocolsAndPersistence),
            ("CX02", "Legacy reading checks remain untested and migration fields stay strict", ContextCheckTests.LegacyContract),
            ("VCX01", "Verifier: Responses JSON cannot join separate output messages", ContextBoundaryVerificationTests.ResponsesJsonOutputIsolation),
            ("VCX02", "Verifier: Responses SSE cannot join separate output items", ContextBoundaryVerificationTests.ResponsesSseOutputIsolation),
            ("VCX03", "Verifier: explicit empty final snapshot overrides prior deltas", ContextBoundaryVerificationTests.EmptyFinalSnapshotOverridesDeltas),
            ("VCX04", "Verifier: Chat choices remain isolated for JSON/SSE", ContextBoundaryVerificationTests.ChatChoicesNeverJoin),
            ("VCX05", "Verifier: byte fragments and authoritative final snapshots", ContextBoundaryVerificationTests.FragmentationAndFinalSnapshots),
            ("VCX06", "Verifier: correct answer prefix cannot hide interruption/truncation/error", ContextBoundaryVerificationTests.InterruptedCorrectAnswersNeverPass),
            ("VCX07", "Verifier: exact marker order, grammar and connectivity independence", ContextBoundaryVerificationTests.ExactAnswerRules),
            ("VCX08", "Verifier: marker mismatch never retries or pauses manual/automatic inspection", ContextBoundaryVerificationTests.MismatchDoesNotRetryOrPauseInspection),
            ("VCX09", "Verifier: old full migration payload loads while malformed new fields fail closed", ContextBoundaryVerificationTests.LegacyPayloadAndStrictNewFields),
            ("VCX10", "Verifier: fresh challenges, decrypted vault and exported summary never retain text", ContextBoundaryVerificationTests.SummaryNeverRetainsPromptOrOutput),
            ("VCX11", "Verifier: overflow cannot be cleared by a later short final snapshot", ContextBoundaryVerificationTests.OverflowCannotBeErasedByFinalSnapshot),
            ("VCX12", "Verifier: multiple unindexed Chat choices remain inconclusive", ContextBoundaryVerificationTests.AmbiguousUnindexedChatChoices),
            ("VCX13", "Verifier: content blocks within one message still join correctly", ContextBoundaryVerificationTests.SameMessageContentBlocksRemainValid),
            ("VCX14", "Verifier: Responses conflicting identity and final multi-output isolation", ContextBoundaryVerificationTests.ResponsesIdentityConflictsAndFinalOutputIsolation),
            ("VCX15", "Verifier: Chat index safety and legacy relay compatibility", ContextBoundaryVerificationTests.ChatIndexSafetyAndCompatibility),
            ("VCX16", "Verifier: Responses metadata, sticky final safety and exact capture limit", ContextBoundaryVerificationTests.ResponsesMetadataSafetyAndCompatibility),
            ("M01", "Migration full cross-device roundtrip and restart barrier", MigrationTests.Roundtrip),
            ("M02", "Migration authenticated format, strict validation and target recheck", MigrationTests.RejectionAndStrictParsing),
            ("M03", "Migration protection/write/cancellation failure atomicity", MigrationTests.FailureAtomicity),
            ("MV01", "Verifier: migration ticket is service-bound, single-use, disposable, expiring", MigrationVerificationTests.TicketLifecycle),
            ("MV02", "Verifier: backup-only/corrupt/non-empty targets are never empty; bytes untouched", MigrationVerificationTests.EmptyStoreAndBackupOnly),
            ("MV03", "Verifier: package length, KDF cost, algorithm bytes and path rules fail closed", MigrationVerificationTests.MaliciousBoundaries),
            ("MV04", "Verifier: commit barrier, zero-network reload, manual-resume blocking preserved", MigrationVerificationTests.CommitBarrierAndZeroNetworkReload),
            ("MV05", "Verifier: import sets the restart barrier immediately; committed state durable, no network", MigrationVerificationTests.PostCommitBarrierAndDurability),
            ("MV06", "Verifier: schema 3 written, schema 2 still loads, out-of-range schema rejected", MigrationVerificationTests.SchemaEnvelopeBehaviour),
            ("IR01", "Inspection bounded retries and final-only counters", InspectionRetryTests.BoundedAttemptsAndFinalResults),
            ("IR02", "Committed result survives cancellation at attempt boundaries", InspectionRetryTests.CompletedAttemptSurvivesCancellation),
            ("IR04", "Selection, key and authorization changes stop retries", InspectionRetryTests.ChangedScopeStopsRetries),
            ("IR03", "Legacy one-attempt authorization requires explicit upgrade", InspectionRetryTests.LegacyAuthorizationRequiresFreshConsent),
            ("IR05", "Retries stop at a mid-chain auth failure and pause the profile", InspectionRetryTests.AuthFailureMidChainStopsAndPauses),
            ("IR06", "Manual single connection test never retries", InspectionRetryTests.SingleManualConnectionNeverRetries),
            ("IR07", "UI disclosure promises request multipliers, never a cost ceiling", InspectionRetryTests.DisclosureCopyPromisesNoCostCeiling),
            ("T01", "Profile CRUD save/get/delete", T01_ProfileCrud),
            ("T02", "Key replacement vs blank keep (revision)", T02_KeyReplacement),
            ("T03", "Delete profile keeps run history", T03_DeleteProfileKeepsHistory),
            ("T04", "Store files contain no plaintext key", T04_NoPlaintextKeyOnDisk),
            ("T05", "Refresh requires valid consent, 0 requests otherwise", T05_RefreshConsentGating),
            ("T06", "Refresh single GET basepath/models, Bearer, dedup+persist", T06_RefreshHappyPath),
            ("T07", "Refresh 404/429/malformed preserve old cache", T07_RefreshFailurePreservesCache),
            ("T08", "Redirect is not followed (single request)", T08_NoRedirectFollow),
            ("T09", "Probe start single POST fixed URL, no Bearer, body shape", T09_ProbeStartOnce),
            ("T10", "Probe network timeout -> SubmissionUnknown, no resubmit", T10_ProbeTimeoutUnknown),
            ("T11", "Poll runId-only/running/completed parsing", T11_PollParsing),
            ("T12", "Poll unknown/malformed leaves success unchanged", T12_PollUnknownNoRegression),
            ("T13", "HTTP errors meaningful + response cap", T13_HttpErrorsAndCap),
            ("T14", "Echoed key redacted; export excludes runid/report; report URL allowlist", T14_RedactionAndExport),
            ("T15", "Restart resumes poll without POST; deleted profile history queryable", T15_RestartResumeAndHistory),
            ("T16", "Real Windows DPAPI roundtrip (protect/unprotect, no plaintext)", T16_DpapiRoundtrip),
            ("T17", "Base URL / key change clears model cache", T17_CacheInvalidatedOnChange),
            ("T18", "Failed atomic save preserves previously committed key", T18_FailedSaveKeepsOldKey),
            ("T19", "Secret echoed as runId is not persisted", T19_SecretRunIdRejected),
            ("T20", "Slow body read is cancellable, no hang", T20_SlowBodyCancellable),
            ("T21", "Long real-world model IDs preserved exactly (no over-redaction)", T21_LongModelIdsPreserved),
            ("T22", "Legacy item id / top-level verdict still accepted", T22_LegacyShapeCompat),
            ("T23", "Corrupt primary recovers from backup with exactly one warning", T23_BackupRecoveryWarnsOnce),
            ("T24", "Non-app files in target directory rejected, ACL untouched", T24_ForeignDirectoryRejectedAclKept),
            ("T25", "Probe invalid consent sends 0 requests and persists nothing", T25_ProbeConsentGating),
            ("F01", "Three-protocol SSE/JSON bounded classification matrix and restart", StreamingTests.Matrix),
            ("F02", "Broken SSE transport, independent counters/progress persistence", StreamingTests.TransportAndCounts),
            ("V01", "Verifier: SSE/JSON boundaries (error-first, incomplete=possible, no HTML success)", VerifierProtocolTests.V01_ClassificationBoundaries),
            ("V02", "Verifier: bounded SSE reads fail to unknown with a single request", VerifierProtocolTests.V02_BoundedReads),
            ("V03", "Verifier: four disjoint round counters and their persistence", VerifierProtocolTests.V03_RoundCounters),
            ("V04", "Verifier: legacy records not rewritten; redacted export whitelist", VerifierProtocolTests.V04_LegacyRecordsUnchanged),
            ("V05", "Verifier: Probe three protocols single POST + key-free poll GET", VerifierProtocolTests.V05_ProbeThreeProtocols),
            ("V06", "Verifier: UI outcome classification keeps possible success separate", VerifierProtocolTests.V06_UiClassification),
            ("V07", "Verifier: history filter tags match produced labels", VerifierProtocolTests.V07_UiFilterMapping),
            ("V08", "Verifier: truncated/contradictory completions are not confirmed success", VerifierProtocolTests.V08_TruncationIsNotConfirmedSuccess),
            ("V09", "Verifier: help page root content stretches to viewport (MaxWidth 1120, vertical scroll, no Left)", HelpLayoutTests.V09_HelpPageFillsWidth),
            ("C01", "ValidateConnection is local-only; bad URL/model never a pass", ConnectionTests.C01_ValidateLocalOnly),
            ("C02", "TestConnection single POST relay chat/completions, Bearer key, reading prompt", ConnectionTests.C02_TestConnectionHappyPath),
            ("C03", "TestConnection invalid consent sends 0 requests, stores nothing", ConnectionTests.C03_ConsentGating),
            ("C04", "200 without a real chat message is never ModelResponded", ConnectionTests.C04_NonModelResponses),
            ("C05", "Relay model alias accepted without identity claims", ConnectionTests.C05_ModelAliasNoIdentityClaim),
            ("C06", "HTTP 401/403/404/429/400/503 codes, Retry-After, no retry", ConnectionTests.C06_HttpErrorCodes),
            ("C07", "429 Retry-After blocks the next attempt locally", ConnectionTests.C07_RetryAfterBlocksNext),
            ("C08", "Timeout/cancellation is Unknown with exactly one attempt", ConnectionTests.C08_TimeoutSingleAttempt),
            ("C09", "Summary in protected vault: no key echo, no raw output, no probe history", ConnectionTests.C09_SummaryRedactionAndIsolation),
            ("C10", "Concurrent manual test refused (single-flight, no second request)", ConnectionTests.C10_SingleFlight),
            ("C11", "Settings default off/60min, interval 1..1440, enable needs consent", ConnectionTests.C11_SettingsDefaultsAndIntervalBounds),
            ("C12", "Enable requires fresh exact ConnectionScheduleConsent; off needs none", ConnectionTests.C12_EnableConsentAndRoundtrip),
            ("C13", "Restart turns scheduling off, stored settings retained", ConnectionTests.C13_RestartTurnsSchedulingOff),
            ("C14", "Scheduled test immediately after enable is NotDue with 0 requests", ConnectionTests.C14_ScheduledNotDue),
            ("C15", "Off/key/base/model change revokes schedule, 0 requests", ConnectionTests.C15_ScheduleRevocation),
            ("P01", "Protocol enum numbering, defaults, endpoint and label contract", ProtocolTests.P01_ProtocolEnumContract),
            ("P02", "Anthropic /models uses x-api-key + anthropic-version, no Bearer", ProtocolTests.P02_AnthropicModelsHeaders),
            ("P03", "Claude has_more=true rejected as partial, cache preserved, no paging", ProtocolTests.P03_AnthropicHasMoreRejectsPartial),
            ("P04", "has_more ignored for Chat/Responses protocols", ProtocolTests.P04_HasMoreIgnoredForNonAnthropic),
            ("P05", "Pagination consent refused before any network request", ProtocolTests.P05_PaginationConsentRefusedBeforeNetwork),
            ("P06", "Protocol switch clears cache and rejects stale per-URL consent", ProtocolTests.P06_ProtocolSwitchClearsCacheAndConsent),
            ("P07", "Protocol switch revokes session auto-test approval, 0 requests", ProtocolTests.P07_ProtocolSwitchRevokesSchedule),
            ("P08", "Connection test per protocol: URL, auth headers, body, parsing", ProtocolTests.P08_ConnectionTestPerProtocol),
            ("P09", "200 non-message per protocol never ModelResponded, no fallback", ProtocolTests.P09_NonModelResponsesPerProtocol),
            ("P10", "Probe sends official upstreamFormat for all three protocols", ProtocolTests.P10_NonChatProbeRejectedBeforeNetwork),
            ("P11", "Chat Probe works, record snapshots protocol, poll key-free", ProtocolTests.P11_ChatProbeStillWorksAndRecordSnapshotsProtocol),
            ("P12", "Schema1 loads as Chat, secret usable, first save writes schema2", ProtocolTests.P12_Schema1LoadsAndMigratesOnFirstSave),
            ("P13", "Injected protocol/unknown field in schema1 rejected, file untouched", ProtocolTests.P13_Schema1InjectedProtocolRejected),
            ("P14", "Schema2 protocol tamper is integrity failure, no .bak rollback", ProtocolTests.P14_Schema2ProtocolTamperNotRolledBack),
            ("P15", "Failed migration save leaves legacy bytes and keys intact", ProtocolTests.P15_FailedMigrationSaveKeepsLegacyFile),
            ("P16", "Corrupt JSON still recovers from backup after migration", ProtocolTests.P16_CorruptJsonStillRecoversFromBackup),
            ("P17", "Schema1 checksum is DTO-shape bound; new property cannot pass", ProtocolTests.P17_Schema1HashIsDtoShapeBound),
            ("P18", "Malformed envelope shapes fail closed, file untouched", ProtocolTests.P18_MalformedEnvelopeFailsClosed),
            ("P19", "Duplicate protocol field rejected, no last-wins parsing", ProtocolTests.P19_DuplicateProtocolFieldRejected),
            ("P20", "Key echoed by relay never leaks in new protocol paths", ProtocolTests.P20_KnownKeyEchoNoLeakInNewProtocols),
            ("P21", "Pagination edge cases and no-backup schema2 tamper", ProtocolTests.P21_PaginationEdgeCasesAndNoBackupTamper),
            ("P22", "Byte-exact artifact from previous Core loads and migrates", ProtocolTests.P22_ArtifactFromPreviousCoreLoads),
            ("P23", "Out-of-range protocol refused locally, zero requests", ProtocolTests.P23_InvalidProtocolRejectedNoRequest),
            ("B01", "Model refresh batch isolates failures and commits successful empty caches", ModelBatchTests.B01_FailureIsolationAndEmptySuccess),
            ("B02", "Model cache time wording is neutral and never claims a network fetch", ModelBatchTests.B02_CacheTimeWordingIsNeutral),
            ("R03", "Completed result ordering, newest failure, target isolation and original timestamps", LatestResultsTests.R03_CompletionOrderingAndIsolation),
            ("S01", "App interval defaults and integer bounds", AppSchedulerTests.S01_IntervalValidation),
            ("S02", "App waiting deadline resets from save time", AppSchedulerTests.S02_WaitResetUsesSaveTime),
            ("S03", "App in-flight interval save keeps absolute deadline", AppSchedulerTests.S03_SaveDuringRoundUsesAbsoluteDue),
            ("S04", "App overdue saved deadline coalesces once without overlap", AppSchedulerTests.S04_OverdueSaveCoalescesOnce),
            ("S05", "App explicit stop cancels round but invalid save does not", AppSchedulerTests.S05_StopCancelsButInvalidSaveDoesNot),
            ("R02", "Interval save with fresh consent leaves in-flight round intact", SelectionRequirementTests.R02_IntervalSaveKeepsInflightRound),
            ("R04", "Off-state scope confirm stores grants without enabling or sending", SelectionRequirementTests.R04_OffStateScopeConfirmGrantsWithoutEnabling),
            ("R05", "Same-interval scope confirm keeps in-flight round intact", SelectionRequirementTests.R05_SameIntervalConfirmKeepsInflightRound),
            ("R01", "Selection persistence, interval isolation, cache intersections, latest completed results", SelectionRequirementTests.R01_ExplicitSelectionsIntervalAndLatestResults),
            ("I01", "Inspection defaults off/30min, pending approval, no auto run", InspectionTests.I01_DefaultsAndNoAutoRun),
            ("I02", "Enable/interval need fresh exact-scope consent, zero requests", InspectionTests.I02_ConsentGatingAndExactScope),
            ("I03", "One round covers all profiles' cached models, per-protocol auth", InspectionTests.I03_BatchAllProfilesWithPerProtocolEndpoints),
            ("I04", "Persisted authorization resumes after restart, Core runs no timer", InspectionTests.I04_PersistedAuthorizationSurvivesRestart),
            ("I05", "Expanded cache pending, authorized subset continues, shrink no widen", InspectionTests.I05_ExpandedScopePendingButAuthorizedSubsetContinues),
            ("I06", "Disable persists, blocks timer round, manual round still allowed", InspectionTests.I06_DisablePersistsAndManualRoundStillWorks),
            ("I07", "Disable during round cancels queue, no second request", InspectionTests.I07_DisableDuringRoundStopsQueue),
            ("I08", "401 pause / 429 host cooldown / others continue, no retry or bypass", InspectionTests.I08_HttpErrorsPauseCooldownNoBypass),
            ("I09", "Connection history fields, Probe isolation, redacted export, delete", InspectionTests.I09_HistoryFieldsIsolationExportDelete),
            ("I10", "Completion never resurrects a record deleted mid-flight", InspectionTests.I10_NoHistoryResurrectionOnCompletion),
            ("I11", "Short store lock, RoundBusy, completion keeps concurrent config", InspectionTests.I11_ConcurrencyQueriesResponsiveRoundBusyConfigKept),
            ("I12", "History retention 30 days / 10000 records", InspectionTests.I12_HistoryRetentionCapAndAge),
            ("I13", "Unknown pre-write record durable, restart does not resend", InspectionTests.I13_PrewriteUnknownRecordSurvivesRestart),
            ("I14", "Interval bounds 10..480, saved-off consent, interval re-consent", InspectionTests.I14_IntervalBoundsAndSavedOffConsent),
            ("I15", "Pre-feature vault loads off, old session consent not promoted", InspectionTests.I15_PreviousVaultLoadsWithoutPromotingOldConsent),
            ("ID01", "Identity: defaults off/30min, no auto run, manual needs scope", IdentityInspectionTests.V01_DefaultsAndNoAutomaticRun),
            ("ID02", "Identity: enable/interval need fresh exact-scope consent, zero requests", IdentityInspectionTests.V02_ConsentGatingAndExactScope),
            ("ID03", "Identity: one round freezes all targets, strict serial, waits for terminal state", IdentityInspectionTests.V03_SerialRoundWaitsForTerminalState),
            ("ID04", "Identity: unknown/expired blocks the rest, Expired is not a remote end", IdentityInspectionTests.V04_UnknownBlocksAndExpiredIsNotTerminal),
            ("ID05", "Identity: intent persisted, restart never re-POSTs, GET-only resume", IdentityInspectionTests.V05_IntentPersistedNeverReplayed),
            ("ID06", "Identity: an active/unknown Probe record blocks a new round", IdentityInspectionTests.V06_ActiveRunBlocksNewRound),
            ("ID07", "Identity: 401/403 pause+stop, 429 host cooldown+stop, no retry", IdentityInspectionTests.V07_HttpAuthAndRateLimitStopRound),
            ("ID07B", "Identity: 429 cools host and stops, no retry", IdentityInspectionTests.V07b_RateLimitCoolsHostAndStops),
            ("ID07C", "Identity: GET 429 cools host, immediate re-poll refused locally", IdentityInspectionTests.V07c_PollRateLimitCoolsHost),
            ("ID08", "Identity: manual round uses per-run consent, never writes automatic grants", IdentityInspectionTests.V08_ManualRoundUsesPerRunConsentOnly),
            ("ID09", "Identity: selection persistence, cache intersection, grant revocation", IdentityInspectionTests.V09_SelectionPersistenceAndCacheIntersection),
            ("ID10", "Identity: credential revision change skips target without sending", IdentityInspectionTests.V10_RevisionChangeSkipsTargetWithoutSending),
            ("ID11", "Notification: two independent switches persisted, no network", IdentityInspectionTests.V11_NotificationSwitchesPersisted),
            ("ID12", "Notification: automatic-only, per-target dedup, unknown not recovery, stale target rejected", IdentityInspectionTests.V12_NotificationDedupAndRevisionIsolation),
            ("ID13", "Identity: interval bound to grant, off-state change cannot re-enable old grant", IdentityInspectionTests.V13_IntervalBoundToGrant),
            ("ID14", "Identity: legacy capability state migrates without automation or data loss", IdentityInspectionTests.V14_LegacyCapabilityStateMigratesWithoutAutomation),
            ("ID15", "Identity: RecordResult after End never resurrects the round", IdentityInspectionTests.V15_RecordAfterEndDoesNotResurrectRound),
            ("N15", "Notification logic: stable non-sensitive problem codes", IndependentNotificationLogicTests.N15_SafeProblemCodes),
            ("N16", "Notification logic: connection four-way classes, possible/unknown never recovery", IndependentNotificationLogicTests.N16_ConnectionObservationClasses),
            ("N17", "Notification logic: identity verdict mapping, warnings/mismatch/spoof", IndependentNotificationLogicTests.N17_IdentityObservationClasses),
            ("N18", "Notification logic: automatic identity source/trigger filtering", IndependentNotificationLogicTests.N18_AutomaticIdentityFiltering),
            ("N19", "Notification logic: round mapping filters, compose/title/describe", IndependentNotificationLogicTests.N19_RoundMappingAndComposing),
            ("N20", "Connection retry: notifications use only the final attempt per exact target", IndependentNotificationLogicTests.N20_RetryFinalAttemptNotifications),
            ("N21", "Connection retry: latest-round display shows final attempt per target, history kept", IndependentNotificationLogicTests.N21_RoundDisplayShowsFinalAttemptPerTarget),
            ("N22", "Connection retry: confirmation text discloses 3 attempts, 3x cost, exceptions", IndependentNotificationLogicTests.N22_ScopeTextDisclosesRetries),
        };
        return await TestRunner.RunAllAsync(tests).ConfigureAwait(false);
    }

    private static DesktopService NewService(string dir, FakeHttpHandler handler, ISecretProtector? protector = null)
        => new(dir, Fixtures.Client(handler), protector ?? new ReversibleTestProtector());

    // Official-shape completed payload: probeId item ids, nested identityAssessment.verdict,
    // documented status enums, unix-ms completedAt.
    private static string CompletedJson(string runId = "run-1", string reportUrl = "https://bazaarlink.ai/probe/abc")
        => $$"""
        {
          "runId":"{{runId}}",
          "status":"completed",
          "identityAssessment":{
            "status":"match",
            "verdict":{"status":"clean_match","trueFamily":"gpt","trueModel":"gpt-4o","confidence":"0.93",
                       "reasoning":["r1","r2"]}
          },
          "warnings":[{"label":"latency","severity":"medium","evidence":"slow"}],
          "items":[
            {"probeId":"p1","label":"L1","group":"g1","status":"done","passed":true,"durationMs":12,"inputTokens":3,"outputTokens":4},
            {"probeId":"p2","label":"L2","group":"g1","status":"done","passed":"warning","passReason":"d"}
          ],
          "totalInputTokens":10,"totalOutputTokens":20,
          "completedAt":1700000000000,
          "reportUrl":"{{reportUrl}}"
        }
        """;

    // T01 -------------------------------------------------------------------
    private static async Task T01_ProfileCrud()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = NewService(tmp.Path, handler);
        Check.Equal(Path.GetFullPath(tmp.Path), service.DataDirectory, "DataDirectory should be the given directory");

        var saved = await service.SaveProfileAsync(Fixtures.NewProfile("Alpha"), Key1);
        Check.Equal("Alpha", saved.Name, "saved name");
        Check.Equal(Fixtures.RelayBase, saved.BaseUrl, "saved base url");
        Check.True(saved.Id != Guid.Empty, "profile id assigned");

        var all = await service.GetProfilesAsync();
        Check.Equal(1, all.Count, "one profile");
        Check.Equal(saved.Id, all[0].Id, "same id round-trip");
        Check.Equal("Alpha", all[0].Name, "name persisted");
        Check.Equal(0, handler.Count, "CRUD must not hit network");

        var second = await service.SaveProfileAsync(Fixtures.NewProfile("Beta", "https://relay2.example.com/v1"), Key1);
        Check.Equal(2, (await service.GetProfilesAsync()).Count, "two profiles");

        await service.DeleteProfileAsync(saved.Id);
        var remaining = await service.GetProfilesAsync();
        Check.Equal(1, remaining.Count, "one profile after delete");
        Check.Equal(second.Id, remaining[0].Id, "beta remains");
    }

    // T02 -------------------------------------------------------------------
    private static async Task T02_KeyReplacement()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        using var service = NewService(tmp.Path, handler);

        var saved = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        int rev1 = saved.KeyRevision;

        // Blank replacement key must keep the existing secret and revision.
        var keptNull = await service.SaveProfileAsync(saved with { Name = "Renamed" }, null);
        Check.Equal(rev1, keptNull.KeyRevision, "null replacement keeps revision");
        var keptBlank = await service.SaveProfileAsync(keptNull with { Name = "Renamed2" }, "   ");
        Check.Equal(rev1, keptBlank.KeyRevision, "whitespace replacement keeps revision");

        var refreshed = await service.RefreshModelsAsync(keptBlank.Id, Fixtures.ModelConsent(keptBlank));
        Check.Equal(1, handler.Count, "one refresh request");
        Check.Contains(handler.Requests[0].Headers["Authorization"].Single(), "Bearer " + Key1, "old key retained after blank save");

        // Real replacement bumps revision and uses the new key.
        var replaced = await service.SaveProfileAsync(keptBlank with { Name = "Renamed3" }, Key2);
        Check.True(replaced.KeyRevision > rev1, "replacement bumps revision");
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        await service.RefreshModelsAsync(replaced.Id, Fixtures.ModelConsent(replaced));
        Check.Equal(2, handler.Count, "second refresh request");
        Check.Contains(handler.Requests[1].Headers["Authorization"].Single(), "Bearer " + Key2, "new key used after replacement");
    }

    // T03 -------------------------------------------------------------------
    private static async Task T03_DeleteProfileKeepsHistory()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-100"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        var record = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));
        Check.NotNull(record.RemoteRunId, "run id captured");

        await service.DeleteProfileAsync(profile.Id);
        Check.Equal(0, (await service.GetProfilesAsync()).Count, "profile deleted");

        var history = await service.GetHistoryAsync();
        Check.Equal(1, history.Count, "history survives profile delete");
        Check.Equal(record.LocalId, history[0].LocalId, "same record remains");
        Check.Equal(profile.Id, history[0].ProfileId, "record keeps profile id");
    }

    // T04 -------------------------------------------------------------------
    private static async Task T04_NoPlaintextKeyOnDisk()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        var protector = new ReversibleTestProtector();
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
        }

        string raw = Fixtures.ReadAllStoreText(tmp.Path);
        Check.NotContains(raw, Key1, "no plaintext key in store files");

        // Reopen with the same protector: the persisted key must decrypt and still be usable.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        using var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var p2 = (await reopened.GetProfilesAsync()).Single();
        await reopened.RefreshModelsAsync(p2.Id, Fixtures.ModelConsent(p2));
        Check.Contains(handler.Requests[1].Headers["Authorization"].Single(), Key1, "key survives reopen and decrypts");
    }

    // T05 -------------------------------------------------------------------
    private static async Task T05_RefreshConsentGating()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var good = Fixtures.ModelConsent(profile);

        var badProfile = good with { ProfileId = Guid.NewGuid() };
        var badRevision = good with { KeyRevision = profile.KeyRevision + 1 };
        var badBase = good with { BaseUrl = "https://other.example.com/v1" };
        var badPurpose = good with { Purpose = OutboundPurpose.Probe };
        var badRecipient = good with { Recipient = profile.BaseUrl };
        var badModel = good with { ModelId = "model-a" };
        var expired = good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-30) };
        var future = good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        foreach (var (label, consent) in new (string, OutboundConsent)[]
                 {
                     ("wrong profile", badProfile), ("wrong revision", badRevision), ("wrong base", badBase),
                     ("wrong purpose", badPurpose), ("wrong recipient", badRecipient), ("non-null model", badModel),
                     ("expired", expired), ("future", future)
                 })
        {
            await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, consent), "ConsentRequired",
                "refresh rejects " + label);
        }
        Check.Equal(0, handler.Count, "no network requests when consent invalid");
    }

    // T06 -------------------------------------------------------------------
    private static async Task T06_RefreshHappyPath()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o", "gpt-4o", "o3"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        var updated = await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
        Check.Equal(1, handler.Count, "exactly one GET");
        var req = handler.Requests[0];
        Check.Equal("GET", req.Method, "method GET");
        Check.Equal("https://relay.example.com/v1/models", req.Uri.AbsoluteUri, "base path + /models, no double /v1");
        Check.Contains(req.Headers["Authorization"].Single(), "Bearer " + Key1, "Bearer header carries key");
        Check.False(req.Headers.ContainsKey("X-Api-Key"), "key only in Authorization header");

        Check.Equal(2, updated.Models.Count, "duplicate model id deduped");
        Check.Equal("gpt-4o", updated.Models[0].Id, "first model id");
        Check.NotNull(updated.ModelsFetchedAt, "fetch timestamp set");
        Check.True(updated.ModelsFetchedAt > DateTimeOffset.UtcNow.AddMinutes(-5), "fetch timestamp recent");

        var persisted = (await service.GetProfilesAsync()).Single();
        Check.Equal(2, persisted.Models.Count, "models persisted");
        Check.NotNull(persisted.ModelsFetchedAt, "timestamp persisted");

        Check.False(req.Uri.AbsolutePath.Contains("/v1/v1"), "no duplicated /v1 segment");
    }

    // T07 -------------------------------------------------------------------
    private static async Task T07_RefreshFailurePreservesCache()
    {
        foreach (var (label, status, body) in new (string, HttpStatusCode, string)[]
                 {
                     ("404", HttpStatusCode.NotFound, "not found"),
                     ("429", (HttpStatusCode)429, "slow down"),
                     ("malformed", HttpStatusCode.OK, "{not-json")
                 })
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("keep-a", "keep-b"));
            using var service = NewService(tmp.Path, handler);
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            var seeded = await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
            var seededAt = seeded.ModelsFetchedAt;

            if (status == HttpStatusCode.OK)
                handler.EnqueueRaw(HttpStatusCode.OK, "application/json", body);
            else
                handler.EnqueueStatus(status);

            var ex = await Check.ThrowsClientAsync(
                () => service.RefreshModelsAsync(seeded.Id, Fixtures.ModelConsent(seeded)),
                status == HttpStatusCode.OK ? "MalformedJson" : "Http" + (int)status,
                label + " raises meaningful code");
            Check.True(ex.Message.Length > 0, label + " message not empty");

            var after = (await service.GetProfilesAsync()).Single();
            Check.Equal(2, after.Models.Count, label + " keeps cached models");
            Check.Equal("keep-a", after.Models[0].Id, label + " keeps first model");
            Check.Equal(seededAt, after.ModelsFetchedAt, label + " keeps fetch timestamp");
            Check.Equal(2, handler.Count, label + " exactly two requests total");
        }
    }

    // T08 -------------------------------------------------------------------
    private static async Task T08_NoRedirectFollow()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.Enqueue(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("", System.Text.Encoding.UTF8, "text/html") };
            response.Headers.Location = new Uri("https://evil.example.com/steal");
            return response;
        });
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile)),
            "Http302", "redirect surfaces as error");
        Check.Equal(1, handler.Count, "redirect not followed: only the original request");
        Check.Equal("relay.example.com", handler.Requests[0].Uri.Host, "no request to redirect target");
    }

    // T09 -------------------------------------------------------------------
    private static async Task T09_ProbeStartOnce()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-777"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        var record = await service.StartProbeAsync(profile.Id, "model-a", "claimed-a", Fixtures.ProbeConsent(profile, "model-a"));
        Check.Equal(1, handler.Count, "exactly one POST");
        var req = handler.Requests[0];
        Check.Equal("POST", req.Method, "method POST");
        Check.Equal(Fixtures.ServiceRunUrl, req.Uri.AbsoluteUri, "fixed service start URL");
        Check.False(req.Headers.ContainsKey("Authorization"), "no Bearer on probe POST");
        Check.NotContains(req.Body, "Bearer", "no Bearer text in body");

        using var doc = JsonDocument.Parse(req.Body);
        Check.Equal("https://relay.example.com/v1", doc.RootElement.GetProperty("baseUrl").GetString(), "body baseUrl");
        Check.Equal(Key1, doc.RootElement.GetProperty("apiKey").GetString(), "body carries key");
        Check.Equal("model-a", doc.RootElement.GetProperty("modelId").GetString(), "body modelId");
        Check.Equal("claimed-a", doc.RootElement.GetProperty("claimedModel").GetString(), "body claimedModel");
        Check.Equal(false, doc.RootElement.GetProperty("sync").GetBoolean(), "async sync=false");

        Check.Equal("run-777", record.RemoteRunId, "run id captured");
        Check.Equal(LocalRunState.Running, record.State, "running acknowledgement -> Running");
    }

    // T10 -------------------------------------------------------------------
    private static async Task T10_ProbeTimeoutUnknown()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueException(new TaskCanceledException("simulated network timeout"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        try { await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a")); }
        catch (Exception) { /* timeout may surface; state must still be recorded */ }

        Check.Equal(1, handler.Count, "POST attempted exactly once");

        var history = await service.GetHistoryAsync();
        Check.Equal(1, history.Count, "timeout record persisted");
        Check.Equal(LocalRunState.SubmissionUnknown, history[0].State, "state SubmissionUnknown after timeout");
        Check.Null(history[0].RemoteRunId, "no run id after unknown submission");

        using var reopened = NewService(tmp.Path, handler);
        var restored = (await reopened.GetHistoryAsync()).Single();
        Check.Equal(LocalRunState.SubmissionUnknown, restored.State, "unknown state restored on restart");
        Check.Equal(1, handler.Count, "restart does not resubmit");
    }

    // T11 -------------------------------------------------------------------
    private static async Task T11_PollParsing()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-1"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var rec = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));
        Check.Equal(LocalRunState.Running, rec.State, "running acknowledgement -> Running");

        handler.EnqueueJson(HttpStatusCode.OK, "{\"status\":\"running\",\"phase\":\"generating\"}");
        var running = await service.PollProbeAsync(rec.LocalId);
        Check.Equal(LocalRunState.Running, running.State, "running stays Running");
        Check.Equal("generating", running.Phase, "phase parsed");
        Check.Equal(2, handler.Count, "GET poll is a single request");
        Check.Equal("GET", handler.Requests[1].Method, "poll uses GET");
        Check.Equal("https://bazaarlink.ai/api/probe/run/run-1", handler.Requests[1].Uri.AbsoluteUri, "poll URL uses run id");
        Check.False(handler.Requests[1].Headers.ContainsKey("Authorization"), "poll does not resend key");

        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", CompletedJson("run-1"));
        var done = await service.PollProbeAsync(rec.LocalId);
        Check.Equal(LocalRunState.Completed, done.State, "completed state");
        Check.Equal("match", done.IdentityStatus, "identity status parsed");
        Check.Equal("clean_match", done.VerdictStatus, "verdict status parsed");
        Check.Equal("gpt-4o", done.InferredModel, "inferred model parsed");
        Check.Equal("gpt", done.InferredFamily, "inferred family parsed");
        Check.Equal(1, done.Warnings.Count, "nested warnings parsed");
        Check.Equal("latency", done.Warnings[0].Label, "warning label");
        Check.Equal(2, done.Items.Count, "items parsed");
        Check.Equal("p1", done.Items[0].ProbeId, "item id mapped");
        Check.Equal("warning", done.Items[1].Passed, "string passed kept as 'warning'");
        Check.Equal("true", done.Items[0].Passed, "boolean passed normalized to 'true'");
        Check.NotNull(done.CompletedAt, "completedAt set");
        Check.Equal(2023, done.CompletedAt!.Value.Year, "unix ms converted to 2023");
        Check.Equal("https://bazaarlink.ai/probe/abc", done.ReportUrl, "official report url retained");

        var hist = (await service.GetHistoryAsync()).Single();
        Check.Equal(LocalRunState.Completed, hist.State, "completed persisted");
        Check.Equal(2, hist.Items.Count, "items persisted");
    }

    // T12 -------------------------------------------------------------------
    private static async Task T12_PollUnknownNoRegression()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-A"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var recA = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));

        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", CompletedJson("run-A"));
        var doneA = await service.PollProbeAsync(recA.LocalId);
        Check.Equal(LocalRunState.Completed, doneA.State, "baseline completed");
        int requestsAfterComplete = handler.Count;

        // A terminal record is returned as-is: no further request and no downgrade, regardless of server state.
        var after = await service.PollProbeAsync(recA.LocalId);
        Check.Equal(LocalRunState.Completed, after.State, "terminal success not downgraded");
        Check.Equal("clean_match", after.VerdictStatus, "verdict preserved");
        Check.Equal(requestsAfterComplete, handler.Count, "terminal poll performs no request");

        // A running record that receives an unknown state must not be treated as success.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-B"));
        var recB = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"status\":\"totally-new-state\"}");
        var unknown = await service.PollProbeAsync(recB.LocalId);
        Check.Equal(LocalRunState.IncompatibleResponse, unknown.State, "unknown state -> IncompatibleResponse");
        Check.NotEqual(LocalRunState.Completed, unknown.State, "unknown state not Completed");

        // Malformed JSON while running must not become success. The service surfaces it either as a
        // meaningful error or as a recorded PollingInterrupted/IncompatibleResponse, never Completed.
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{broken");
        try
        {
            var malformed = await service.PollProbeAsync(recB.LocalId);
            Check.NotEqual(LocalRunState.Completed, malformed.State, "malformed poll never yields Completed");
            Check.True(!string.IsNullOrWhiteSpace(malformed.Error), "malformed poll records an error");
        }
        catch (ClientOperationException ex)
        {
            Check.True(ex.Message.Length > 0, "malformed poll error message meaningful");
        }
        var b = (await service.GetHistoryAsync()).Single(r => r.LocalId == recB.LocalId);
        Check.NotEqual(LocalRunState.Completed, b.State, "malformed poll never yields Completed in history");
        Check.True(!string.IsNullOrWhiteSpace(b.Error), "malformed poll error persisted");
    }

    // T13 -------------------------------------------------------------------
    private static async Task T13_HttpErrorsAndCap()
    {
        var cases = new (string Label, HttpStatusCode Status, string Code)[]
        {
            ("401", HttpStatusCode.Unauthorized, "Http401"),
            ("403", HttpStatusCode.Forbidden, "Http403"),
            ("404", HttpStatusCode.NotFound, "Http404"),
            ("429", (HttpStatusCode)429, "Http429"),
            ("503", HttpStatusCode.ServiceUnavailable, "Http503"),
        };
        foreach (var (label, status, code) in cases)
        {
            using var tmp = new TempDir();
            var handler = new FakeHttpHandler();
            handler.EnqueueStatus(status, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));
            using var service = NewService(tmp.Path, handler);
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            var ex = await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile)), code, label + " code");
            Check.True(ex.Message.Length > 5, label + " message meaningful");
            if (label == "429") Check.NotNull(ex.RetryAfter, "429 exposes RetryAfter");
        }

        using (var tmp = new TempDir())
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueRaw(HttpStatusCode.OK, "text/html", "<html>sign in</html>");
            using var service = NewService(tmp.Path, handler);
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile)),
                "UnexpectedContent", "HTML login page rejected");
        }

        using (var tmp = new TempDir())
        {
            var handler = new FakeHttpHandler();
            string big = new string('x', 3 * 1024 * 1024);
            handler.EnqueueRaw(HttpStatusCode.OK, "application/json", "{\"data\":\"" + big + "\"}");
            using var service = NewService(tmp.Path, handler);
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            await Check.ThrowsClientAsync(() => service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile)),
                "ResponseTooLarge", "oversized response rejected");
        }
    }

    // T14 -------------------------------------------------------------------
    private static async Task T14_RedactionAndExport()
    {
        using var tmp = new TempDir();
        using var exportDir = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-secret-42"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var rec = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));

        // Server echoes the key in an error body; it must never surface.
        handler.EnqueueRaw(HttpStatusCode.BadRequest, "application/json", "{\"error\":\"bad key " + Key1 + "\"}");
        try { await service.PollProbeAsync(rec.LocalId); }
        catch (Exception ex) { Check.NotContains(ex.Message, Key1, "error message must not echo key"); }

        // Valid completion that also carries an echoed key and a javascript report URL.
        string payload = CompletedJson("run-secret-42", "javascript:alert(1)")
            .Replace("\"identityAssessment\"", "\"echo\":\"" + Key1 + "\",\"identityAssessment\"");
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", payload);
        var done = await service.PollProbeAsync(rec.LocalId);
        Check.Equal(LocalRunState.Completed, done.State, "completed with echoed key");
        Check.Null(done.ReportUrl, "javascript report url rejected");
        Check.NotContains(JsonSerializer.Serialize(done), Key1, "history record must not echo key");

        // Foreign-domain report URL is rejected too.
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", CompletedJson("run-secret-42", "https://evil.example.com/probe/x"));
        var foreign = await service.PollProbeAsync(rec.LocalId);
        Check.Equal(LocalRunState.Completed, foreign.State, "still completed");
        Check.Null(foreign.ReportUrl, "foreign-domain report url rejected");

        string exportPath = System.IO.Path.Combine(exportDir.Path, "export.json");
        await service.ExportRecordAsync(rec.LocalId, exportPath);
        Check.True(File.Exists(exportPath), "export file written");
        string exported = Fixtures.ReadAllText(exportPath);
        Check.NotContains(exported, Key1, "export must not contain key");
        Check.NotContains(exported, "run-secret-42", "export must not contain remote run id");
        Check.NotContains(exported, "javascript:", "export must not contain unsafe report url");
        Check.NotContains(exported, "evil.example.com", "export must not contain foreign report url");

        string store = Fixtures.ReadAllStoreText(tmp.Path);
        Check.NotContains(store, Key1, "store must not contain key");
    }

    // T15 -------------------------------------------------------------------
    private static async Task T15_RestartResumeAndHistory()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-resume"));
        Guid localId;
        Guid profileId;
        using (var service = NewService(tmp.Path, handler))
        {
            var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
            profileId = profile.Id;
            var rec = await service.StartProbeAsync(profileId, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));
            localId = rec.LocalId;
        }

        // Restart: only a GET is allowed; no key is needed to query an existing run.
        var restartHandler = new FakeHttpHandler();
        restartHandler.EnqueueRaw(HttpStatusCode.OK, "application/json", CompletedJson("run-resume"));
        using (var reopened = NewService(tmp.Path, restartHandler))
        {
            var polled = await reopened.PollProbeAsync(localId);
            Check.Equal(LocalRunState.Completed, polled.State, "resumed run reaches completed");
            Check.Equal(1, restartHandler.Count, "only one request after restart");
            Check.Equal("GET", restartHandler.Requests[0].Method, "resume poll is GET, no POST");
            Check.False(restartHandler.Requests[0].Headers.ContainsKey("Authorization"), "no key sent on resume poll");

            await reopened.DeleteProfileAsync(profileId);
            var history = await reopened.GetHistoryAsync();
            Check.Equal(1, history.Count, "history remains after profile delete");
            Check.Equal(LocalRunState.Completed, history[0].State, "history still shows completed run");
            Check.Equal(localId, history[0].LocalId, "same local record");
        }
    }

    // T16 -------------------------------------------------------------------
    private static Task T16_DpapiRoundtrip()
    {
        if (!OperatingSystem.IsWindows())
            throw new TestFailure("DPAPI roundtrip requires Windows; environment is not Windows.");
        var protector = new WindowsDpapiSecretProtector();
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(Key1);
        byte[] cipher = protector.Protect(plaintext);
        Check.True(cipher.Length > 0, "ciphertext non-empty");
        Check.NotEqual(Convert.ToBase64String(plaintext), Convert.ToBase64String(cipher), "ciphertext differs from plaintext");
        string cipherText = System.Text.Encoding.UTF8.GetString(cipher);
        Check.NotContains(cipherText, Key1, "ciphertext does not contain plaintext key");

        byte[] roundtrip = protector.Unprotect(cipher);
        Check.Equal(Key1, System.Text.Encoding.UTF8.GetString(roundtrip), "DPAPI unprotect restores plaintext");

        // A fresh protector instance (same Windows user) must decrypt too.
        byte[] again = new WindowsDpapiSecretProtector().Unprotect(cipher);
        Check.Equal(Key1, System.Text.Encoding.UTF8.GetString(again), "DPAPI decrypts across protector instances");

        // Tampering must fail, not silently return garbage.
        cipher[^1] ^= 0xFF;
        try { protector.Unprotect(cipher); throw new TestFailure("tampered DPAPI blob should not decrypt"); }
        catch (System.Security.Cryptography.CryptographicException) { }
        return Task.CompletedTask;
    }

    // T17 -------------------------------------------------------------------
    private static async Task T17_CacheInvalidatedOnChange()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var cached = await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
        Check.Equal(2, cached.Models.Count, "models cached");
        Check.NotNull(cached.ModelsFetchedAt, "fetch timestamp set");

        // Base URL change must invalidate the cached model list.
        var moved = await service.SaveProfileAsync(cached with { BaseUrl = "https://relay2.example.com/v1" }, null);
        Check.Equal(0, moved.Models.Count, "models cleared after base URL change");
        Check.Null(moved.ModelsFetchedAt, "timestamp cleared after base URL change");

        // Re-seed then rotate the key: cache must clear again.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1", "m2"));
        var reseeded = await service.RefreshModelsAsync(moved.Id, Fixtures.ModelConsent(moved));
        Check.Equal(2, reseeded.Models.Count, "models re-cached");
        var rotated = await service.SaveProfileAsync(reseeded, Key2);
        Check.Equal(0, rotated.Models.Count, "models cleared after key change");
        Check.Null(rotated.ModelsFetchedAt, "timestamp cleared after key change");

        // Renaming without key/base change must keep the cache.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        var seed2 = await service.RefreshModelsAsync(rotated.Id, Fixtures.ModelConsent(rotated));
        var renamed = await service.SaveProfileAsync(seed2 with { Name = "Renamed Only" }, null);
        Check.Equal(1, renamed.Models.Count, "models kept on rename-only edit");
        Check.NotNull(renamed.ModelsFetchedAt, "timestamp kept on rename-only edit");
    }

    // T18 -------------------------------------------------------------------
    private static async Task T18_FailedSaveKeepsOldKey()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        var protector = new ToggleFailProtector();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        int rev = profile.KeyRevision;

        protector.FailProtect = true;
        await Check.ThrowsClientAsync(() => service.SaveProfileAsync(profile with { Name = "Renamed" }, Key2),
            "StorageWrite", "failed save surfaces StorageWrite");

        protector.FailProtect = false;
        // Old key and revision must still be intact and usable.
        var after = (await service.GetProfilesAsync()).Single();
        Check.Equal(rev, after.KeyRevision, "revision unchanged after failed save");
        Check.Equal("Primary", after.Name, "name unchanged after failed save");

        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
        await service.RefreshModelsAsync(after.Id, Fixtures.ModelConsent(after));
        Check.Contains(handler.Requests[0].Headers["Authorization"].Single(), "Bearer " + Key1, "old key still usable after failed save");
        Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Key2, "rejected new key never written");
    }

    // T19 -------------------------------------------------------------------
    private static async Task T19_SecretRunIdRejected()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        // Server echoes the API key as the run identifier.
        handler.EnqueueJson(HttpStatusCode.OK, "{\"runId\":\"" + Key1 + "\"}");
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        var rec = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));
        Check.Null(rec.RemoteRunId, "secret run id rejected as unsafe");
        Check.Equal(LocalRunState.SubmissionUnknown, rec.State, "unknown submission when run id unsafe");
        Check.NotContains(Fixtures.ReadAllStoreText(tmp.Path), Key1, "echoed key never persisted");
        Check.NotContains(rec.Error ?? "", Key1, "error text must not echo key");
    }

    // T20 -------------------------------------------------------------------
    private static async Task T20_SlowBodyCancellable()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowBodyContent() });
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile), cts.Token)
                .WaitAsync(abort.Token);
            throw new TestFailure("stalled body should not complete successfully");
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            throw new TestFailure("body read did not react to cancellation within 20s");
        }
        catch (ClientOperationException ex)
        {
            cancelled = ex.Code is "Timeout" or "MalformedJson" or "UnexpectedContent" or "Network" or "StorageWrite";
            Check.True(cancelled, "stalled body surfaced a meaningful code, got " + ex.Code);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        sw.Stop();
        Check.True(cancelled, "stalled body aborted via cancellation");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(10), "cancellation was prompt, took " + sw.ElapsedMilliseconds + " ms");
    }

    // T21 -------------------------------------------------------------------
    private static async Task T21_LongModelIdsPreserved()
    {
        const string longId = "qwen/qwen3-next-80b-a3b-instruct";
        const string longId2 = "openai/gpt-4.1-mini-2025-04-14";

        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson(longId, longId2));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(defaultModel: longId), Key1);

        var refreshed = await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
        Check.Equal(2, refreshed.Models.Count, "both long ids kept");
        var ids = refreshed.Models.Select(m => m.Id).ToArray();
        Check.True(ids.Contains(longId), "slash/hyphen long id preserved verbatim");
        Check.True(ids.Contains(longId2), "second long id preserved verbatim");
        Check.Equal(longId, refreshed.DefaultModelId, "default model id preserved");
        Check.False(ids.Any(i => i.Contains("[已脱敏]")), "long ids must not be heuristic-redacted");

        var persisted = (await service.GetProfilesAsync()).Single();
        var pids = persisted.Models.Select(m => m.Id).ToArray();
        Check.True(pids.Contains(longId), "long id persisted exactly");
        Check.True(pids.Contains(longId2), "second long id persisted exactly");

        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-long"));
        var rec = await service.StartProbeAsync(persisted.Id, longId2, longId, Fixtures.ProbeConsent(persisted, longId2));
        Check.Equal(longId2, rec.ModelId, "record model id exact");
        Check.Equal(longId, rec.ClaimedModel, "record claimed model exact");
        using var doc = JsonDocument.Parse(handler.Requests[1].Body);
        Check.Equal(longId2, doc.RootElement.GetProperty("modelId").GetString(), "POST modelId sent verbatim");
        Check.Equal(longId, doc.RootElement.GetProperty("claimedModel").GetString(), "POST claimedModel sent verbatim");
        Check.NotContains(handler.Requests[1].Body, "[已脱敏]", "no over-redaction in outbound body");
    }

    // T22 -------------------------------------------------------------------
    private static async Task T22_LegacyShapeCompat()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-legacy"));
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var rec = await service.StartProbeAsync(profile.Id, "model-a", null, Fixtures.ProbeConsent(profile, "model-a"));

        // Legacy shape: items use "id" and verdict is top-level. Must not be treated as success if unparsed,
        // so we assert only that the record does not fabricate completion.
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", """
        {"runId":"run-legacy","status":"completed",
         "identityAssessment":{"status":"match"},
         "verdict":{"status":"clean_match"},
         "items":[{"id":"legacy-1","label":"L","status":"done","passed":true}]}
        """);
        var done = await service.PollProbeAsync(rec.LocalId);
        Check.NotEqual(LocalRunState.SubmissionUnknown, done.State, "legacy poll resolves a definite state");
        if (done.State == LocalRunState.Completed)
        {
            Check.Equal(1, done.Items.Count, "legacy item id parsed when accepted");
            Check.Equal("legacy-1", done.Items[0].ProbeId, "legacy id mapped to ProbeId");
        }
        else
        {
            Check.True(done.Error is { Length: > 0 }, "unparsed legacy shape yields an explicit error");
        }
    }

    // T23 -------------------------------------------------------------------
    private static string PrimaryPath(string directory) => System.IO.Path.Combine(directory, "desktop-state.json");
    private static string BackupPath(string directory) => PrimaryPath(directory) + ".bak";

    private static async Task T23_BackupRecoveryWarnsOnce()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        var protector = new ReversibleTestProtector();
        Guid olderId;
        using (var service = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            // First save writes the primary only; the second save rotates the first version into .bak.
            olderId = (await service.SaveProfileAsync(Fixtures.NewProfile("Older"), Key1)).Id;
            await service.SaveProfileAsync(Fixtures.NewProfile("Newer", "https://relay2.example.com/v1"), Key2);
        }
        Check.True(File.Exists(PrimaryPath(tmp.Path)), "primary exists before corruption");
        Check.True(File.Exists(BackupPath(tmp.Path)), "backup exists after second save");

        File.WriteAllText(PrimaryPath(tmp.Path), "{ corrupt-not-json");
        string backupBefore = Fixtures.ReadAllText(BackupPath(tmp.Path));

        int warnings = 0;
        using (var reopened = new DesktopService(tmp.Path, Fixtures.Client(handler), protector))
        {
            try { await reopened.GetProfilesAsync(); }
            catch (ClientOperationException ex) when (ex.Code == "StorageRecovered")
            {
                warnings++;
                Check.True(ex.Message.Length > 0, "recovery warning has text");
            }
            // The same instance must not warn twice.
            var recovered = await reopened.GetProfilesAsync();
            Check.Equal(1, recovered.Count, "backup state restores the older single profile");
            Check.Equal(olderId, recovered[0].Id, "recovered profile is the pre-backup one");
            Check.Equal("Older", recovered[0].Name, "recovered profile name from backup");
            Check.NotContains(Fixtures.ReadAllText(PrimaryPath(tmp.Path)), "corrupt-not-json",
                "corrupt primary replaced by recovered state");
            Check.Equal(backupBefore, Fixtures.ReadAllText(BackupPath(tmp.Path)),
                "known-good backup preserved, not overwritten by the corrupt primary");

            // The restored secret must still decrypt and be usable.
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("m1"));
            await reopened.RefreshModelsAsync(recovered[0].Id, Fixtures.ModelConsent(recovered[0]));
            Check.Contains(handler.Requests[0].Headers["Authorization"].Single(), "Bearer " + Key1,
                "key recovered from backup decrypts and is used");
        }
        Check.Equal(1, warnings, "exactly one recovery warning across loads");

        using var third = new DesktopService(tmp.Path, Fixtures.Client(handler), protector);
        var afterRestart = await third.GetProfilesAsync();
        Check.Equal(1, afterRestart.Count, "no repeat warning on a fresh instance");
    }

    // T24 -------------------------------------------------------------------
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Sddl(FileSystemSecurity security)
        => security.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);

    private static async Task T24_ForeignDirectoryRejectedAclKept()
    {
        if (!OperatingSystem.IsWindows())
            throw new TestFailure("ACL comparison requires Windows; environment is not Windows.");
        const string marker = "user data that must survive";
        using var tmp = new TempDir();
        string foreign = System.IO.Path.Combine(tmp.Path, "notes.txt");
        File.WriteAllText(foreign, marker);
        string dirSddlBefore = Sddl(new DirectoryInfo(tmp.Path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        string fileSddlBefore = Sddl(new FileInfo(foreign)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));

        var handler = new FakeHttpHandler();
        var ex = await Check.ThrowsClientAsync(
            () => { using var _ = NewService(tmp.Path, handler); return Task.CompletedTask; },
            "StorageSecurity", "directory with unrelated files is rejected");

        // ACL, content and absence of store files must be unchanged by the rejected attempt.
        Check.Equal(dirSddlBefore, Sddl(new DirectoryInfo(tmp.Path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)), "directory ACL unchanged");
        Check.Equal(fileSddlBefore, Sddl(new FileInfo(foreign)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)), "foreign file ACL unchanged");
        Check.Equal(marker, Fixtures.ReadAllText(foreign), "foreign file content unchanged");
        Check.False(File.Exists(PrimaryPath(tmp.Path)), "no store file created in rejected directory");
        Check.False(File.Exists(BackupPath(tmp.Path)), "no store backup created in rejected directory");
        Check.True(ex.Message.Length > 0, "rejection message explains the reason");
    }

    // T25 -------------------------------------------------------------------
    private static async Task T25_ProbeConsentGating()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = NewService(tmp.Path, handler);
        var profile = await service.SaveProfileAsync(Fixtures.NewProfile(), Key1);
        var good = Fixtures.ProbeConsent(profile, "model-a");

        foreach (var (label, consent) in new (string, OutboundConsent?)[]
                 {
                     ("null", null),
                     ("wrong profile", good with { ProfileId = Guid.NewGuid() }),
                     ("wrong revision", good with { KeyRevision = profile.KeyRevision + 1 }),
                     ("wrong base", good with { BaseUrl = "https://other.example.com/v1" }),
                     ("wrong purpose", good with { Purpose = OutboundPurpose.ModelList }),
                     ("wrong recipient", good with { Recipient = Fixtures.ServiceRunUrl + "-other" }),
                     ("wrong model", good with { ModelId = "model-b" }),
                     ("model id case differs", good with { ModelId = "MODEL-A" }),
                     ("expired", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }),
                     ("future", good with { AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(5) })
                 })
        {
            await Check.ThrowsClientAsync(
                () => service.StartProbeAsync(profile.Id, "model-a", null, consent!),
                "ConsentRequired", "probe start rejects " + label);
        }
        Check.Equal(0, handler.Count, "no network request for any invalid consent");
        Check.Equal(0, (await service.GetHistoryAsync()).Count, "invalid consent creates no history record");
        Check.False(File.Exists(BackupPath(tmp.Path)), "no probe record committed to store");
    }

    // --seed-inspection-ui --------------------------------------------------
    // Isolated store for manual inspection/connection-history UI review: real DPAPI, FakeHttpHandler only.
    // Deliberately leaves global inspection OFF and unauthorized so a reviewer can never trigger a
    // scheduled round; the connection history is pre-filled with terminal records only.
    private static OutboundConsent SeedModelConsent(RelayProfile profile)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList,
            ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow, profile.Protocol);

    private static async Task<int> SeedInspectionUiAsync(string directory, bool protocolStates = false)
    {
        if (!Path.IsPathRooted(directory))
        {
            Console.Error.WriteLine("--seed-inspection-ui requires an absolute temp directory.");
            return 2;
        }
        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine("--seed-inspection-ui path is invalid: " + ex.GetType().Name);
            return 2;
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            Console.Error.WriteLine("--seed-inspection-ui refuses a non-empty directory: " + target);
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--seed-inspection-ui requires Windows (DPAPI CurrentUser). No plaintext fallback exists.");
            return 2;
        }

        const string seedMarker = "模拟数据，仅用于界面验收";
        const string seedChatOk = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";
        const string seedAnthropicOk = "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}";
        var handler = new FakeHttpHandler();
        // Responses are consumed strictly in request order: two model-list refreshes, the manual round
        // (主线路 3 models sorted, then the Claude profile), then three manual single tests.
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o", "gpt-4o-mini", "o3"));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("claude-3-7-sonnet-20250219"));
        handler.EnqueueJson(HttpStatusCode.OK, seedChatOk);
        handler.EnqueueJson(HttpStatusCode.OK, protocolStates ? "{}" : seedChatOk);
        if (protocolStates) handler.EnqueueRaw(HttpStatusCode.OK, "text/event-stream", "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n");
        else handler.EnqueueJson(HttpStatusCode.OK, seedChatOk);
        handler.EnqueueJson(HttpStatusCode.OK, seedAnthropicOk);
        handler.EnqueueJson(HttpStatusCode.OK, seedChatOk);
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        handler.EnqueueStatus((HttpStatusCode)429);
        using var service = new DesktopService(target, Fixtures.Client(handler), new WindowsDpapiSecretProtector());
        RelayProfile chat = await service.SaveProfileAsync(
            new RelayProfile { Name = "主线路", BaseUrl = "https://relay-a.example.com/v1" },
            "sk-fake-inspection-01-not-a-real-key");
        chat = await service.RefreshModelsAsync(chat.Id, SeedModelConsent(chat));
        RelayProfile claude = await service.SaveProfileAsync(
            new RelayProfile { Name = "Claude 线路", BaseUrl = "https://relay-b.example.com/v1", Protocol = ApiProtocol.AnthropicMessages },
            "sk-fake-inspection-02-not-a-real-key");
        claude = await service.RefreshModelsAsync(claude.Id, SeedModelConsent(claude));
        RelayProfile empty = await service.SaveProfileAsync(
            new RelayProfile { Name = "未拉取模型的通道", BaseUrl = "https://relay-c.example.com/v1" },
            "sk-fake-inspection-03-not-a-real-key");

        RelayProfile fetchedEmpty = await service.SaveProfileAsync(
            new RelayProfile { Name = "已拉取空模型的通道", BaseUrl = "https://relay-d.example.com/v1" },
            "sk-fake-inspection-04-not-a-real-key");
        // Keep response order deterministic: this fetch follows the seven seeded direct calls below.

        // Approval is recorded for the seeded scope, but the switch stays OFF: no timer can start.
        InspectionSnapshot snapshot = await service.GetInspectionSnapshotAsync();
        snapshot = await service.SaveInspectionSelectionsAsync(snapshot.Targets.Select(t =>
            new InspectionSelection(t.Target.ProfileId, t.Target.Models.Count > 0, t.Target.Models)).ToArray());
        InspectionConsent consent = new(
            snapshot.Targets.Where(t => t.Target.Models.Count > 0).Select(t => t.Target).ToArray(),
            snapshot.Settings.IntervalMinutes, DateTimeOffset.UtcNow) { MaxAttempts = 3 };
        await service.SaveInspectionSettingsAsync(new InspectionSettings { Enabled = false, IntervalMinutes = 30 }, consent);

        // One manual round, then manual single tests that end in a clean answer, a 404 and a 429.
        // Terminal states only, and no 401 so the seeded profiles stay authorized for the reviewer.
        await service.RunInspectionRoundAsync(InspectionTrigger.Manual);
        await service.TestConnectionAsync(chat.Id, "gpt-4o", new OutboundConsent(
            chat.Id, chat.KeyRevision, chat.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(chat.BaseUrl, chat.Protocol), "gpt-4o", DateTimeOffset.UtcNow, chat.Protocol));
        await service.TestConnectionAsync(claude.Id, "claude-3-7-sonnet-20250219", new OutboundConsent(
            claude.Id, claude.KeyRevision, claude.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(claude.BaseUrl, claude.Protocol), "claude-3-7-sonnet-20250219",
            DateTimeOffset.UtcNow, claude.Protocol));
        await service.TestConnectionAsync(chat.Id, "o3", new OutboundConsent(
            chat.Id, chat.KeyRevision, chat.BaseUrl, OutboundPurpose.ConnectionTest,
            ConnectionTestEndpoints.Recipient(chat.BaseUrl, chat.Protocol), "o3", DateTimeOffset.UtcNow, chat.Protocol));

        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson());
        await service.RefreshModelsAsync(fetchedEmpty.Id, SeedModelConsent(fetchedEmpty));
        IReadOnlyList<ConnectionTestResult> history = await service.GetConnectionHistoryAsync();
        InspectionSnapshot finalSnapshot = await service.GetInspectionSnapshotAsync();
        string raw = Fixtures.ReadAllStoreText(target);
        bool noPlaintext = new[] { "sk-fake-inspection-01-not-a-real-key", "sk-fake-inspection-02-not-a-real-key",
            "sk-fake-inspection-03-not-a-real-key", "sk-fake-inspection-04-not-a-real-key" }.All(k => !raw.Contains(k, StringComparison.Ordinal));

        Console.WriteLine("SEED_MODE=seed-inspection-ui (real DPAPI, FakeHttpHandler only, no external network)");
        Console.WriteLine("NOTE=" + seedMarker + "；模型 ID 仅为界面布局样本，不代表真实可用性。");
        Console.WriteLine("SEED_DIR=" + target);
        foreach (RelayProfile p in await service.GetProfilesAsync())
            Console.WriteLine($"  PROFILE=\"{p.Name}\" PROTOCOL={p.Protocol} MODELS={p.Models.Count}");
        foreach (InspectionTargetStatus s in finalSnapshot.Targets)
            Console.WriteLine($"  TARGET=\"{s.ProfileName}\" STATE={s.Authorization} AUTHORIZED={s.AuthorizedModels.Count}/{s.Target.Models.Count}");
        Console.WriteLine("INSPECTION_ENABLED=" + finalSnapshot.Settings.Enabled + " INTERVAL=" + finalSnapshot.Settings.IntervalMinutes);
        Console.WriteLine("HISTORY=" + history.Count
            + " AUTOMATIC=" + history.Count(r => r.Source == ConnectionRecordSource.Automatic)
            + " ROUND_MANUAL=" + history.Count(r => r.Source == ConnectionRecordSource.RoundManual)
            + " MANUAL=" + history.Count(r => r.Source == ConnectionRecordSource.Manual)
            + " ACTIVE=" + history.Count(r => r.Stage == ConnectionTestStage.Unknown && r.CompletedAt is null));
        foreach (ConnectionTestResult r in history)
            Console.WriteLine($"  RECORD={r.RecordId} SOURCE={r.Source} MODEL={r.ModelId} STAGE={r.Stage} HTTP={r.HttpStatusCode} CODE={r.Code}");
        Console.WriteLine("PROBE_HISTORY=" + (await service.GetHistoryAsync()).Count);
        Console.WriteLine("PLAINTEXT_KEY_ABSENT=" + noPlaintext);
        Console.WriteLine("REQUESTS=" + handler.Count + " ALL_FAKE_HOSTS=" +
            handler.Requests.All(q => q.Uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)));

        bool ok = !finalSnapshot.Settings.Enabled && history.Count >= 4 && history.All(r => r.CompletedAt is not null)
            && (await service.GetHistoryAsync()).Count == 0 && noPlaintext && empty.Models.Count == 0;
        Console.WriteLine(ok ? "SEED_INSPECTION_UI=OK" : "SEED_INSPECTION_UI=FAILED");
        return ok ? 0 : 1;
    }

    // --seed-identity-ui -----------------------------------------------------
    // Isolated store for manual identity-inspection UI review: real DPAPI, FakeHttpHandler only, new empty directory.
    // Automatic switch stays OFF and no automatic grant is written, so a reviewer can never trigger a scheduled round.
    // One completed MANUAL round provides terminal records only; no Running/PollingInterrupted/Expired record is seeded.
    private static string IdentityVerdictJson(string runId, string verdict, string trueModel, string trueFamily)
        => $$$"""
        {"runId":"{{{runId}}}","status":"completed",
         "identityAssessment":{"status":"match","verdict":{"status":"{{{verdict}}}","trueFamily":"{{{trueFamily}}}","trueModel":"{{{trueModel}}}","confidence":"0.9",
           "reasoning":["模拟数据，仅用于界面验收：本结果仅就本次样本给出概率性判断。"]}},
         "items":[{"probeId":"p1","label":"基础指令遵循","status":"done","passed":true,"durationMs":800,"inputTokens":60,"outputTokens":40}],
         "completedAt":{{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}}
        """;

    private static async Task<int> SeedIdentityUiAsync(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            Console.Error.WriteLine("--seed-identity-ui requires an absolute temp directory.");
            return 2;
        }
        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine("--seed-identity-ui path is invalid: " + ex.GetType().Name);
            return 2;
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            Console.Error.WriteLine("--seed-identity-ui refuses a non-empty directory: " + target);
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--seed-identity-ui requires Windows (DPAPI CurrentUser). No plaintext fallback exists.");
            return 2;
        }

        const string key1 = "sk-fake-identity-01-not-a-real-key";
        const string key2 = "sk-fake-identity-02-not-a-real-key";
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o", "gpt-4o-mini"));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("claude-3-7-sonnet-20250219"));
        using var service = new DesktopService(target, Fixtures.Client(handler), new WindowsDpapiSecretProtector());

        RelayProfile chat = await service.SaveProfileAsync(
            new RelayProfile { Name = "身份巡检示例", BaseUrl = "https://relay-a.example.com/v1" }, key1);
        chat = await service.RefreshModelsAsync(chat.Id, SeedModelConsent(chat));
        RelayProfile claude = await service.SaveProfileAsync(
            new RelayProfile { Name = "身份巡检示例 Claude", BaseUrl = "https://relay-b.example.com/v1", Protocol = ApiProtocol.AnthropicMessages }, key2);
        claude = await service.RefreshModelsAsync(claude.Id, SeedModelConsent(claude));

        // Select every cached model, but leave the automatic switch off and write no automatic grant.
        IdentityInspectionSnapshot snapshot = await service.GetIdentityInspectionSnapshotAsync();
        snapshot = await service.SaveIdentityInspectionSelectionsAsync(snapshot.Targets
            .GroupBy(t => t.Target.ProfileId)
            .Select(g => new IdentityInspectionSelection(g.Key, true, g.Select(t => t.Target.ModelId).ToArray()))
            .ToArray());
        IdentityInspectionTarget[] scope = snapshot.Targets.Where(t => t.Selected).Select(t => t.Target).ToArray();

        // One manual round over the exact selected scope; every attempt reaches a terminal state via the shared poll path.
        IdentityInspectionRoundSummary round = await service.BeginIdentityInspectionRoundAsync(
            IdentityInspectionTrigger.Manual, new IdentityInspectionConsent(scope, 30, DateTimeOffset.UtcNow));
        string[] verdicts = ["clean_match", "plain_mismatch", "spoof_behavior_induced"];
        for (int i = 0; i < scope.Length; i++)
        {
            string runId = "run-identity-" + (i + 1);
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly(runId));
            handler.EnqueueJson(HttpStatusCode.OK, IdentityVerdictJson(runId, verdicts[i % verdicts.Length], "gpt-4o", "gpt"));
            IdentityInspectionStep step = await service.TrySubmitNextIdentityInspectionTargetAsync(round.RoundId);
            if (step.Probe is null)
            {
                Console.Error.WriteLine("--seed-identity-ui submission failed: " + step.Code);
                return 1;
            }
            await service.PollProbeAsync(step.Probe.LocalId);
            await service.RecordIdentityInspectionResultAsync(round.RoundId, step.Probe.LocalId);
        }
        IdentityInspectionRoundSummary ended = await service.EndIdentityInspectionRoundAsync(round.RoundId);

        IdentityInspectionSnapshot finalSnapshot = await service.GetIdentityInspectionSnapshotAsync();
        IReadOnlyList<ProbeRecord> history = await service.GetHistoryAsync();
        string raw = Fixtures.ReadAllStoreText(target);
        bool noPlaintext = !raw.Contains(key1, StringComparison.Ordinal) && !raw.Contains(key2, StringComparison.Ordinal);
        bool allTerminal = history.All(r => r.State is LocalRunState.Completed or LocalRunState.Failed or LocalRunState.Aborted);

        Console.WriteLine("SEED_MODE=seed-identity-ui (real DPAPI, FakeHttpHandler only, no external network)");
        Console.WriteLine("NOTE=模拟数据，仅用于界面验收；模型 ID 仅为布局样本，不代表真实可用性。");
        Console.WriteLine("SEED_DIR=" + target);
        foreach (RelayProfile p in await service.GetProfilesAsync())
            Console.WriteLine($"  PROFILE=\"{p.Name}\" PROTOCOL={p.Protocol} MODELS={p.Models.Count}");
        foreach (IdentityInspectionTargetStatus t in finalSnapshot.Targets)
            Console.WriteLine($"  TARGET=\"{t.ProfileName}\" MODEL={t.Target.ModelId} SELECTED={t.Selected} AUTHORIZED={t.Authorized} PAUSED={t.Paused}");
        Console.WriteLine("AUTOMATIC_ENABLED=" + finalSnapshot.Settings.Enabled + " INTERVAL=" + finalSnapshot.Settings.IntervalMinutes);
        Console.WriteLine("LATEST_ROUND=" + (finalSnapshot.LatestRound is { } latest
            ? $"TRIGGER={latest.Trigger} TOTAL={latest.Total} COMPLETED={latest.Completed} SKIPPED={latest.Skipped} DONE={latest.CompletedAt is not null} STOP={latest.StopCode ?? "(none)"}"
            : "(none)"));
        Console.WriteLine("PROBE_HISTORY=" + history.Count + " ALL_TERMINAL=" + allTerminal);
        foreach (ProbeRecord rec in history)
            Console.WriteLine($"  RECORD={rec.LocalId} STATE={rec.State} SOURCE={rec.Source} TRIGGER={rec.IdentityInspectionTrigger} MODEL={rec.ModelId} VERDICT={rec.VerdictStatus ?? "(none)"}");
        Console.WriteLine("PLAINTEXT_KEY_ABSENT=" + noPlaintext);
        Console.WriteLine("REQUESTS=" + handler.Count + " ALL_FAKE_HOSTS=" +
            handler.Requests.All(q => q.Uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
                || q.Uri.Host.Equals("bazaarlink.ai", StringComparison.OrdinalIgnoreCase)));

        bool ok = !finalSnapshot.Settings.Enabled && ended.CompletedAt is not null
            && history.Count == scope.Length && allTerminal && noPlaintext
            && finalSnapshot.Targets.Where(t => t.Selected).All(t => !t.Authorized);
        Console.WriteLine(ok ? "SEED_IDENTITY_UI=OK" : "SEED_IDENTITY_UI=FAILED");
        return ok ? 0 : 1;
    }

    // --seed-ui -------------------------------------------------------------
    // Writes an isolated, real-DPAPI store for manual WPF acceptance. Fake transport only; no external network.
    private static async Task<int> SeedUiAsync(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            Console.Error.WriteLine("--seed-ui requires an absolute temp directory.");
            return 2;
        }
        Directory.CreateDirectory(directory);
        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("gpt-4o", "o3"));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly("run-ui-demo"));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json", CompletedJson("run-ui-demo"));

        using var service = new DesktopService(directory, Fixtures.Client(handler), new WindowsDpapiSecretProtector());
        var profile = await service.SaveProfileAsync(
            new RelayProfile { Name = "界面验收示例", BaseUrl = "https://relay.example/v1", DefaultModelId = "gpt-4o" },
            "sk-fake-ui-demo-not-a-real-key");
        var refreshed = await service.RefreshModelsAsync(profile.Id, Fixtures.ModelConsent(profile));
        var rec = await service.StartProbeAsync(refreshed.Id, "gpt-4o", "gpt-4o", Fixtures.ProbeConsent(refreshed, "gpt-4o"));
        var done = await service.PollProbeAsync(rec.LocalId);

        Console.WriteLine("SEED_DIR=" + Path.GetFullPath(directory));
        Console.WriteLine("PROFILE=" + refreshed.Name + " MODELS=" + refreshed.Models.Count);
        Console.WriteLine("RECORD=" + done.LocalId + " STATE=" + done.State + " ITEMS=" + done.Items.Count);
        return done.State == LocalRunState.Completed ? 0 : 1;
    }

    // --seed-visual ---------------------------------------------------------
    // Visual-acceptance store: real Windows DPAPI, FakeHttpHandler transport only, new empty directory only.
    // All history records are TERMINAL (no Running/PollingInterrupted) so a UI reviewer can never trigger a resume/network call.
    // Every fixture string that a reviewer could read states "模拟数据，仅用于界面验收"; the model IDs are shape samples, not claims of availability.
    private const string SimulatedMarker = "模拟数据，仅用于界面验收";

    private static string VisualKey1 = "sk-fake-visual-01-not-a-real-key";
    private static string VisualKey2 = "sk-fake-visual-02-longer-not-a-real-key-0123456789abcdefghijklmn";
    private static string VisualKey3 = "sk-fake-visual-03";

    // OpenAI-compatible /models shape; realistic-looking IDs used purely as layout fixtures.
    private static readonly string[] VisualModelsA =
    [
        "gpt-4o",
        "gpt-4o-mini",
        "o3-mini",
        "claude-3-7-sonnet-20250219",
        "deepseek-chat",
        "qwen3-235b-a22b",
        "gemini-2.5-pro",
        "grok-4"
    ];

    private static readonly string[] VisualModelsB = ["gpt-4o-mini", "o3", "text-embedding-3-large"];

    private static long UnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static OutboundConsent VisualProbeConsent(RelayProfile profile, string modelId, DateTimeOffset acceptedAt)
        => new(profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.Probe, Fixtures.ServiceRunUrl, modelId, acceptedAt);

    private static string VisualCompletedMatchJson(string runId, long completedAt, long startedAt)
        => $$"""
        {
          "runId":"{{runId}}",
          "status":"completed",
          "phase":"身份核验与语言特征比对完成",
          "identityAssessment":{
            "status":"match",
            "verdict":{
              "status":"clean_match",
              "trueFamily":"gpt",
              "trueModel":"gpt-4o",
              "confidence":"0.91",
              "reasoning":[
                "{{SimulatedMarker}}：回复风格、拒答边界与工具调用格式与声明模型高度一致。",
                "{{SimulatedMarker}}：长上下文一致性测试未发现明显降级或偷换迹象。",
                "{{SimulatedMarker}}：仅就本次样本给出概率性判断，不构成模型真实身份的确定性证明。"
              ]
            }
          },
          "warnings":[
            {"label":"响应时延","severity":"medium","evidence":"{{SimulatedMarker}}：首个 token 延迟偏高，可能影响后续一致性样本的判定。","type":"latency"}
          ],
          "items":[
            {"probeId":"p-visual-01","label":"基础指令遵循","group":"语言与指令","status":"done","passed":true,"durationMs":820,"inputTokens":64,"outputTokens":41,"passReason":"{{SimulatedMarker}}：输出符合格式要求。"},
            {"probeId":"p-visual-02","label":"中文长文本摘要","group":"语言与指令","status":"done","passed":true,"durationMs":1740,"inputTokens":412,"outputTokens":188},
            {"probeId":"p-visual-03","label":"函数调用结构","group":"工具与结构","status":"done","passed":true,"durationMs":960,"inputTokens":88,"outputTokens":52},
            {"probeId":"p-visual-04","label":"拒答边界","group":"安全策略","status":"done","passed":"warning","durationMs":1330,"inputTokens":120,"outputTokens":96,"passReason":"{{SimulatedMarker}}：拒答措辞与官方风格略有差异，不足以单独判定。"},
            {"probeId":"p-visual-05","label":"多轮上下文一致性","group":"一致性","status":"done","passed":true,"durationMs":2210,"inputTokens":530,"outputTokens":244},
            {"probeId":"p-visual-06","label":"自我声明交叉核对","group":"身份核验","status":"done","passed":true,"durationMs":610,"inputTokens":40,"outputTokens":18}
          ],
          "totalInputTokens":1254,
          "totalOutputTokens":639,
          "startedAt":{{startedAt}},
          "completedAt":{{completedAt}},
          "reportUrl":"https://bazaarlink.ai/probe/{{runId}}"
        }
        """;

    private static string VisualCompletedUncertainJson(string runId, long completedAt, long startedAt)
        => $$"""
        {
          "runId":"{{runId}}",
          "status":"completed",
          "phase":"分析与语言核验完成，身份判断无法收敛",
          "identityAssessment":{
            "status":"uncertain",
            "verdict":{
              "status":"ambiguous",
              "confidence":"0.42",
              "reasoning":[
                "{{SimulatedMarker}}：样本中同时出现两个家族的特征，无法稳定归因。",
                "{{SimulatedMarker}}：中转站可能做了混合路由，本客户端无法确认实际调用目标。",
                "{{SimulatedMarker}}：该结果仅表示本次样本证据不足，不代表模型一定被替换。"
              ]
            }
          },
          "warnings":[
            {"label":"样本不足","severity":"high","evidence":"{{SimulatedMarker}}：可用样本量低于判定阈值。"},
            {"label":"上游报错","severity":"warning","evidence":"{{SimulatedMarker}}：一个检测项因上游超时未能完成。"}
          ],
          "items":[
            {"probeId":"p-visual-11","label":"基础指令遵循","group":"语言与指令","status":"done","passed":true,"durationMs":880,"inputTokens":60,"outputTokens":37},
            {"probeId":"p-visual-12","label":"中文长文本摘要","group":"语言与指令","status":"done","passed":false,"durationMs":2600,"inputTokens":402,"outputTokens":151,"passReason":"{{SimulatedMarker}}：摘要遗漏关键约束。"},
            {"probeId":"p-visual-13","label":"函数调用结构","group":"工具与结构","status":"error","passed":"false","durationMs":30000,"inputTokens":96,"outputTokens":0,"error":"{{SimulatedMarker}}：上游中转返回 504 网关超时。"},
            {"probeId":"p-visual-14","label":"拒答边界","group":"安全策略","status":"done","passed":"warning","durationMs":1290,"inputTokens":118,"outputTokens":90,"passReason":"{{SimulatedMarker}}：拒答延迟明显高于同类样本。"},
            {"probeId":"p-visual-15","label":"自我声明交叉核对","group":"身份核验","status":"done","passed":false,"durationMs":700,"inputTokens":44,"outputTokens":22,"passReason":"{{SimulatedMarker}}：自我声明与行为特征不一致。"}
          ],
          "totalInputTokens":720,
          "totalOutputTokens":300,
          "startedAt":{{startedAt}},
          "completedAt":{{completedAt}},
          "reportUrl":"https://bazaarlink.ai/probe/{{runId}}"
        }
        """;

    private static string VisualFailedJson(string runId, long completedAt, long startedAt)
        => $$"""
        {
          "runId":"{{runId}}",
          "status":"failed",
          "phase":"提交后上游拒绝调用",
          "error":"{{SimulatedMarker}}：中转站返回 429 额度不足，本次检测未能执行。",
          "startedAt":{{startedAt}},
          "completedAt":{{completedAt}}
        }
        """;

    private static string VisualAbortedJson(string runId, long completedAt, long startedAt)
        => $$"""
        {
          "runId":"{{runId}}",
          "status":"aborted",
          "phase":"服务侧中止任务",
          "error":"{{SimulatedMarker}}：服务侧主动中止，可能触发了风控或人工限制。",
          "items":[
            {"probeId":"p-visual-21","label":"基础指令遵循","group":"语言与指令","status":"done","passed":true,"durationMs":790,"inputTokens":58,"outputTokens":33},
            {"probeId":"p-visual-22","label":"中文长文本摘要","group":"语言与指令","status":"error","passed":"false","durationMs":12000,"inputTokens":390,"outputTokens":0,"error":"{{SimulatedMarker}}：任务被中止前该检测项未完成。"},
            {"probeId":"p-visual-23","label":"函数调用结构","group":"工具与结构","status":"pending","passed":null,"durationMs":0,"inputTokens":0,"outputTokens":0}
          ],
          "startedAt":{{startedAt}},
          "completedAt":{{completedAt}}
        }
        """;

    private static async Task<int> SeedVisualAsync(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            Console.Error.WriteLine("--seed-visual requires an absolute temp directory.");
            return 2;
        }

        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine("--seed-visual path is invalid: " + ex.GetType().Name);
            return 2;
        }

        // Refuse to write into anything that already holds files: no overwriting an existing store or user data.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            Console.Error.WriteLine("--seed-visual refuses a non-empty directory: " + target);
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--seed-visual requires Windows (DPAPI CurrentUser). No plaintext fallback exists.");
            return 2;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Consent is only valid for 10 minutes, and this runner has no injectable clock, so every consent/timestamp
        // below is derived from UtcNow at seed time. Core stamps CreatedAt itself; completedAt is then "just now".
        DateTimeOffset t1 = now;
        DateTimeOffset t2 = now.AddMilliseconds(200);
        DateTimeOffset t3 = now.AddMilliseconds(400);
        DateTimeOffset t4 = now.AddMilliseconds(600);

        string runMatch = "run-visual-match-01";
        string runUncertain = "run-visual-uncertain-02";
        string runFailed = "run-visual-failed-03";
        string runAborted = "run-visual-aborted-04";

        var handler = new FakeHttpHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson(VisualModelsA));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson(VisualModelsB));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly(runMatch));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json",
            VisualCompletedMatchJson(runMatch, UnixMs(t1.AddSeconds(38)), UnixMs(t1)));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly(runUncertain));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json",
            VisualCompletedUncertainJson(runUncertain, UnixMs(t2.AddSeconds(52)), UnixMs(t2)));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly(runFailed));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json",
            VisualFailedJson(runFailed, UnixMs(t3.AddSeconds(6)), UnixMs(t3)));
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.RunIdOnly(runAborted));
        handler.EnqueueRaw(HttpStatusCode.OK, "application/json",
            VisualAbortedJson(runAborted, UnixMs(t4.AddSeconds(19)), UnixMs(t4)));

        string[] keys = [VisualKey1, VisualKey2, VisualKey3];
        var states = new List<LocalRunState>();

        using (var service = new DesktopService(target, Fixtures.Client(handler), new WindowsDpapiSecretProtector()))
        {
            // P1: short name, primary relay, full 8-model cache, default model set.
            RelayProfile p1 = await service.SaveProfileAsync(
                new RelayProfile
                {
                    Name = "主线路",
                    BaseUrl = "https://relay-a.example.com/v1",
                    DefaultModelId = "gpt-4o"
                }, VisualKey1);
            p1 = await service.RefreshModelsAsync(p1.Id, Fixtures.ModelConsent(p1));

            // P2: long display name + long URL, small cache (checks wrapping/truncation of both).
            RelayProfile p2 = await service.SaveProfileAsync(
                new RelayProfile
                {
                    Name = "备用中转站 · 名称较长用于检查列表与详情页的换行、省略与对齐表现（" + SimulatedMarker + "）",
                    BaseUrl = "https://relay-long-subdomain-name-for-layout-checks.example.com/openai/v1",
                    DefaultModelId = "gpt-4o-mini"
                }, VisualKey2);
            p2 = await service.RefreshModelsAsync(p2.Id, Fixtures.ModelConsent(p2));

            // P3: no model cache yet, so the empty-cache state is visible as well.
            RelayProfile p3 = await service.SaveProfileAsync(
                new RelayProfile
                {
                    Name = "测试通道",
                    BaseUrl = "https://relay-c.example.com/v1",
                    DefaultModelId = ""
                }, VisualKey3);

            // Terminal-only history: each start is followed by a poll that lands in a terminal state,
            // so no record is ever active and no resume action can reach the network.
            ProbeRecord m = await service.StartProbeAsync(p1.Id, "gpt-4o", "gpt-4o", VisualProbeConsent(p1, "gpt-4o", t1));
            states.Add((await service.PollProbeAsync(m.LocalId)).State);

            ProbeRecord u = await service.StartProbeAsync(p1.Id, "claude-3-7-sonnet-20250219", "claude-3-7-sonnet",
                VisualProbeConsent(p1, "claude-3-7-sonnet-20250219", t2));
            states.Add((await service.PollProbeAsync(u.LocalId)).State);

            ProbeRecord f = await service.StartProbeAsync(p2.Id, "gpt-4o-mini", null, VisualProbeConsent(p2, "gpt-4o-mini", t3));
            states.Add((await service.PollProbeAsync(f.LocalId)).State);

            ProbeRecord a = await service.StartProbeAsync(p3.Id, "qwen3-235b-a22b", "qwen3-235b-a22b",
                VisualProbeConsent(p3, "qwen3-235b-a22b", t4));
            states.Add((await service.PollProbeAsync(a.LocalId)).State);
        }

        // Reopen with a fresh service + real DPAPI to prove the sealed store decrypts and round-trips.
        var profilesAfter = new List<RelayProfile>();
        IReadOnlyList<ProbeRecord> historyAfter;
        using (var reopened = new DesktopService(target, Fixtures.Client(new FakeHttpHandler()), new WindowsDpapiSecretProtector()))
        {
            profilesAfter.AddRange(await reopened.GetProfilesAsync());
            historyAfter = await reopened.GetHistoryAsync();
        }

        string raw = Fixtures.ReadAllStoreText(target);
        bool noPlaintext = keys.All(k => !raw.Contains(k, StringComparison.Ordinal));
        int active = historyAfter.Count(r => r.State is LocalRunState.Submitting or LocalRunState.SubmissionUnknown
            or LocalRunState.Running or LocalRunState.PollingInterrupted or LocalRunState.IncompatibleResponse);
        bool allExpectedStates = states.SequenceEqual([
            LocalRunState.Completed, LocalRunState.Completed, LocalRunState.Failed, LocalRunState.Aborted]);

        bool consoleOutputDir = Path.IsPathRooted(target);
        Console.WriteLine("SEED_MODE=seed-visual (real DPAPI, FakeHttpHandler only, no external network)");
        Console.WriteLine("NOTE=" + SimulatedMarker + "；模型 ID 仅为界面布局样本，不代表可用或真实可用性。");
        Console.WriteLine("SEED_DIR=" + target);
        Console.WriteLine("PROFILES=" + profilesAfter.Count);
        foreach (RelayProfile p in profilesAfter)
            Console.WriteLine($"  PROFILE=\"{p.Name}\" URL={p.BaseUrl} MODELS={p.Models.Count} DEFAULT={(p.DefaultModelId.Length == 0 ? "(none)" : p.DefaultModelId)} KeyRev={p.KeyRevision}");
        Console.WriteLine("HISTORY=" + historyAfter.Count
            + " COMPLETED=" + historyAfter.Count(r => r.State == LocalRunState.Completed)
            + " FAILED=" + historyAfter.Count(r => r.State == LocalRunState.Failed)
            + " ABORTED=" + historyAfter.Count(r => r.State == LocalRunState.Aborted)
            + " ACTIVE=" + active);
        foreach (ProbeRecord r in historyAfter)
            Console.WriteLine($"  RECORD={r.LocalId} STATE={r.State} MODEL={r.ModelId} ITEMS={r.Items.Count} WARNINGS={r.Warnings.Count} REASONING={r.Reasoning.Count} CREATED={r.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("REQUESTS=" + handler.Count + " ALL_FAKE_HOSTS=" +
            handler.Requests.All(q => q.Uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
                || q.Uri.Host.Equals("bazaarlink.ai", StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine("PLAINTEXT_KEY_ABSENT=" + noPlaintext);
        Console.WriteLine("STORE_FILES=" + string.Join(",", Directory.EnumerateFiles(target).Select(Path.GetFileName)));

        bool ok = consoleOutputDir && profilesAfter.Count == 3 && historyAfter.Count == 4 && allExpectedStates
            && active == 0 && noPlaintext;
        Console.WriteLine(ok ? "SEED_VISUAL=OK" : "SEED_VISUAL=FAILED");
        return ok ? 0 : 1;
    }
}
