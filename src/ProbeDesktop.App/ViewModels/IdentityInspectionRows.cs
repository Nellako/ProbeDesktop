using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>身份识别巡检页的文本与状态映射（纯函数，不联网）。</summary>
public static class IdentityInspectionText
{
    /// <summary>每个目标等待结果的本地上限。这只是本地等待上限，不是远端任务或费用上限。</summary>
    public static readonly TimeSpan LocalWaitLimit = TimeSpan.FromMinutes(10);

    public const string OfficialProbePostUrl = "https://bazaarlink.ai/api/probe/run";

    public static string Kind(IdentityInspectionTrigger trigger) => trigger == IdentityInspectionTrigger.Manual ? "手动" : "定时";

    /// <summary>Core 报告的轮次停止代码或目标跳过代码的可读说明。只使用固定文案，未知代码只显示代码本身。</summary>
    public static string CodeText(string? code) => code switch
    {
        null or "" => "",
        "LocalPollingLimit" => "单个目标等待已达本地 10 分钟上限，结果未知；这只是本地等待上限，远端任务不视为已取消",
        "UnknownResult" => "结果未知，没有确认到远端结束状态",
        "Http429" => "官方服务限流（HTTP 429）",
        "ConnectionRateLimited" => "官方主机仍在限流冷却中",
        "ActiveRun" => "已有未结束或结果未知的鉴别任务，为避免重复提交而停止",
        "SubmissionBusy" => "已有另一次提交正在进行",
        "IdentityInspectionDisabled" => "定时身份识别巡检已关闭",
        "IdentityProfilePaused" => "该配置因认证被拒绝（401/403）已暂停，本轮不再提交",
        "IdentityNotAuthorized" => "目标已变化、未勾选、未授权或已暂停，未发送请求",
        "ProbeExpired" => "服务已无法查询该任务（可能已过期），结果未知；这不代表任务从未提交",
        "ApplicationInterrupted" => "上次运行在这一轮中途结束，未处理的目标已跳过、未确认的提交按结果未知处理，不会补交",
        "Cancelled" => "已取消",
        "RoundEnded" => "本轮结束时尚未处理或未确认",
        "AwaitingResult" => "仍在等待上一个目标的结果",
        _ => "原因代码 " + ProblemObservations.SafeCode(code, "unknown"),
    };

    /// <summary>整轮提前结束的说明。</summary>
    public static string StopCodeText(string? code)
        => string.IsNullOrEmpty(code) ? "" : $"本轮提前结束：{CodeText(code)}。本轮剩余目标不再提交，不补交；已发送的请求无法撤回，远端任务不视为已取消。";

    /// <summary>
    /// 一个目标当前的展示状态。Probe 记录存在时以记录为准（与通知分类同一套规则）；
    /// 只有明确一致且无警示才显示为一致。tone：none / ok / warn / bad。
    /// </summary>
    public static (string State, string Tone, string Detail) Describe(
        IdentityInspectionAttempt attempt, ProbeRecordViewModel? vm, bool awaiting, bool roundBusy)
    {
        var reason = string.IsNullOrEmpty(attempt.Code) ? "" : $"（{CodeText(attempt.Code)}）";
        if (attempt.State == IdentityInspectionAttemptState.Skipped)
            return ("已跳过", "none", "未发送请求。" + reason);
        if (vm is not null)
        {
            var (final, state, problem) = ProblemObservations.ClassifyProbe(vm.Record, awaiting);
            if (!final)
            {
                return (vm.State == LocalRunState.Running ? "远端检测中" : vm.StateText, "none",
                    "等待本地查询读取进度；查询只读取，不会重新提交。" + reason);
            }

            if (state == NotificationObservationState.Healthy)
            {
                var healthy = vm.Record.VerdictStatus == "clean_match" ? "身份一致" : "家族层相符";
                return (healthy, "ok", $"检测任务：{vm.StateText}；身份结论：{vm.IdentitySummaryText}。");
            }

            var tone = problem is "mismatch" or "spoof-suspected" or "failed" or "aborted" ? "bad" : "warn";
            var warnNote = vm.Record.Warnings.Count > 0 ? $"另有 {vm.Record.Warnings.Count} 项行为警示。" : "";
            return (IdentityStateLabel(vm.Record, problem), tone,
                $"检测任务：{vm.StateText}；身份结论：{vm.IdentitySummaryText}。{warnNote}" + reason);
        }

        return attempt.State switch
        {
            IdentityInspectionAttemptState.Pending => ("待处理", "none", "尚未轮到该目标。"),
            IdentityInspectionAttemptState.IntentPersisted => roundBusy
                ? ("提交中", "none", "已记录提交意图，正在向 BazaarLink 提交。")
                : ("提交结果未知", "warn", "已记录提交意图但没有确认提交成功；不会重新提交，远端可能已开始检测并产生费用。" + reason),
            IdentityInspectionAttemptState.Submitted => ("已提交", "none", "已提交，等待本地记录与查询。" + reason),
            IdentityInspectionAttemptState.Completed => ("已完成", "ok", "已完成，详情见鉴别记录。" + reason),
            IdentityInspectionAttemptState.Unknown => ("结果未知", "warn", "没有确认结果；不会重新提交。" + reason),
            _ => (attempt.State.ToString(), "none", reason),
        };
    }

