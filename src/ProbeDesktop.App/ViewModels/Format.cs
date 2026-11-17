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
        LocalRunState.Failed => "远端返回失败，或请求未发送即已取消。",
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

    public static string IdentityText(string? status) => status switch
    {
        null or "" => "—",
        "match" => "一致（match）",
        "mismatch" => "不一致（mismatch）",
        "uncertain" => "不确定（uncertain）",
        "insufficient_data" => "数据不足（insufficient_data）",
        _ => status,
    };

    public static string VerdictText(string? status) => status switch
    {
        null or "" => "—",
        "clean_match" => "行为与声明一致（clean_match）",
        "clean_match_family_only" => "仅模型家族一致（clean_match_family_only）",
        "clean_match_submodel_mismatch" => "家族一致、子型号不一致（clean_match_submodel_mismatch）",
        "plain_mismatch" => "不一致（plain_mismatch）",
        "spoof_behavior_induced" => "疑似诱导性伪装行为（spoof_behavior_induced）",
        "spoof_selfclaim_forged" => "疑似伪造自我声明（spoof_selfclaim_forged）",
        "ambiguous" => "无法区分（ambiguous）",
        "insufficient_data" => "数据不足（insufficient_data）",
        _ => status,
    };

    public static string ItemStatusText(string? status) => status switch
    {
        "pending" => "等待",
        "running" => "进行中",
        "done" => "完成",
        "error" => "出错",
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
