using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>
/// 由 Core 快照计算“当前所选范围”的纯逻辑，规则与 Core 的 SelectedTargets 保持一致：
/// 只有 IsSelected=true 的配置才计入，且只取“所选模型 ∩ 当前缓存”；空结果的配置不出现在范围内。
/// 授权同意（InspectionConsent）的 Scope 必须逐项等于这里的结果，否则 Core 会以 ConsentRequired 拒绝。
/// 空选择得到空范围：调用方据此不发请求、不申请授权。
/// </summary>
public static class InspectionScope
{
    public static IReadOnlyList<InspectionTarget> Selected(InspectionSnapshot snapshot)
    {
        var result = new List<InspectionTarget>();
        foreach (var status in snapshot.Targets)
        {
            var selection = snapshot.Selections.FirstOrDefault(s => s.ProfileId == status.Target.ProfileId);
            if (selection is not { IsSelected: true })
            {
                continue;
            }

            var chosen = new HashSet<string>(selection.SelectedModelIds, StringComparer.Ordinal);
            var models = status.Target.Models
                .Where(chosen.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (models.Length > 0)
            {
                result.Add(status.Target with { Models = models });
            }
        }

        return result;
    }

    public static int SelectedModelCount(InspectionSnapshot snapshot) => Selected(snapshot).Sum(t => t.Models.Count);

    /// <summary>所选范围内已授权（且未暂停）的模型数。</summary>
    public static int AuthorizedSelectedCount(InspectionSnapshot snapshot)
    {
        var count = 0;
        foreach (var target in Selected(snapshot))
        {
            var status = snapshot.Targets.First(s => s.Target.ProfileId == target.ProfileId);
            var authorized = new HashSet<string>(status.AuthorizedModels, StringComparer.Ordinal);
            count += target.Models.Count(authorized.Contains);
        }

        return count;
    }

    /// <summary>所选范围内尚未授权的模型数（新增、间隔变化后失效、鉴权暂停等）。</summary>
    public static int PendingSelectedCount(InspectionSnapshot snapshot)
        => SelectedModelCount(snapshot) - AuthorizedSelectedCount(snapshot);
}
