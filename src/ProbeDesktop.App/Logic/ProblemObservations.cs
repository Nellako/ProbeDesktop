using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>一条待上报给 Core 的自动观察。Label 是“配置名 / 模型 ID”，Hint 是附加在通知末尾的补充说明（可为空）。</summary>
public sealed record ObservedItem(NotificationObservation Observation, string Label, string? Hint = null);

/// <summary>
/// 把自动结果转换成通知观察（纯逻辑，只依赖 Core 类型）。规则：
/// - 只产生自动观察（Automatic=true）；调用方只在自动结果后使用，手动运行不通知；
/// - 问题类型只用稳定的非敏感代码（字母、数字、_、-），绝不使用响应文本；
/// - 只有真正成功才是 Healthy（可清除旧异常）。连接“成功（可能）”和“结果未知”是稳定问题代码，会通知，但不算恢复；
/// - 完成的鉴别只要报告带警示项（Warnings 非空）至少是问题 identity-warning；不一致/疑似伪装优先于警示；
/// - 连接只观察自动轮次产生的记录（Source == Automatic），手动或“立即巡检”记录不产生观察；
///   同一轮内同一精确目标的多次尝试只取最后一次已完成结果，中间失败不通知；
/// - 仍在等待的结果、采集基线中、其他无法判定、跳过是 Unknown：既不通知也不清除已记录的问题；
/// - 目标取自记录或快照自带的精确字段（配置、凭据修订、地址、协议、模型），Core 再与当前配置核对，不一致的旧目标不会挂到新配置上；
/// - 身份类只观察身份识别巡检的定时记录（Source == IdentityInspection 且触发类型为 Automatic），手动单次、手动一轮和旧版“能力自动”历史不产生观察。
/// </summary>
public static class ProblemObservations
{
    public const int MaxCodeLength = 32;
    public const string PossibleSuccess = "possible-success";
    public const string UnknownResult = "unknown-result";
    public const string IdentityWarning = "identity-warning";

    /// <summary>只保留 ASCII 字母、数字、_、-，并限制长度；全部被过滤时返回 fallback。</summary>
    public static string SafeCode(string? raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        var chars = raw.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(MaxCodeLength).ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }

    public static (NotificationObservationState State, string? Problem) ClassifyConnection(ConnectionTestResult r)
    {
        switch (LatestResultPicker.Classify(r))
        {
            case LatestResultPicker.ConnectionOutcome.Success:
                return (NotificationObservationState.Healthy, null);
            case LatestResultPicker.ConnectionOutcome.Failed:
                var problem = r.HttpStatusCode is { } http
                    ? "http-" + http.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "failed-" + SafeCode(r.Code, "error");
                return (NotificationObservationState.Problem, problem);
            case LatestResultPicker.ConnectionOutcome.PossibleSuccess:
                return (NotificationObservationState.Problem, PossibleSuccess);
            case LatestResultPicker.ConnectionOutcome.Unknown:
                return (NotificationObservationState.Problem, UnknownResult);
            default:
                return (NotificationObservationState.Unknown, null);
        }
    }

    /// <summary>
    /// 自动发起的 Probe 记录的最终状态。Final=false 表示仍在进行或在等待自动查询，不通知也不清除旧异常。
    /// waiting：记录可查询且自动查询已经启动、正在运行（或尚未被用户停止）。仅真正的 match 才是 Healthy。
    /// </summary>
    public static (bool Final, NotificationObservationState State, string? Problem) ClassifyProbe(ProbeRecord r, bool waiting)
    {
        switch (r.State)
        {
            case LocalRunState.Completed:
                return Verdict(r) is { } problem
                    ? (true, NotificationObservationState.Problem, problem)
                    : (true, NotificationObservationState.Healthy, null);
            case LocalRunState.Failed:
                return (true, NotificationObservationState.Problem, "failed");
            case LocalRunState.Aborted:
                return (true, NotificationObservationState.Problem, "aborted");
            case LocalRunState.Expired:
                return (true, NotificationObservationState.Problem, UnknownResult);
            case LocalRunState.Submitting:
                return (false, NotificationObservationState.Unknown, null);
            default:
                // Running / PollingInterrupted / SubmissionUnknown / IncompatibleResponse：
                // 没有远端任务标识就永远无法查询；有标识但自动查询已停止（暂停、用户停止、失败）则结果未知。
                var pollable = !string.IsNullOrEmpty(r.RemoteRunId);
                return pollable && waiting
                    ? (false, NotificationObservationState.Unknown, null)
                    : (true, NotificationObservationState.Problem, UnknownResult);
        }
    }

    /// <summary>
    /// 完成的鉴别：返回问题代码，或 null 表示真正一致（Healthy）。优先级：不一致/疑似伪装 &gt; 报告警示项 &gt; 其他。
    /// 报告带警示项（Warnings 非空）时，即使结论是 clean_match 也至少是 identity-warning，不当作恢复。
    /// 信息不足、仅家族一致、无结论都是“结果未知”，不当作恢复。
    /// </summary>
    private static string? Verdict(ProbeRecord r)
    {
        var verdict = r.VerdictStatus ?? "";
        if (verdict is "plain_mismatch" or "clean_match_submodel_mismatch" || (verdict.Length == 0 && r.IdentityStatus == "mismatch"))
        {
            return "mismatch";
        }

        if (verdict.StartsWith("spoof_", StringComparison.Ordinal))
        {
            return "spoof-suspected";
        }

        if (r.Warnings.Count > 0)
        {
            return IdentityWarning;
        }

        if (verdict == "clean_match")
        {
            return null;
        }

        return verdict.Length == 0 && r.IdentityStatus == "match" ? null : UnknownResult;
    }

