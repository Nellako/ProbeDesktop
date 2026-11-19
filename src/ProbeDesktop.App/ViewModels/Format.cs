using System.Globalization;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public static class Format
{
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    public static string Time(DateTimeOffset? value, string empty = "—")
        => value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Zh) : empty;

    public static string Text(string? value, string empty = "—")
        => string.IsNullOrWhiteSpace(value) ? empty : value;

    public static string Number(long? value) => value is { } v ? v.ToString("N0", Zh) : "—";

    public static string StateText(LocalRunState state) => state switch
    {
        LocalRunState.Submitting => "提交中",
        LocalRunState.SubmissionUnknown => "提交结果未知",
        LocalRunState.Running => "远端检测中",
        LocalRunState.PollingInterrupted => "查询中断",
        LocalRunState.Completed => "已完成",
        LocalRunState.Failed => "失败",
        LocalRunState.Aborted => "已中止",
        LocalRunState.Expired => "已过期",
        LocalRunState.IncompatibleResponse => "响应不兼容",
        _ => state.ToString(),
    };

    /// <summary>info / success / warning / danger，用于状态徽标颜色。</summary>
    public static string StateTone(LocalRunState state) => state switch
    {
        LocalRunState.Completed => "success",
        LocalRunState.Failed or LocalRunState.Aborted => "danger",
        LocalRunState.SubmissionUnknown or LocalRunState.PollingInterrupted
            or LocalRunState.Expired or LocalRunState.IncompatibleResponse => "warning",
        _ => "info",
    };

    /// <summary>与 Core 一致：这些状态不会再被服务更新。</summary>
    public static bool IsTerminal(LocalRunState state)
        => state is LocalRunState.Completed or LocalRunState.Failed or LocalRunState.Aborted or LocalRunState.Expired;

    /// <summary>与 Core 一致：存在这些状态的记录时，Core 拒绝发起新的检测。</summary>
    public static bool IsActive(LocalRunState state)
        => state is LocalRunState.Submitting or LocalRunState.SubmissionUnknown or LocalRunState.Running or LocalRunState.PollingInterrupted;

    public static string StateExplanation(ProbeRecord r) => r.State switch
    {
        LocalRunState.Submitting => "正在向 BazaarLink 提交。若程序在此时关闭，提交结果将无法确认。",
        LocalRunState.SubmissionUnknown when string.IsNullOrEmpty(r.RemoteRunId)
            => "提交结果未知，且没有拿到远端任务标识，因此无法查询，本程序也不会自动重新提交。远端可能已经开始检测并产生费用。",
        LocalRunState.SubmissionUnknown => "提交结果未知，但已有任务标识，可以手动恢复查询；不会重新提交。",
        LocalRunState.Running => "远端检测进行中。本地查询只读取进度，不会重复提交。",
        LocalRunState.PollingInterrupted => "本地查询中断（网络、超时或服务错误）。远端检测可能仍在进行，可手动恢复查询。",
        LocalRunState.Completed => "远端已返回完成结果。",
        LocalRunState.Failed when r.RemoteStatus == "failed" => "服务报告任务执行失败。",
        LocalRunState.Failed => "请求未发送即已取消。",
        LocalRunState.Aborted => "远端返回已中止。",
        LocalRunState.Expired => "服务已无法查询该任务（可能已超过约 30 分钟的保留期）。这不代表任务从未提交。",
        LocalRunState.IncompatibleResponse => "服务响应结构不在当前版本支持范围内，结果不能可靠解释。可稍后手动查询。",
        _ => "",
    };

    /// <summary>
    /// 鉴别记录的来源：手动提交、身份识别巡检（手动一轮或定时），以及旧版“能力自动”历史。
    /// 旧版标签只用于已有记录，不会被改写成新的巡检来源。
    /// </summary>
    public static string ProbeSourceText(ProbeRecordSource source) => source switch
    {
        ProbeRecordSource.CapabilityAutomatic => "旧版能力自动",
        ProbeRecordSource.IdentityInspection => "身份巡检",
        _ => "手动",
    };

    /// <summary>带触发方式的来源文本：身份巡检区分手动一轮和定时。</summary>
    public static string ProbeSourceText(ProbeRecord record) => record.Source == ProbeRecordSource.IdentityInspection
        ? record.IdentityInspectionTrigger switch
        {
            IdentityInspectionTrigger.Manual => "身份巡检（手动一轮）",
            IdentityInspectionTrigger.Automatic => "身份巡检（定时）",
            _ => "身份巡检",
        }
        : ProbeSourceText(record.Source);

    /// <summary>外层身份判断（identityAssessment.status）。外层只反映“模型家族”层面，不含具体型号；英文代码只放在技术详情里。</summary>
    public static string IdentityText(string? status) => status switch
    {
        null or "" => "—",
        "match" => "家族层相符",
        "mismatch" => "家族层不符",
        "uncertain" => "证据不确定",
        "insufficient_data" => "数据不足",
        _ => "未识别的取值：" + status,
    };

    /// <summary>详细判定（verdict.status）的简短中文名，不含英文代码。</summary>
    public static string VerdictText(string? status) => status switch
    {
        null or "" => "—",
        "clean_match" => "家族与型号均有据",
        "clean_match_family_only" => "家族相符，型号未能确定",
        "clean_match_submodel_mismatch" => "家族相符，但型号不符",
        "plain_mismatch" => "家族不符",
        "spoof_behavior_induced" => "疑似诱导行为",
        "spoof_selfclaim_forged" => "疑似伪造自述",
        "ambiguous" => "证据冲突，无法判定",
        "insufficient_data" => "数据不足",
        _ => "未识别的取值：" + status,
    };

    /// <summary>
    /// 详细判定的一段中文说明。只说明服务给出的结论含义，不替服务补充“实际型号”：
    /// <paramref name="trueModel"/> 只用于区分“服务给出了实际型号”和“实际型号未知”。
    /// </summary>
    public static string VerdictExplanation(string? status, string? trueModel) => status switch
    {
        "clean_match" => "服务认为本次证据同时支持声明的模型家族和具体型号。这是基于本次样本的概率性判断，不是绝对证明。",
        "clean_match_family_only" => "证据支持声明的模型家族，但不足以确定具体型号。这不等于型号一致，也不等于型号不符。",
        "clean_match_submodel_mismatch" => string.IsNullOrWhiteSpace(trueModel)
            ? "模型家族与声明相符，但声明的具体型号没有通过比对。服务没有确认实际型号（只有某个官方基线吻合时才会给出），实际型号未知。"
            : "模型家族与声明相符，但声明的具体型号没有通过比对。服务给出的实际型号见下方。",
        "plain_mismatch" => "证据指向与声明不同的模型家族，且没有发现伪装信号。这表示行为与声明的家族不一致，不等于确认对方有意伪装。",
        "spoof_behavior_induced" => "行为信号像是被诱导得接近声明的家族，而其他指纹信号指向别处。这是“疑似”判断，不是确证。",
        "spoof_selfclaim_forged" => "行为信号指向别的模型，而端点的表面自述却表现为声明的家族。这是“疑似”判断，不是确证。",
        "ambiguous" => "现有证据互相冲突，没有稳定的多数指向。这是服务给出的正式结论，不是程序出错；它既不是清白证明，也不是有罪判决，可稍后复测。",
        "insufficient_data" => "可用的有效证据太少，服务没有给出身份结论。这与“证据冲突”不同，可稍后复测。",
        _ => "",
    };

    /// <summary>详细判定对应的徽标色调：success / info / warning / danger。</summary>
    public static string VerdictTone(string? status) => status switch
    {
        "clean_match" => "success",
        "clean_match_family_only" or "insufficient_data" => "info",
        "ambiguous" => "warning",
        "clean_match_submodel_mismatch" or "plain_mismatch"
            or "spoof_behavior_induced" or "spoof_selfclaim_forged" => "danger",
        _ => "info",
    };

    /// <summary>服务给出的 confidence 的中文说明。它只是本次样本证据的强弱，不是准确率或概率。</summary>
    public static string EvidenceStrengthText(string? confidence)
    {
        if (string.IsNullOrWhiteSpace(confidence))
        {
            return "服务未给出";
        }

        return confidence.Trim().ToLowerInvariant() switch
        {
            "high" => "高",
            "medium" or "mid" or "moderate" => "中",
            "low" => "低",
            _ => confidence.Trim(),
        };
    }

    public static string ItemStatusText(string? status) => status switch
    {
        "pending" => "等待",
        "running" => "进行中",
        "done" => "已执行",
        "error" => "执行失败",
        null or "" => "—",
        _ => status,
    };

    public static string PassedText(string? passed) => passed switch
    {
        "true" => "通过",
        "false" => "未通过",
        "warning" => "警示",
        null or "" => "—",
        _ => passed,
    };

    public static string SeverityText(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => "严重",
        "high" => "高",
        "medium" => "中",
        "low" => "低",
        "info" => "提示",
        "warning" => "警示",
        null or "" => "警示",
        _ => severity!,
    };
}
