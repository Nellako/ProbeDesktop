using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>
/// 模型页“最近结果”的展示规则（纯逻辑，只依赖 Core 类型）。
/// “最近已完成”的筛选由 Core 的 ILatestResultsService 完成（按配置 ID + 模型 ID 精确匹配，
/// 直连要求有完成时间，并取最新轮次内的最高状态；鉴别要求终态，失败也算已完成）；这里只负责展示用的时间、状态与“配置已变更”判断，
/// 因此展示时间一律取完成时间，与模型列表的拉取/刷新时间无关。
/// </summary>
public static class LatestResultPicker
{
    /// <summary>展示用时间：完成时间优先；缺失时退回开始时间（仅防御，Core 返回的直连记录都有完成时间）。</summary>
    public static DateTimeOffset ConnectionTime(ConnectionTestResult r) => r.CompletedAt ?? r.StartedAt;

    public static DateTimeOffset ProbeTime(ProbeRecord r) => r.CompletedAt ?? r.LastPolledAt ?? r.CreatedAt;

    /// <summary>该结果记录时的 Key 版本 / Base URL / 协议与当前配置不一致时为 true（仍显示，但标注“配置已变更”）。</summary>
    public static bool ConfigDiffers(int recordKeyRevision, string recordBaseUrl, ApiProtocol recordProtocol,
        int currentKeyRevision, string currentBaseUrl, ApiProtocol currentProtocol)
        => recordKeyRevision != currentKeyRevision
           || recordProtocol != currentProtocol
           || !string.Equals(recordBaseUrl.TrimEnd('/'), currentBaseUrl.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>
    /// 直连记录的展示分类：成功 / 成功（可能）/ 失败（含底层 Unknown）。
    /// Incomplete 表示记录停在中间阶段，不计入三类终态统计。历史底层状态不改写。
    /// </summary>
    public enum ConnectionOutcome { Success, PossibleSuccess, Failed, Incomplete }

    /// <summary>
    /// 按 Stage 分类，不单看 ModelResponded：PossibleSuccess 的 ModelResponded 恒为 false，
    /// 必须先于 ModelResponded 判断，避免被误并入“成功”或“失败”。
    /// </summary>
    public static ConnectionOutcome Classify(ConnectionTestResult r) => InspectionRoundResults.OutcomeRank(r) switch
    {
        3 => ConnectionOutcome.Success,
        2 => ConnectionOutcome.PossibleSuccess,
        1 => ConnectionOutcome.Failed,
        _ => ConnectionOutcome.Incomplete,
    };

    /// <summary>三类终态的统一文案；Incomplete 没有统一文案，由调用方按 Stage 给出。</summary>
    public static string OutcomeText(ConnectionOutcome outcome) => outcome switch
    {
        ConnectionOutcome.Success => "成功",
        ConnectionOutcome.PossibleSuccess => "成功（可能）",
        ConnectionOutcome.Failed => "失败",
        _ => "未完成",
    };

    /// <summary>色调：ok / possible / bad / warn / none。“可能成功”使用独立的 possible，不与 ok 或 warn 共用。</summary>
    public static string OutcomeTone(ConnectionOutcome outcome) => outcome switch
    {
        ConnectionOutcome.Success => "ok",
        ConnectionOutcome.PossibleSuccess => "possible",
        ConnectionOutcome.Failed => "bad",
        _ => "none",
    };

    /// <summary>直连结果的状态文字与色调（ok / possible / warn / bad / none）。</summary>
    public static (string State, string Tone) DescribeConnection(ConnectionTestResult r)
    {
        var outcome = Classify(r);
        var text = outcome switch
        {
            ConnectionOutcome.Failed => "失败" + (string.IsNullOrWhiteSpace(r.Code) ? "" : "（" + r.Code + "）"),
            ConnectionOutcome.Incomplete => r.Stage switch
            {
                ConnectionTestStage.LocalValidationPassed => "仅本地检查通过（未发送）",
                ConnectionTestStage.ApiReached => "API 可达（未确认模型）",
                _ => "未完成",
            },
            _ => OutcomeText(outcome),
        };
        return (text, OutcomeTone(outcome));
    }
}
