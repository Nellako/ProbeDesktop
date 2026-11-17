using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

/// <summary>连接记录窗口的数据。只展示 Core 返回的摘要记录，不含 Key 或完整响应。</summary>
public sealed class ConnectionHistoryViewModel : ObservableObject
{
    private readonly IGlobalInspectionService? _service;
    private readonly IDialogService _dialogs;
    private readonly RecordFeed? _feed;
    private readonly List<ConnectionTestResult> _all = [];
    private ConnectionTestResult? _selected;
    private string _profileFilter = "";
    private string _modelFilter = "";
    private string _statusFilter = "";
    private string _sourceFilter = "";
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _status = "";
    private bool _busy;

    public ConnectionHistoryViewModel(IGlobalInspectionService? service, IDialogService dialogs, RecordFeed? feed = null)
    {
        _feed = feed;
        _service = service;
        _dialogs = dialogs;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ConnectionTestResult r && Matches(r);
        ReloadCommand = new AsyncCommand(ReloadAsync, () => !_busy && _service is not null);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => !_busy && _selected is not null && _service is not null);
        ClearCommand = new AsyncCommand(ClearAsync, () => !_busy && _all.Count > 0 && _service is not null);
        ExportCommand = new AsyncCommand(ExportAsync, () => !_busy && _all.Count > 0 && _service is not null);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            ProfileFilter = ModelFilter = StatusFilter = SourceFilter = "";
            FromDate = null;
            ToDate = null;
        });
    }

    public ObservableCollection<ConnectionTestResult> Items { get; } = [];
    public ICollectionView View { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    public ConnectionTestResult? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(DetailRows));
                OnPropertyChanged(nameof(DetailMessage));
                OnPropertyChanged(nameof(DetailTone));
                OnPropertyChanged(nameof(HasSelection));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string ProfileFilter
    {
        get => _profileFilter;
        set { if (SetProperty(ref _profileFilter, value)) RefreshView(); }
    }

    public string ModelFilter
    {
        get => _modelFilter;
        set { if (SetProperty(ref _modelFilter, value)) RefreshView(); }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetProperty(ref _statusFilter, value)) RefreshView(); }
    }

    public string SourceFilter
    {
        get => _sourceFilter;
        set { if (SetProperty(ref _sourceFilter, value)) RefreshView(); }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set { if (SetProperty(ref _fromDate, value)) RefreshView(); }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set { if (SetProperty(ref _toDate, value)) RefreshView(); }
    }

    private void RefreshView()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(EmptyHint));
    }

    /// <summary>列表为空时的说明：区分“还没有记录”和“筛选后没有结果”。</summary>
    public string EmptyHint => _all.Count == 0
        ? "还没有连接记录。手动测试、立即巡检或定时巡检完成后会出现在这里。"
        : "没有符合当前筛选条件的记录，可点击“清除筛选”。";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                CommandManager.InvalidateRequerySuggested();
                CommandManager.InvalidateRequerySuggested();
                CommandManager.InvalidateRequerySuggested();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CountText => $"共 {_all.Count} 条，当前显示 {View.Cast<object>().Count()} 条";

    /// <summary>
    /// 当前显示范围内的分项统计。成功、成功（可能）、失败（含原结果未知）；
    /// 停在中间阶段的记录单列为“未完成”（为 0 时不显示），使各项之和等于当前显示条数。
    /// </summary>
    public string StatsText
    {
        get
        {
            int success = 0, possible = 0, failed = 0, incomplete = 0;
            foreach (var r in View.Cast<ConnectionTestResult>())
            {
                switch (LatestResultPicker.Classify(r))
                {
                    case LatestResultPicker.ConnectionOutcome.Success: success++; break;
                    case LatestResultPicker.ConnectionOutcome.PossibleSuccess: possible++; break;
                    case LatestResultPicker.ConnectionOutcome.Failed: failed++; break;
                    default: incomplete++; break;
                }
            }

            var text = $"当前显示：成功 {success} · 成功（可能） {possible} · 失败 {failed}";
            return incomplete > 0 ? text + $" · 未完成 {incomplete}" : text;
        }
    }

    /// <summary>详情行：只展示白名单摘要字段，不含 Key 或完整响应。</summary>
    public IReadOnlyList<DetailRow> DetailRows
    {
        get
        {
            if (_selected is not { } r)
            {
                return Array.Empty<DetailRow>();
            }

            var rows = new List<DetailRow>
            {
                new("配置", r.ProfileName),
                new("模型", r.ModelId),
                new("协议", ProtocolText(r.Protocol)),
                new("来源", SourceText(r.Source)),
                new("开始时间", r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                new("耗时", r.ElapsedMilliseconds is { } ms ? ms.ToString("0") + " ms" : "—"),
                new("HTTP 状态", r.HttpStatusCode?.ToString() ?? "—"),
                new("结果", ResultText(r)),
                new("标记读取", ContextDetailText(r)),
            };
            if (!string.IsNullOrWhiteSpace(r.BaseUrl))
            {
                rows.Add(new DetailRow("接口地址", r.BaseUrl));
            }

            return rows;
        }
    }

    /// <summary>“说明”正文，单独以提示块展示；空则不显示。成功（可能）会固定附带一句不能当作确认成功的提醒。</summary>
    public string DetailMessage
    {
        get
        {
            if (_selected is not { } r)
            {
                return "";
            }

            var message = UserMessages.Redact(ProtocolInfo.AppendContextDetail(r.Message, r.ContextCheck));
            if (LatestResultPicker.Classify(r) != LatestResultPicker.ConnectionOutcome.PossibleSuccess)
            {
                return message;
            }

            const string caution = "成功（可能）：没有确认到有效的模型消息，不能当作确认成功，也不能当作失败；不验证模型真实身份或能力。";
            return message.Length == 0 ? caution : caution + "\n" + message;
        }
    }

    /// <summary>提示块色调：danger / warn / possible / info。</summary>
    public string DetailTone => _selected is { } r ? ToneOf(r) : "info";

    public static string ToneOf(ConnectionTestResult r) => LatestResultPicker.Classify(r) switch
    {
        LatestResultPicker.ConnectionOutcome.Failed => "danger",
        LatestResultPicker.ConnectionOutcome.PossibleSuccess => "possible",
        _ when r.ContextCheck == ConnectionContextCheck.Mismatch => "possible",
        _ => "info",
    };

    private static string ContextDetailText(ConnectionTestResult r) => r.ContextCheck == ConnectionContextCheck.NotChecked
        ? "未检验（旧记录，不追溯补判）"
        : r.PromptVersion > 0
            ? $"{ProtocolInfo.ContextLabel(r.ContextCheck)}（约256 token，Prompt v{r.PromptVersion}）"
            : ProtocolInfo.ContextLabel(r.ContextCheck);

    /// <summary>记录的结果文字：三个终态用统一文案，中间阶段沿用阶段名。</summary>
    public static string ResultLabel(ConnectionTestResult r)
    {
        var outcome = LatestResultPicker.Classify(r);
        return outcome == LatestResultPicker.ConnectionOutcome.Incomplete
            ? StageText(r.Stage)
            : LatestResultPicker.OutcomeText(outcome);
    }

    private static string ResultText(ConnectionTestResult r)
    {
        var label = ResultLabel(r);
        return string.IsNullOrWhiteSpace(r.Code) || r.Code.Equals(label, StringComparison.OrdinalIgnoreCase)
            ? label
            : $"{label}（{r.Code}）";
    }

    public static string ProtocolText(ApiProtocol p) => p switch
    {
        ApiProtocol.ChatCompletions => "Chat Completions",
        ApiProtocol.OpenAIResponses => "OpenAI Responses",
        ApiProtocol.AnthropicMessages => "Anthropic Messages",
        _ => p.ToString(),
    };

    public async Task ReloadAsync()
    {
        if (_service is null)
        {
            Status = "当前服务不支持连接记录。";
            return;
        }

        IsBusy = true;
        try
        {
            var list = await _service.GetConnectionHistoryAsync();
            _all.Clear();
            _all.AddRange(list);
            Items.Clear();
            foreach (var r in list)
            {
                Items.Add(r);
            }

            RefreshView();
            OnPropertyChanged(nameof(CountText));
            Status = "已从本地读取，不联网。";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + UserMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_selected is not { } r || _service is null)
        {
            return;
        }

        if (!_dialogs.Confirm("删除连接记录", "只删除本机这一条记录，不影响中转站或远端。确定删除吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _service.DeleteConnectionRecordAsync(r.RecordId);
            _all.Remove(r);
            Items.Remove(r);
            Selected = null;
            RefreshView();
            OnPropertyChanged(nameof(CountText));
            Status = "已删除本机记录。";
            _feed?.NotifyConnection();
        }
        catch (Exception ex)
        {
            Status = "删除失败：" + UserMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearAsync()
    {
        if (_service is null || !_dialogs.Confirm("清空连接记录", $"将删除本机全部 {_all.Count} 条连接记录，不影响远端。此操作无法撤销。确定清空吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _service.ClearConnectionHistoryAsync();
            _all.Clear();
            Items.Clear();
            Selected = null;
            RefreshView();
            Status = "已清空本机连接记录。";
            _feed?.NotifyConnection();
        }
        catch (Exception ex)
        {
            Status = "清空失败：" + UserMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (_service is null)
        {
            return;
        }

        var path = _dialogs.PickExportPath($"connection-records-{DateTime.Now:yyyyMMdd-HHmm}.json");
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _service.ExportConnectionHistoryAsync(path);
            Status = "已导出脱敏摘要（不含 Key）。";
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + UserMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool Matches(ConnectionTestResult r)
    {
        var local = r.StartedAt.ToLocalTime().Date;
        return Contains(r.ProfileName, _profileFilter)
            && Contains(r.ModelId, _modelFilter)
            && (_statusFilter.Length == 0 || ResultLabel(r) == _statusFilter)
            && (_sourceFilter.Length == 0 || SourceText(r.Source) == _sourceFilter)
            && (_fromDate is not { } from || local >= from.Date)
            && (_toDate is not { } to || local <= to.Date);
    }

    private static bool Contains(string value, string filter) =>
        filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static string SourceText(ConnectionRecordSource s) => s switch
    {
        ConnectionRecordSource.Manual => "手动",
        ConnectionRecordSource.Automatic => "自动巡检",
        ConnectionRecordSource.RoundManual => "立即巡检",
        _ => "未知",
    };

    /// <summary>阶段名。显式列出每个值；新增的未识别阶段显示为“未识别阶段”，不再被默认当作“结果未知”。</summary>
    public static string StageText(ConnectionTestStage s) => s switch
    {
        ConnectionTestStage.LocalValidationPassed => "本地检查通过",
        ConnectionTestStage.ApiReached => "API 可达",
        ConnectionTestStage.ModelResponded => "成功",
        ConnectionTestStage.PossibleSuccess => "成功（可能）",
        ConnectionTestStage.Failed => "失败",
        ConnectionTestStage.Unknown => "失败",
        _ => "未识别阶段",
    };
}
