using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed record FilterOption<T>(string Label, T? Value);

/// <summary>历史记录页。选择记录只显示本地数据，不触发网络请求。</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly IDesktopService _service;
    private readonly ProbeRunCoordinator _runs;
    private readonly IDialogService _dialogs;
    private ProbeRecordViewModel? _selected;
    private FilterOption<string> _profileFilter;
    private FilterOption<LocalRunState?> _stateFilter;
    private string _modelFilter = "";
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _status = "";
    private bool _isBusy;

    public HistoryViewModel(IDesktopService service, ProbeRunCoordinator runs, IDialogService dialogs)
    {
        _service = service;
        _runs = runs;
        _dialogs = dialogs;
        View = new ListCollectionView(runs.Records);
        View.SortDescriptions.Add(new SortDescription(nameof(ProbeRecordViewModel.CreatedAt), ListSortDirection.Descending));
        View.Filter = Filter;

        StateOptions =
        [
            new("全部状态", null),
            .. Enum.GetValues<LocalRunState>().Select(s => new FilterOption<LocalRunState?>(Format.StateText(s), s)),
        ];
        _stateFilter = StateOptions[0];
        _profileFilter = new("全部配置", null);
        RebuildProfileOptions();

        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ReloadCommand = new AsyncCommand(ReloadAsync, () => !IsBusy);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => Selected is { IsPolling: false, State: not LocalRunState.Submitting } && !IsBusy);
        ExportCommand = new AsyncCommand(ExportAsync, () => Selected is not null && !IsBusy);

        _runs.RecordsChanged += (_, _) =>
        {
            RebuildProfileOptions();
            if (_stateFilter.Value is not null)
            {
                View.Refresh();
            }

            OnPropertyChanged(nameof(CountText));
        };
    }

    public ICollectionView View { get; }
    public ObservableCollection<FilterOption<string>> ProfileOptions { get; } = [];
    public IReadOnlyList<FilterOption<LocalRunState?>> StateOptions { get; }

    public ICommand ClearFiltersCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }

    public ProbeRecordViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public FilterOption<string> ProfileFilter
    {
        get => _profileFilter;
        set
        {
            if (value is not null && SetProperty(ref _profileFilter, value))
            {
                Refresh();
            }
        }
    }

    public FilterOption<LocalRunState?> StateFilter
    {
        get => _stateFilter;
        set
        {
            if (value is not null && SetProperty(ref _stateFilter, value))
            {
                Refresh();
            }
        }
    }

    public string ModelFilter
    {
        get => _modelFilter;
        set
        {
            if (SetProperty(ref _modelFilter, value))
            {
                Refresh();
            }
        }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set
        {
            if (SetProperty(ref _fromDate, value))
            {
                Refresh();
            }
        }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set
        {
            if (SetProperty(ref _toDate, value))
            {
                Refresh();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CountText
    {
        get
        {
            var shown = View.Cast<object>().Count();
            return shown == _runs.Records.Count ? $"共 {shown} 条记录" : $"显示 {shown} / {_runs.Records.Count} 条记录";
        }
    }

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    private bool Filter(object obj)
    {
        if (obj is not ProbeRecordViewModel r)
        {
            return false;
        }

        if (_profileFilter.Value is { } profile && !string.Equals(r.ProfileName, profile, StringComparison.Ordinal))
        {
            return false;
        }

        if (_stateFilter.Value is { } state && r.State != state)
        {
            return false;
        }

        var model = ModelFilter.Trim();
        if (model.Length > 0
            && !r.ModelId.Contains(model, StringComparison.OrdinalIgnoreCase)
            && !(r.Record.ClaimedModel?.Contains(model, StringComparison.OrdinalIgnoreCase) ?? false)
            && !(r.Record.InferredModel?.Contains(model, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        var local = r.CreatedAt.ToLocalTime().Date;
        if (FromDate is { } from && local < from.Date)
        {
            return false;
        }

        if (ToDate is { } to && local > to.Date)
        {
            return false;
        }

        return true;
    }

    private void RebuildProfileOptions()
    {
        var names = _runs.Records.Select(r => r.ProfileName).Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.CurrentCulture).ToList();
        var current = _profileFilter.Value;
        var existing = ProfileOptions.Skip(1).Select(o => o.Value).ToList();
        if (ProfileOptions.Count > 0 && existing.SequenceEqual(names))
        {
            return;
        }

        ProfileOptions.Clear();
        var all = new FilterOption<string>("全部配置", null);
        ProfileOptions.Add(all);
        foreach (var name in names)
        {
            ProfileOptions.Add(new FilterOption<string>(name, name));
        }

        _profileFilter = ProfileOptions.FirstOrDefault(o => o.Value == current) ?? all;
        OnPropertyChanged(nameof(ProfileFilter));
        if (current is not null && _profileFilter.Value is null)
        {
            View.Refresh();
        }
    }

    private void ClearFilters()
    {
        _profileFilter = ProfileOptions[0];
        _stateFilter = StateOptions[0];
        _modelFilter = "";
        _fromDate = null;
        _toDate = null;
        OnPropertyChanged(string.Empty);
        Refresh();
    }

    /// <summary>
    /// 在列表中定位并选中指定本地 ID 的鉴别记录（供身份识别巡检“查看报告”使用）。只读本地数据，不联网：
    /// 先清除筛选，若记录尚未载入则重读一次本地历史。找不到返回 false。
    /// </summary>
    public async Task<bool> ShowRecordAsync(Guid localId)
    {
        ClearFilters();
        if (_runs.Records.All(r => r.LocalId != localId))
        {
            try
            {
                await _runs.ReloadAsync();
            }
            catch (Exception)
            {
            }
        }

        var found = _runs.Records.FirstOrDefault(r => r.LocalId == localId);
        if (found is null)
        {
            return false;
        }

        Selected = found;
        return true;
    }

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            await _runs.ReloadAsync();
            Status = $"已从本地重新读取历史（{Format.Time(DateTimeOffset.Now)}）。未联网。";
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dialogs.ShowException("读取历史失败", ex);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } r || r.IsPolling || r.State == LocalRunState.Submitting)
        {
            return;
        }

        var activeNote = r.IsActive
            ? "\n\n该任务尚未结束或结果未知。删除后本程序将无法再查询它，但远端检测不会因此停止，也不保证停止计费。"
            : "";
        var message =
            $"确定删除这条本地检测记录吗？\n\n配置：{r.ProfileName}\n模型：{r.ModelId}\n创建：{r.CreatedAtText}\n状态：{r.StateText}\n\n" +
            "只删除本机保存的记录，不会删除 BazaarLink 上的远端任务或报告（如有）。此操作无法撤销。" + activeNote;
        if (!_dialogs.Confirm("删除本地记录", message))
        {
            return;
        }

        if (r.IsAutoPolling)
        {
            _runs.StopAutoPolling(r, userInitiated: false);
        }

        IsBusy = true;
        try
        {
            await _service.DeleteRecordAsync(r.LocalId, _runs.Lifetime);
            if (ReferenceEquals(Selected, r))
            {
                Selected = null;
            }

            _runs.Forget(r.LocalId);
            Status = "已删除本地记录。";
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dialogs.ShowException("删除失败", ex);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task ExportAsync()
    {
        if (Selected is not { } r)
        {
            return;
        }

        var suggested = $"probe-{r.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss}-{r.LocalId.ToString("N")[..8]}.json";
        var path = _dialogs.PickExportPath(suggested);
        if (path is null)
        {
            Status = "已取消导出。";
            return;
        }

        if (File.Exists(path))
        {
            _dialogs.Error("导出失败", "该文件已存在。为避免覆盖，导出只会新建文件，请换一个文件名。");
            return;
        }

        IsBusy = true;
        try
        {
            await _service.ExportRecordAsync(r.LocalId, path, _runs.Lifetime);
            Status = $"已导出到 {path}";
            _dialogs.Info("导出完成",
                $"已导出到：\n{path}\n\n导出内容为脱敏后的结果摘要，不含 API Key、Base URL、远端任务标识和报告链接。本地历史记录保持不变。");
        }
        catch (OperationCanceledException) when (_runs.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _dialogs.ShowException("导出失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
