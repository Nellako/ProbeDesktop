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

    private static void ApplyInspectionConsent(StoreState state, int intervalMinutes, InspectionConsent? consent)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        InspectionTarget[] current = SelectedTargets(state);
        if (consent is null || consent.MaxAttempts != InspectionRoundResults.MaxAttempts || consent.Scope is null || consent.IntervalMinutes != intervalMinutes ||
            consent.AcceptedAt < now.AddMinutes(-10) || consent.AcceptedAt > now.AddSeconds(30) || current.Length == 0 ||
            consent.Scope.Count != current.Length || consent.Scope.Any(t => t is null || t.Models is null) ||
            consent.Scope.Select(t => t.ProfileId).Distinct().Count() != current.Length ||
            current.Any(t => !consent.Scope.Any(c => SameTarget(c, t))))
            throw new ClientOperationException("ConsentRequired", "请重新确认当前选中配置、密钥版本、协议、模型集合、间隔，以及每目标最多三次调用和额外费用。");
        // An invalid target is not granted, but cannot prevent other valid selected targets from running.
        state.Vault.InspectionGrants = current.Select(t => t with
        {
            Models = t.Models.Where(m => ValidateConnection(state, t.ProfileId, m).Passed).ToArray()
        }).Where(t => t.Models.Count > 0).Select(t => new InspectionGrant(t, consent.AcceptedAt, intervalMinutes, MaxAttempts: consent.MaxAttempts)).ToList();
    }

    public async Task<InspectionSnapshot> SaveInspectionSelectionsAsync(IReadOnlyList<InspectionSelection> selections, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(selections);
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
            // Selection never grants permission, and changing it never cancels an already dispatched request.
            state.Vault.InspectionSelections = saved;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return InspectionView(state);
        }
        finally { _store.Gate.Release(); }
    }

    public async Task<InspectionSnapshot> SaveInspectionIntervalAsync(int intervalMinutes, InspectionConsent? consent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireInspectionInterval(intervalMinutes);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreState state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (consent is not null) ApplyInspectionConsent(state, intervalMinutes, consent);
            state.Vault.InspectionSettings = state.Vault.InspectionSettings with { IntervalMinutes = intervalMinutes };
            // Deliberately retain nonce and the active round's cancellation source.
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return InspectionView(state);
        }
        finally { _store.Gate.Release(); }
    }

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