    /// <summary>
    /// 连接巡检一轮的观察。只处理自动轮次产生的记录（Source == Automatic），手动或“立即巡检”记录不产生观察。
    /// 每个目标（配置、凭据修订、Base URL、协议、模型 ID 精确一致）在本轮内可能有最多 3 次尝试，
    /// 只取最后一次已完成的结果：先失败后成功不通知（最终为 Healthy，可清除旧异常），耗尽后仍非绿色只按最终分类通知。
    /// 目标取自刚读取的巡检快照；凭据修订、协议或 Base URL 任一不精确一致的记录跳过，避免挂到新目标上。
    /// </summary>
    public static IReadOnlyList<ObservedItem> FromConnectionRound(Guid roundId, IEnumerable<ConnectionTestResult> history, InspectionSnapshot snapshot)
    {
        var items = new List<ObservedItem>();
        var automatic = history.Where(h => h.Source == ConnectionRecordSource.Automatic);
        foreach (var r in InspectionRoundResults.FinalConnections(automatic, roundId))
        {
            var status = snapshot.Targets.FirstOrDefault(t => t.Target.ProfileId == r.ProfileId);
            if (status is null
                || status.Target.KeyRevision != r.KeyRevision
                || status.Target.Protocol != r.Protocol
                || !string.Equals(status.Target.BaseUrl, r.BaseUrl, StringComparison.Ordinal))
            {
                continue;
            }

            var (state, problem) = ClassifyConnection(r);
            var target = new NotificationTarget(status.Target.ProfileId, status.Target.KeyRevision, status.Target.BaseUrl, status.Target.Protocol, r.ModelId);
            items.Add(new ObservedItem(
                new NotificationObservation(NotificationCategory.Connection, target, true, state, problem),
                $"{status.ProfileName} / {r.ModelId}"));
        }

        return items;
    }

    /// <summary>
    /// 身份识别巡检定时记录的最终状态观察（身份类）。只含最终状态的记录；同一记录是否已上报由调用方去重。
    /// 手动单次、手动一轮和旧版“能力自动”历史记录返回 null。目标直接取自记录的精确字段，Core 再与当前配置核对。
    /// </summary>
    public static ObservedItem? FromAutomaticProbe(ProbeRecord r, bool waiting)
    {
        if (!IsAutomaticIdentity(r))
        {
            return null;
        }

        var (final, state, problem) = ClassifyProbe(r, waiting);
        if (!final)
        {
            return null;
        }

        var target = new NotificationTarget(r.ProfileId, r.KeyRevision, r.BaseUrl, r.Protocol, r.ModelId);
        return new ObservedItem(
            new NotificationObservation(NotificationCategory.Identity, target, true, state, problem),
            $"{r.ProfileName} / {r.ModelId}");
    }

    /// <summary>是否为身份识别巡检的定时记录（只有这类记录才会产生身份类自动通知）。</summary>
    public static bool IsAutomaticIdentity(ProbeRecord r)
        => r.Source == ProbeRecordSource.IdentityInspection && r.IdentityInspectionTrigger == IdentityInspectionTrigger.Automatic;

    /// <summary>问题代码的简短中文说明。不包含响应文本；未知代码只显示代码本身。</summary>
    public static string Describe(NotificationCategory category, string? problem)
    {
        if (string.IsNullOrEmpty(problem))
        {
            return "出现问题";
        }

        switch (problem)
        {
            case IdentityWarning:
                return "鉴别报告含警示项";
            case PossibleSuccess:
                return "成功（可能），没有确认到有效的模型响应";
            case UnknownResult:
                return category == NotificationCategory.Identity ? "鉴别结果未知：查询已暂停或被限流，或结果不完整；远端任务不视为已取消" : "结果未知";
            case "mismatch":
                return "鉴别结果与目标模型不一致";
            case "spoof-suspected":
                return "鉴别提示疑似伪装";
            case "failed":
                return "鉴别任务失败";
            case "aborted":
                return "鉴别任务已中止";
        }

        if (problem.StartsWith("http-", StringComparison.Ordinal))
        {
            var code = problem["http-".Length..];
            return code switch
            {
                "401" or "403" => $"鉴权失败（HTTP {code}），该配置已暂停",
                "429" => "被限流（HTTP 429）",
                _ => $"请求失败（HTTP {code}）",
            };
        }

        if (problem.StartsWith("failed-", StringComparison.Ordinal))
        {
            return $"连接失败（{problem["failed-".Length..]}）";
        }

        return $"问题类型 {problem}";
    }

    public static string Title(NotificationCategory category) => category switch
    {
        NotificationCategory.Connection => "连接巡检发现问题",
        _ => "身份识别巡检发现问题",
    };

    /// <summary>
    /// 同一轮的同类问题合并为一条：一个目标显示“标签：说明”，多个目标最多列出前 3 个并给出总数。
    /// 只使用配置名、模型 ID 和固定说明，不含密钥、完整地址或原始回答。
    /// </summary>
    public static string Compose(NotificationCategory category, IReadOnlyList<(string Label, string? Problem, string? Hint)> problems)
    {
        static string One(NotificationCategory c, (string Label, string? Problem, string? Hint) p)
            => $"{p.Label}：{Describe(c, p.Problem)}" + (string.IsNullOrEmpty(p.Hint) ? "" : $"（{p.Hint}）");

        if (problems.Count == 1)
        {
            return One(category, problems[0]);
        }

        var head = string.Join("；", problems.Take(3).Select(p => One(category, p)));
        return problems.Count > 3 ? $"共 {problems.Count} 个目标：{head} 等" : $"共 {problems.Count} 个目标：{head}";
    }
}
