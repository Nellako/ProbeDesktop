using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>模型页里一行“最近结果”的展示状态。Tone：none / ok / possible / warn / bad。</summary>
public sealed class RecentLine : ObservableObject
{
    private string _state = "暂无测试结果";
    private string _tone = "none";
    private string _contextBadge = "";
    private string _contextTone = "none";
    private string _meta = "";
    private string _detail = "";
    private string _detailTone = "none";
    private string _note = "";
    private bool _hasRecord;

    public RecentLine(string title) => Title = title;

    public string Title { get; }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string Tone
    {
        get => _tone;
        private set => SetProperty(ref _tone, value);
    }

    /// <summary>直连测试独立的标记读取徽章文案（如“标记：通过 / 不匹配 / 无法判断 / 未检验”）；鉴别记录为空。</summary>
    public string ContextBadge
    {
        get => _contextBadge;
        private set => SetProperty(ref _contextBadge, value);
    }

    public string ContextTone
    {
        get => _contextTone;
        private set => SetProperty(ref _contextTone, value);
    }

    /// <summary>完成时间 + 来源，例如“完成于 2026-10-30 12:00:00 · 来源：自动巡检”。</summary>
    public string Meta
    {
        get => _meta;
        private set => SetProperty(ref _meta, value);
    }

    /// <summary>结果摘要说明（含标记读取校验说明）。</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string DetailTone
    {
        get => _detailTone;
        private set => SetProperty(ref _detailTone, value);
    }

    public string Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    /// <summary>有记录时才显示“历史”标记：它是过去某次的结果，不代表当前状态。</summary>
    public bool HasRecord
    {
        get => _hasRecord;
        private set => SetProperty(ref _hasRecord, value);
    }

    public void Set(string state, string tone, string meta, string note, bool hasRecord,
        string contextBadge = "", string contextTone = "none", string detail = "", string detailTone = "none")
    {
        State = state;
        Tone = tone;
        ContextBadge = contextBadge;
        ContextTone = contextTone;
        Meta = meta;
        Detail = detail;
        DetailTone = detailTone;
        Note = note;
        HasRecord = hasRecord;
    }

    public void SetEmpty(string state = "暂无测试结果", string note = "") => Set(state, "none", "", note, false);
}

/// <summary>
/// 模型页：当前“配置 + 模型”最近一次已完成的直连连接测试和鉴别检测（含失败），数据由 Core 的 ILatestResultsService 提供。
/// - 只读本地记录，不联网；时间取完成时间，不使用模型列表的拉取/刷新时间；
/// - 每次切换目标或收到记录变更通知就递增 generation，异步读取返回时若 generation 已变则丢弃，避免旧目标的结果覆盖新目标；
/// - 与“本地检查”无关，也不混入 CurrentRun。
/// 所有成员在 UI 线程使用。
/// </summary>
public sealed class ModelRecentResultsViewModel : ObservableObject, IDisposable
{
    private readonly ILatestResultsService? _latest;
    private readonly ProbeRunCoordinator _runs;
    private readonly RecordFeed _feed;
    private readonly CancellationToken _lifetime;
    private ProfileItem? _profile;
    private string _modelId = "";
    private int _gen;
    private bool _disposed;

    public ModelRecentResultsViewModel(ILatestResultsService? latest, ProbeRunCoordinator runs, RecordFeed feed, CancellationToken lifetime)
    {
        _latest = latest;
        _runs = runs;
        _feed = feed;
        _lifetime = lifetime;
        Connection = new RecentLine("最近直连测试");
        Probe = new RecentLine("最近鉴别检测");
        _feed.ConnectionRecordsChanged += OnRecordsChanged;
        _runs.RecordsChanged += OnRecordsChanged;
        Apply(null, "");
    }

    public RecentLine Connection { get; }
    public RecentLine Probe { get; }

    public bool HasTarget => _profile is not null && _modelId.Length > 0;

    public string Hint => HasTarget
        ? "来自本机已保存的历史记录，不联网；显示该配置下此模型最近一次已完成的结果，失败也会显示。"
        : "选择配置并选择或填写模型后，这里显示该模型最近一次的直连测试和鉴别检测结果。";

    /// <summary>目标（配置或模型）变化时调用。目标变了就先清空旧显示，再读取，防止短暂显示上一个目标的结果。</summary>
    public void SetTarget(ProfileItem? profile, string modelId)
    {
        if (!_disposed)
        {
            Apply(profile, modelId.Trim());
        }
    }

