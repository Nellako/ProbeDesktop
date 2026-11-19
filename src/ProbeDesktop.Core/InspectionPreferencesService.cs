namespace ProbeDesktop.Core;

public sealed partial class DesktopService
{
    private static InspectionSelection SelectionFor(StoreState state, Guid profileId) =>
        state.Vault.InspectionSelections.Find(s => s.ProfileId == profileId) ?? new(profileId, false, []);

    private static void RequireInspectionInterval(int intervalMinutes)
    {
        if (intervalMinutes is < 10 or > 480)
            throw new ClientOperationException("InvalidInterval", "巡检间隔必须为 10 至 480 分钟，默认 30 分钟。");
    }

    private static InspectionTarget[] SelectedTargets(StoreState state) => state.Profiles.Select(p =>
    {
        InspectionSelection selection = SelectionFor(state, p.Id);
        return TargetFor(p) with
        {
            Models = selection.IsSelected
            ? p.Models.Select(m => m.Id).Where(m => selection.SelectedModelIds.Contains(m, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : []
        };
    }).Where(t => t.Models.Count > 0).ToArray();

    public async Task<InspectionSnapshot> ConfirmInspectionSettingsAsync(IReadOnlyList<InspectionSelection> selections, InspectionConsent consent, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(selections);
        RequireInspectionInterval(consent.IntervalMinutes);
        if (consent.MaxAttempts is < 1 or > InspectionRoundResults.MaxAttempts)
            throw new ClientOperationException("InvalidRetries", "最大重试次数必须为 0、1 或 2。");
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (selections.Any(s => s is null || s.SelectedModelIds is null) || selections.Select(s => s.ProfileId).Distinct().Count() != selections.Count)
                throw new ClientOperationException("InvalidSelection", "巡检选择包含重复配置或无效模型集合。");
            List<InspectionSelection> saved = [];
            foreach (InspectionSelection selection in selections)
            {
                RelayProfile profile = FindProfile(state, selection.ProfileId);
                if (selection.SelectedModelIds.Any(m => m is null || !profile.Models.Any(c => c.Id == m)))
                    throw new ClientOperationException("InvalidSelection", "只能选择当前成功缓存中的模型。");
                saved.Add(selection with { SelectedModelIds = selection.SelectedModelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
            }
            state.Vault.InspectionSelections = saved;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            InspectionTarget[] current = SelectedTargets(state);
            if (consent.SettingsRevision != state.Vault.InspectionSettingsRevision || consent.Scope is null ||
                consent.AcceptedAt < now.AddMinutes(-10) || consent.AcceptedAt > now.AddSeconds(30) ||
                consent.Scope.Count != current.Length || consent.Scope.Any(t => t is null || t.Models is null) ||
                consent.Scope.Select(t => t.ProfileId).Distinct().Count() != current.Length ||
                current.Any(t => !consent.Scope.Any(c => SameTarget(c, t))))
                throw new ClientOperationException("ConsentRequired", "巡检设置或目标已变化，或确认已过期。请重新确认巡检设置。");
            foreach (var target in current)
                foreach (var model in target.Models)
                {
                    var validation = ValidateConnection(state, target.ProfileId, model);
                    if (!validation.Passed) throw new ClientOperationException(validation.Code, validation.Message);
                }
            state.Vault.InspectionGrants = current.Select(t => new InspectionGrant(t, consent.AcceptedAt, consent.IntervalMinutes, MaxAttempts: consent.MaxAttempts)).ToList();
            state.Vault.InspectionSettings = state.Vault.InspectionSettings with { IntervalMinutes = consent.IntervalMinutes, MaxRetries = consent.MaxAttempts - 1 };
            state.Vault.InspectionSettingsRevision = Guid.NewGuid();
            state.Vault.InspectionSettingsVersion = 1;
            state.Vault.InspectionUpgradePending = false;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return InspectionView(state);
        }
        finally { _store.Gate.Release(); }
    }

    public Task<InspectionSnapshot> SaveInspectionSelectionsAsync(IReadOnlyList<InspectionSelection> selections, CancellationToken cancellationToken = default) =>
        throw new ClientOperationException("ConsentRequired", "请统一确认并保存巡检设置。");

    public Task<InspectionSnapshot> SaveInspectionIntervalAsync(int intervalMinutes, InspectionConsent? consent = null, CancellationToken cancellationToken = default) =>
        throw new ClientOperationException("ConsentRequired", "请统一确认并保存巡检设置。");

    public async Task<LatestModelResults> GetLatestResultsAsync(Guid profileId, string modelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            TrimConnectionHistory(state);
            SensitiveText sanitizer = new(state.Vault.KnownSecrets);
            ConnectionTestResult? connection = state.Vault.ConnectionHistory
                .Where(r => r.ProfileId == profileId && r.ModelId == modelId && r.CompletedAt is not null && r.Stage != ConnectionTestStage.LocalValidationPassed)
                .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).FirstOrDefault();
            if (connection is not null)
                connection = InspectionRoundResults.RepresentativeForLatest(state.Vault.ConnectionHistory, connection);
            ProbeRecord? probe = state.Vault.History.OrderByDescending(r => r.CreatedAt)
                .Where(r => r.ProfileId == profileId && r.ModelId == modelId && IsTerminal(r.State) && r.CompletedAt is not null)
                .OrderByDescending(r => r.CompletedAt).FirstOrDefault();
            return new(connection is null ? null : SanitizeConnection(connection, sanitizer), probe is null ? null : sanitizer.Record(probe));
        }
        finally { _store.Gate.Release(); }
    }
}
