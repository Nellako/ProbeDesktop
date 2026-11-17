using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed record FactRow(string Label, string Value);

public sealed record ItemRow(
    string Group, string Label, string ProbeId, string StatusText, string PassedText,
    string Detail, string DurationText, string TokensText);

public sealed record WarningRow(string Label, string SeverityText, string Severity, string Evidence);

/// <summary>
/// 单条检测记录的界面状态。只接受 LocalId 相同的更新，因此切换配置、页面或历史选择不会把一个任务的结果写到另一个任务上。
/// </summary>
public sealed class ProbeRecordViewModel : ObservableObject
{
    private readonly ProbeRunCoordinator _coordinator;
    private ProbeRecord _record;
    private bool _isPolling;
    private bool _isAutoPolling;
    private string _pollNote = "";

    public ProbeRecordViewModel(ProbeRecord record, ProbeRunCoordinator coordinator)
    {
        _record = record;
        _coordinator = coordinator;
        PollOnceCommand = new AsyncCommand(() => _coordinator.PollOnceAsync(this), () => CanPollNow);
        ResumeCommand = new RelayCommand(() => _coordinator.Resume(this), () => CanResume);
        StopCommand = new RelayCommand(() => _coordinator.StopAutoPolling(this, userInitiated: true), () => IsAutoPolling);
        CopyReportUrlCommand = new RelayCommand(() => CopyText(ReportUrl), () => !string.IsNullOrEmpty(ReportUrl));
        CopyRunIdCommand = new RelayCommand(() => CopyText(RemoteRunId), () => !string.IsNullOrEmpty(RemoteRunId));
        Rebuild(previous: null);
    }

    public ProbeRecord Record => _record;
    public Guid LocalId => _record.LocalId;
    public Guid ProfileId => _record.ProfileId;
    public string ProfileName => _record.ProfileName;
    public string ModelId => _record.ModelId;
    public string ClaimedModelText => Format.Text(_record.ClaimedModel, "未声明");
    public LocalRunState State => _record.State;
    public string StateText => Format.StateText(_record.State);
    public string StateTone => Format.StateTone(_record.State);
    public string StateExplanation => Format.StateExplanation(_record);
    public DateTimeOffset CreatedAt => _record.CreatedAt;
    public string CreatedAtText => Format.Time(_record.CreatedAt);
    public string SourceText => Format.ProbeSourceText(_record);
    public string? RemoteRunId => _record.RemoteRunId;
    public string? ReportUrl => _record.ReportUrl;
    public string? Error => string.IsNullOrWhiteSpace(_record.Error) ? null : _record.Error;
    public string IdentityText => Format.IdentityText(_record.IdentityStatus);
    public string VerdictText => Format.VerdictText(_record.VerdictStatus);
    public string InferredModelText => Format.Text(_record.InferredModel);
    public string Title => $"{_record.ProfileName} · {_record.ModelId}";

    public bool IsTerminal => Format.IsTerminal(_record.State);
    public bool IsActive => Format.IsActive(_record.State);
    public bool HasRemoteId => !string.IsNullOrEmpty(_record.RemoteRunId);
    public bool IsRunning => _record.State is LocalRunState.Running or LocalRunState.Submitting;

    /// <summary>迁移导入的未结束记录：不自动查询（含重启后），手动查询须逐次确认接收方。由 Core 持久保存。</summary>
    public bool ManualResumeRequired => _record.ManualResumeRequired && !IsTerminal;

    /// <summary>可以向服务查询：非终态、不在提交中、且有远端任务标识。</summary>
    public bool IsPollable => !IsTerminal && _record.State != LocalRunState.Submitting && HasRemoteId;
    public bool CanPollNow => IsPollable && !IsPolling;
    public bool CanResume => IsPollable && !IsAutoPolling;

