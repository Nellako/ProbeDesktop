using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>某个精确目标在一轮中的展示结果：最后一次尝试的记录，以及本轮对该目标的尝试总数。</summary>
public sealed record RoundTargetResult(ConnectionTestResult Record, int Attempts);

/// <summary>
/// 连接巡检“最新一轮”的展示规则（纯逻辑，只依赖 Core 类型）。
/// 连接巡检对每个目标最多尝试 3 次，每次都会作为真实记录保留在完整连接历史里；
/// 本页只按精确目标（配置、凭据修订、Base URL、协议、模型 ID）显示最后一次尝试：
/// 最后一次已完成时显示其最终结果；最后一次仍在进行时显示“进行中”，不拿更早的失败顶替。
/// </summary>
public static class ConnectionRoundDisplay
{
    /// <summary>每个目标每轮最多尝试次数，与 Core 的授权上限一致。</summary>
    public const int MaxAttempts = InspectionRoundResults.MaxAttempts;

    /// <summary>取指定轮次中每个精确目标的最后一次尝试，按其开始时间倒序。</summary>
    public static IReadOnlyList<RoundTargetResult> LatestPerTarget(IEnumerable<ConnectionTestResult> history, Guid roundId)
        => history
            .Where(r => r.RoundId == roundId)
            .GroupBy(r => (r.ProfileId, r.KeyRevision, r.BaseUrl, r.Protocol, r.ModelId))
            .Select(g => new RoundTargetResult(
                g.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.CompletedAt ?? DateTimeOffset.MinValue).First(),
                g.Count()))
            .OrderByDescending(x => x.Record.StartedAt)
            .ToList();

    /// <summary>多次尝试时附在说明前面的提示；只有一次尝试时为空。</summary>
    public static string AttemptNote(RoundTargetResult item)
        => item.Attempts <= 1
            ? ""
            : item.Record.CompletedAt is null
                ? $"第 {item.Attempts} 次尝试进行中（前面的尝试保留在连接记录）。"
                : $"共尝试 {item.Attempts} 次，显示最后一次（各次尝试见连接记录）。";
}
