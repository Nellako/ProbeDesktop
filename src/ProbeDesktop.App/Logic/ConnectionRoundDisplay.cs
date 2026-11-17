using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>某个精确目标本轮的最好已完成记录（同级取最新），以及所有尝试的总数。</summary>
public sealed record RoundTargetResult(ConnectionTestResult Record, int Attempts)
{
    public bool IsAttemptInProgress { get; init; }
}

/// <summary>
/// 连接巡检“最新一轮”的展示规则（纯逻辑，只依赖 Core 类型）。
/// 连接巡检对每个目标最多尝试 3 次，每次都会作为真实记录保留在完整连接历史里；
/// 本页只按精确目标（配置、凭据修订、Base URL、协议、模型 ID）显示本轮已完成尝试中的最高状态；
/// 后续尝试进行中时保留已有最好结果并注明进度；尚无已完成尝试时显示进行中的记录。
/// </summary>
public static class ConnectionRoundDisplay
{
    /// <summary>每个目标每轮最多尝试次数，与 Core 的授权上限一致。</summary>
    public const int MaxAttempts = InspectionRoundResults.MaxAttempts;

    /// <summary>取指定轮次中每个精确目标的最好已完成记录，按代表记录开始时间倒序；不改写记录的标记校验。</summary>
    public static IReadOnlyList<RoundTargetResult> LatestPerTarget(IEnumerable<ConnectionTestResult> history, Guid roundId)
        => history
            .Where(r => r.RoundId == roundId)
            .GroupBy(r => (r.ProfileId, r.KeyRevision, r.BaseUrl, r.Protocol, r.ModelId))
            .Select(g => new RoundTargetResult(
                InspectionRoundResults.BestCompleted(g) ?? g.OrderByDescending(r => r.StartedAt).First(),
                g.Count()) { IsAttemptInProgress = g.Any(r => r.CompletedAt is null) })
            .OrderByDescending(x => x.Record.StartedAt)
            .ToList();

    /// <summary>多次尝试时附在说明前面的提示；只有一次尝试时为空。</summary>
    public static string AttemptNote(RoundTargetResult item)
        => item.Attempts <= 1
            ? ""
            : item.IsAttemptInProgress || item.Record.CompletedAt is null
                ? $"第 {item.Attempts} 次尝试进行中，显示本轮已完成尝试中的最高状态（同级取最新，各次尝试见连接记录）。"
                : $"共尝试 {item.Attempts} 次，显示本轮最高状态（同级取最新，各次尝试见连接记录）。";
}