    /// <summary>
    /// 终态但不是“身份一致”时的短标签。把“项目未通过/任务失败”“身份不符”“证据不足或冲突”“行为警示”分开；
    /// 疑似诱导与疑似伪造自述分开，不把 family_only 说成不符。未覆盖的情况回退到通用说明。
    /// </summary>
    private static string IdentityStateLabel(ProbeRecord r, string? problem)
    {
        if (r.State == LocalRunState.Completed)
        {
            switch (r.VerdictStatus)
            {
                case "plain_mismatch": return "家族不符";
                case "clean_match_submodel_mismatch": return "型号不符";
                case "spoof_behavior_induced": return "疑似诱导";
                case "spoof_selfclaim_forged": return "疑似伪造自述";
                case "clean_match_family_only": return "型号未确定";
                case "ambiguous": return "证据冲突";
                case "insufficient_data": return "数据不足";
                case "clean_match" when r.Warnings.Count > 0: return "身份一致，有行为警示";
            }

            if (string.IsNullOrEmpty(r.VerdictStatus))
            {
                return r.IdentityStatus switch
                {
                    "mismatch" => "家族层不符",
                    "uncertain" => "证据不确定",
                    "insufficient_data" => "数据不足",
                    "match" when r.Warnings.Count > 0 => "家族层相符，有行为警示",
                    _ => ProblemObservations.Describe(NotificationCategory.Identity, problem),
                };
            }
        }

        return r.State switch
        {
            LocalRunState.Failed => "检测任务失败",
            LocalRunState.Aborted => "检测任务已中止",
            _ => ProblemObservations.Describe(NotificationCategory.Identity, problem),
        };
    }

    public static string WaitStopText(ProbeRunCoordinator.WaitOutcome outcome) => outcome switch
    {
        ProbeRunCoordinator.WaitOutcome.LocalLimit => "单个目标等待已达本地 10 分钟上限，本轮剩余目标不再提交。远端任务不视为已取消，可在“鉴别记录”中恢复查询；此上限不是远端任务或费用上限。",
        ProbeRunCoordinator.WaitOutcome.Stopped => "该目标的自动查询已暂停或停止，结果未知，本轮剩余目标不再提交。远端任务不视为已取消，可在“鉴别记录”中恢复查询。",
        ProbeRunCoordinator.WaitOutcome.RateLimited => "查询被限流（HTTP 429），已暂停该目标的自动查询并遵守官方冷却，本轮剩余目标不再提交，不补交。远端任务不视为已取消，也不会自动恢复，冷却结束后可在“鉴别记录”中手动恢复查询。",
        ProbeRunCoordinator.WaitOutcome.Unpollable => "该目标没有可查询的远端任务标识，结果未知，本轮剩余目标不再提交，也不会重新提交该目标。",
        _ => "",
    };
}

/// <summary>最新一轮里的一个目标。就地更新，保持列表的键盘焦点。</summary>
public sealed class IdentityAttemptRow : ObservableObject
{
    private readonly Action<Guid> _openReport;
    private string _stateText = "";
    private string _tone = "none";
    private string _detail = "";
    private Guid? _probeLocalId;

    public IdentityAttemptRow(Guid attemptId, string profileName, string modelId, string protocolText, Action<Guid> openReport)
    {
        AttemptId = attemptId;
        ProfileName = profileName;
        ModelId = modelId;
        ProtocolText = protocolText;
        _openReport = openReport;
        OpenReportCommand = new RelayCommand(() => { if (_probeLocalId is { } id) _openReport(id); }, () => _probeLocalId is not null);
    }

    public Guid AttemptId { get; }
    public string ProfileName { get; }
    public string ModelId { get; }
    public string ProtocolText { get; }
    public ICommand OpenReportCommand { get; }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    /// <summary>none / ok / warn / bad。</summary>
    public string Tone
    {
        get => _tone;
        private set => SetProperty(ref _tone, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool HasReport => _probeLocalId is not null;

    public void Update(string state, string tone, string detail, Guid? probeLocalId)
    {
        StateText = state;
        Tone = tone;
        Detail = detail;
        if (_probeLocalId != probeLocalId)
        {
            _probeLocalId = probeLocalId;
            OnPropertyChanged(nameof(HasReport));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