    private void Apply(ProfileItem? profile, string modelId)
    {
        var sameTarget = _profile?.Id == profile?.Id && _modelId == modelId;
        _profile = profile;
        _modelId = modelId;
        var gen = ++_gen;
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(Hint));
        if (profile is null || modelId.Length == 0)
        {
            Connection.SetEmpty("—");
            Probe.SetEmpty("—");
            return;
        }

        if (!sameTarget)
        {
            Connection.SetEmpty("读取中…");
            Probe.SetEmpty("读取中…");
        }

        _ = LoadAsync(gen, profile, modelId);
    }

    /// <summary>手动/巡检连接记录、鉴别记录的新增、更新、删除、清空都会走到这里；重新读取并丢弃过时结果。</summary>
    private void OnRecordsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _profile is null || _modelId.Length == 0)
        {
            return;
        }

        var gen = ++_gen;
        _ = LoadAsync(gen, _profile, _modelId);
    }

    private async Task LoadAsync(int gen, ProfileItem profile, string modelId)
    {
        if (_latest is null)
        {
            if (gen == _gen)
            {
                Connection.SetEmpty("暂无", "当前服务不支持读取最近结果。");
                Probe.SetEmpty("暂无");
            }

            return;
        }

        LatestModelResults results;
        try
        {
            results = await _latest.GetLatestResultsAsync(profile.Id, modelId, _lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (gen == _gen && !_disposed)
            {
                Connection.SetEmpty("读取失败", "无法读取本地记录；可在“连接记录”“鉴别记录”窗口查看。");
                Probe.SetEmpty("读取失败");
            }

            return;
        }

        // 目标已切换或又收到更新的变更通知：丢弃这次（可能已过时的）结果。
        if (gen != _gen || _disposed)
        {
            return;
        }

        ShowConnection(profile, results.Connection);
        ShowProbe(profile, results.Probe);
    }

    private void ShowConnection(ProfileItem profile, ConnectionTestResult? r)
    {
        if (r is null)
        {
            Connection.SetEmpty();
            return;
        }

        var (state, tone) = LatestResultPicker.DescribeConnection(r);
        var contextBadge = ProtocolInfo.ContextBadgeText(r.ContextCheck);
        var contextTone = ProtocolInfo.ContextTone(r.ContextCheck);
        var rawMsg = UserMessages.Redact(r.Message).Trim();
        var detail = r.ContextCheck == ConnectionContextCheck.NotChecked || !rawMsg.Contains("标记读取", StringComparison.Ordinal)
            ? (rawMsg.Length == 0 ? ProtocolInfo.ContextNote(r.ContextCheck) : rawMsg + " " + ProtocolInfo.ContextNote(r.ContextCheck))
            : rawMsg;
        var detailTone = r.ContextCheck == ConnectionContextCheck.Mismatch ? "possible" : "none";
        var meta = $"完成于 {Format.Time(LatestResultPicker.ConnectionTime(r))} · 来源：{ConnectionHistoryViewModel.SourceText(r.Source)}";
        var note = LatestResultPicker.ConfigDiffers(r.KeyRevision, r.BaseUrl, r.Protocol, profile.KeyRevision, profile.BaseUrl, profile.Protocol)
            ? "该记录之后，此配置的 Key 版本、地址或协议已变化，结果仅供参考。"
            : "";
        Connection.Set(UserMessages.Redact(state), tone, meta, note, true, contextBadge, contextTone, detail, detailTone);
    }

    private void ShowProbe(ProfileItem profile, ProbeRecord? r)
    {
        if (r is null)
        {
            Probe.SetEmpty();
            return;
        }

        var tone = Format.StateTone(r.State) switch
        {
            "success" => "ok",
            "danger" => "bad",
            "warning" => "warn",
            _ => "none",
        };
        var meta = $"完成于 {Format.Time(LatestResultPicker.ProbeTime(r))} · 来源：{Format.ProbeSourceText(r)}";
        var note = LatestResultPicker.ConfigDiffers(r.KeyRevision, r.BaseUrl, r.Protocol, profile.KeyRevision, profile.BaseUrl, profile.Protocol)
            ? "该记录之后，此配置的 Key 版本、地址或协议已变化，结果仅供参考。"
            : "";
        Probe.Set(Format.StateText(r.State), tone, meta, note, true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gen++;
        _feed.ConnectionRecordsChanged -= OnRecordsChanged;
        _runs.RecordsChanged -= OnRecordsChanged;
    }
}
