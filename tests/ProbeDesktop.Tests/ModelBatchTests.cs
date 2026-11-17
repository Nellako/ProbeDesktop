using System.Net;
using ProbeDesktop.App.Logic;
using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public static class ModelBatchTests
{
    public static async Task B01_FailureIsolationAndEmptySuccess()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler();
        using var service = new DesktopService(tmp.Path, Fixtures.Client(handler), new ReversibleTestProtector());
        var profiles = new List<RelayProfile>();
        for (var i = 0; i < 3; i++)
        {
            var profile = await service.SaveProfileAsync(new RelayProfile { Name = "Batch " + i, BaseUrl = $"https://relay-{i}.example.com/v1" }, Program.Key1);
            handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("old"));
            profiles.Add(await service.RefreshModelsAsync(profile.Id, Consent(profile)));
        }
        await service.SaveInspectionSelectionsAsync(profiles.Select(p => new InspectionSelection(p.Id, true, ["old"])).ToArray());
        var baselineRequests = handler.Count;
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson());
        handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ModelsJson("old", "new"));
        var done = new List<Guid>();
        var results = await SerialBatchRunner.RunAsync(profiles, async (profile, ct) =>
        {
            var updated = await service.RefreshModelsAsync(profile.Id, Consent(profile), ct);
            return new BatchStepResult(BatchItemOutcome.Succeeded, updated.Models.Count.ToString());
        }, null, result => done.Add(result.Key.Id), CancellationToken.None);
        Check.Equal(BatchItemOutcome.Failed, results[0].Outcome, "first failure reported");
        Check.Equal(BatchItemOutcome.Succeeded, results[1].Outcome, "empty response is successful");
        Check.Equal(BatchItemOutcome.Succeeded, results[2].Outcome, "later profile still refreshed");
        Check.Equal(3, handler.Count - baselineRequests, "exactly one request per profile without retry");
        Check.Equal(3, done.Count, "all per-profile statuses published");
        var saved = await service.GetProfilesAsync();
        Check.Equal("old", saved.Single(p => p.Id == profiles[0].Id).Models.Single().Id, "failure keeps cache");
        Check.Equal(0, saved.Single(p => p.Id == profiles[1].Id).Models.Count, "successful empty replaces cache");
        Check.Equal(2, saved.Single(p => p.Id == profiles[2].Id).Models.Count, "later success saves its own cache");
        var selections = (await service.GetInspectionSnapshotAsync()).Selections;
        Check.Equal("old", selections.Single(s => s.ProfileId == profiles[0].Id).SelectedModelIds.Single(), "failure keeps selected model");
        Check.Equal(0, selections.Single(s => s.ProfileId == profiles[1].Id).SelectedModelIds.Count, "successful empty clears selected models");
        Check.Equal("old", selections.Single(s => s.ProfileId == profiles[2].Id).SelectedModelIds.Single(), "new model defaults unselected");
    }

    public static Task B02_CacheTimeWordingIsNeutral()
    {
        const string time = "2026-10-30 18:42:10";
        string[] misleading = ["拉取于", "已获取", "获取于", "从未拉取"];

        var summary = ModelCacheText.Summary(true, 13, time);
        var tooltip = ModelCacheText.SummaryTooltip(true, 13, time);
        var count = ModelCacheText.CountLine(13, 13, time);
        var refreshed = ModelCacheText.RefreshedLine(13, time);
        var emptyRefreshed = ModelCacheText.RefreshedLine(0, time);

        Check.Equal("13 个模型（缓存 " + time + "）", summary, "grid summary uses cache wording");
        Check.Equal("未拉取", ModelCacheText.Summary(false, 0, "无缓存时间"), "no cache stays distinct");
        Check.Equal("尚无模型缓存。", ModelCacheText.SummaryTooltip(false, 0, "无缓存时间"), "no cache tooltip");
        Check.Contains(tooltip, "缓存时间（可能为导入）", "tooltip says time may be imported");
        Check.Contains(count, "缓存时间（可能为导入）", "model page says time may be imported");
        Check.Contains(tooltip, "不代表联网获取", "tooltip denies network-fetch claim");
        Check.Contains(count, "不代表联网获取", "model page denies network-fetch claim");
        Check.Contains(count, "不代表列表完整", "model page denies completeness claim");
        Check.Contains(refreshed, "缓存已更新", "network refresh uses neutral cache-updated wording");
        Check.Contains(emptyRefreshed, "缓存已更新为空", "empty refresh keeps cache-emptied warning");
        foreach (var text in new[] { summary, tooltip, count, refreshed, emptyRefreshed })
        {
            foreach (var phrase in misleading)
            {
                Check.False(text.Contains(phrase, StringComparison.Ordinal), $"'{phrase}' must not appear in: {text}");
            }
        }

        return Task.CompletedTask;
    }

    private static OutboundConsent Consent(RelayProfile profile) => new(profile.Id, profile.KeyRevision, profile.BaseUrl,
        OutboundPurpose.ModelList, ModelApiEndpoints.Models(profile.BaseUrl, profile.Protocol), null, DateTimeOffset.UtcNow, profile.Protocol);
}