    public bool IsPolling
    {
        get => _isPolling;
        set
        {
            if (SetProperty(ref _isPolling, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsAutoPolling
    {
        get => _isAutoPolling;
        set
        {
            if (SetProperty(ref _isAutoPolling, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>本地轮询的说明（自动查询中、已暂停原因、下一次时间等）。</summary>
    public string PollNote
    {
        get => _pollNote;
        set => SetProperty(ref _pollNote, value);
    }

    public string ProgressText { get; private set; } = "";
    public string PhaseText { get; private set; } = "";
    public bool HasItemTotal { get; private set; }
    public int ItemsDone { get; private set; }
    public int ItemsTotal { get; private set; }
    public string TokensText { get; private set; } = "";
    public bool HasReasoning => Reasoning.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasItems => Items.Count > 0;

    public ObservableCollection<FactRow> Facts { get; } = [];
    public ObservableCollection<FactRow> VerdictFacts { get; } = [];
    public ObservableCollection<string> Reasoning { get; } = [];
    public ObservableCollection<WarningRow> Warnings { get; } = [];
    public ObservableCollection<ItemRow> Items { get; } = [];

    public ICommand PollOnceCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CopyReportUrlCommand { get; }
    public ICommand CopyRunIdCommand { get; }

    /// <summary>应用一次更新。LocalId 不一致时拒绝，返回 false。</summary>
    public bool Apply(ProbeRecord record)
    {
        if (record.LocalId != _record.LocalId)
        {
            return false;
        }

        var previous = _record;
        _record = record;
        Rebuild(previous);
        OnPropertyChanged(string.Empty);
        RaiseCommandStates();
        return true;
    }

    private void Rebuild(ProbeRecord? previous)
    {
        var r = _record;

        // 只有在实际变化时才重建集合，避免每 5 秒刷新打断用户的滚动或选择。
        if (previous is null || !previous.Items.SequenceEqual(r.Items))
        {
            Items.Clear();
            foreach (var item in r.Items)
            {
                Items.Add(new ItemRow(
                    Format.Text(item.Group, ""),
                    Format.Text(item.Label, item.ProbeId),
                    item.ProbeId,
                    Format.ItemStatusText(item.Status),
                    Format.PassedText(item.Passed),
                    item.Detail ?? "",
                    item.DurationMs is { } ms ? $"{ms / 1000.0:0.0} 秒" : "—",
                    item.InputTokens is null && item.OutputTokens is null
                        ? "—"
                        : $"{Format.Number(item.InputTokens)} / {Format.Number(item.OutputTokens)}"));
            }
        }

        if (previous is null || !previous.Reasoning.SequenceEqual(r.Reasoning))
        {
            Reasoning.Clear();
            foreach (var line in r.Reasoning.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                Reasoning.Add(line);
            }
        }

        if (previous is null || !previous.Warnings.SequenceEqual(r.Warnings))
        {
            Warnings.Clear();
            foreach (var w in r.Warnings)
            {
                var severity = (w.Severity ?? "").ToLowerInvariant();
                Warnings.Add(new WarningRow(Format.Text(w.Label, "服务警告"), Format.SeverityText(w.Severity), severity, w.Evidence ?? ""));
            }
        }

        ItemsTotal = r.Items.Count;
        ItemsDone = r.Items.Count(i => i.Status is "done" or "error");
        HasItemTotal = ItemsTotal > 0;
        ProgressText = HasItemTotal
            ? $"检测项 {ItemsDone} / {ItemsTotal} 已结束"
            : IsTerminal ? "无检测项数据" : "尚未返回检测项";
        PhaseText = string.IsNullOrWhiteSpace(r.Phase) ? "" : $"阶段：{r.Phase}";
        TokensText = r.TotalInputTokens is null && r.TotalOutputTokens is null
            ? "—"
            : $"输入 {Format.Number(r.TotalInputTokens)} / 输出 {Format.Number(r.TotalOutputTokens)}";

        Facts.Clear();
        Facts.Add(new FactRow("配置", r.ProfileName));
        Facts.Add(new FactRow("来源", Format.ProbeSourceText(r)));
        if (r.ManualResumeRequired && !Format.IsTerminal(r.State))
        {
            Facts.Add(new FactRow("查询方式", "迁移导入，须手动确认后查询（不自动查询）"));
        }
        Facts.Add(new FactRow("协议（提交时快照）", ProtocolInfo.Display(r.Protocol)));
        Facts.Add(new FactRow("Base URL", r.BaseUrl));
        Facts.Add(new FactRow("调用模型", r.ModelId));
        Facts.Add(new FactRow("声明模型", Format.Text(r.ClaimedModel, "未声明")));
        Facts.Add(new FactRow("远端任务标识", Format.Text(r.RemoteRunId, "无")));
        Facts.Add(new FactRow("远端状态", Format.Text(r.RemoteStatus)));
        Facts.Add(new FactRow("确认发送时间", Format.Time(r.ConsentAt)));
        Facts.Add(new FactRow("本地创建时间", Format.Time(r.CreatedAt)));
        Facts.Add(new FactRow("完成时间", Format.Time(r.CompletedAt)));
        Facts.Add(new FactRow("最近查询", Format.Time(r.LastPolledAt, "未查询")));
        Facts.Add(new FactRow("Token 合计", TokensText));
        Facts.Add(new FactRow("本地记录 ID", r.LocalId.ToString()));

        VerdictFacts.Clear();
        VerdictFacts.Add(new FactRow("身份判断（外层）", Format.IdentityText(r.IdentityStatus)));
        VerdictFacts.Add(new FactRow("详细结论", Format.VerdictText(r.VerdictStatus)));
        VerdictFacts.Add(new FactRow("推测模型家族", Format.Text(r.InferredFamily)));
        VerdictFacts.Add(new FactRow("推测模型", Format.Text(r.InferredModel)));
        VerdictFacts.Add(new FactRow("置信度（服务给出）", Format.Text(r.Confidence)));
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被占用时忽略。
        }
    }

    private static void RaiseCommandStates() => CommandManager.InvalidateRequerySuggested();
}
