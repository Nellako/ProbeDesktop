using System.Collections.ObjectModel;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>某个配置最近一次“拉取模型列表”在界面上的状态（单拉与全部拉取共用）。</summary>
public enum RefreshState { None, Queued, Running, Succeeded, Failed, Skipped, Cancelled }

/// <summary>界面侧的配置列表包装。实例按 Id 稳定，更新时就地替换内部记录，避免选择状态丢失。</summary>
public sealed class ProfileItem : ObservableObject
{
    private RelayProfile _profile;
    private bool _isBusy;
    private RefreshState _refreshState;
    private string _refreshDetail = "";

    public ProfileItem(RelayProfile profile) => _profile = profile;

    /// <summary>最近一次拉取状态。独立于 Profile 内容：配置被更新时不会被清除。</summary>
    public RefreshState RefreshState
    {
        get => _refreshState;
        private set
        {
            if (SetProperty(ref _refreshState, value))
            {
                OnPropertyChanged(nameof(RefreshStateText));
                OnPropertyChanged(nameof(RefreshTone));
            }
        }
    }

    public string RefreshDetail
    {
        get => _refreshDetail;
        private set
        {
            if (SetProperty(ref _refreshDetail, value))
            {
                OnPropertyChanged(nameof(RefreshStateText));
            }
        }
    }

    public string RefreshStateText => RefreshState switch
    {
        RefreshState.None => "—",
        RefreshState.Queued => "排队中",
        RefreshState.Running => "拉取中…",
        RefreshState.Succeeded => "成功：" + RefreshDetail,
        RefreshState.Failed => "失败：" + RefreshDetail,
        RefreshState.Skipped => "跳过：" + RefreshDetail,
        RefreshState.Cancelled => "已取消",
        _ => "",
    };

    /// <summary>none / info / ok / warn / bad，用于状态列颜色。</summary>
    public string RefreshTone => RefreshState switch
    {
        RefreshState.Succeeded => "ok",
        RefreshState.Failed => "bad",
        RefreshState.Skipped or RefreshState.Cancelled => "warn",
        RefreshState.Queued or RefreshState.Running => "info",
        _ => "none",
    };

    public void SetRefresh(RefreshState state, string detail = "")
    {
        RefreshDetail = detail;
        RefreshState = state;
    }

    public Guid Id => _profile.Id;
    public RelayProfile Profile => _profile;
    public string Name => _profile.Name;
    public string BaseUrl => _profile.BaseUrl;
    public ApiProtocol Protocol => _profile.Protocol;
    public string ProtocolDisplay => ProtocolInfo.Display(_profile.Protocol);
    public string DefaultModelId => _profile.DefaultModelId;
    public int ModelCount => _profile.Models.Count;
    public int KeyRevision => _profile.KeyRevision;
    public bool HasModelCache => _profile.ModelsFetchedAt is not null;
    /// <summary>缓存时间文本。该时间只代表本地缓存记录，旧状态/导入的配置可能并非联网拉取。</summary>
    public string FetchedAtText => Format.Time(_profile.ModelsFetchedAt, "无缓存时间");
    public string UpdatedAtText => Format.Time(_profile.UpdatedAt);
    public string ModelSummary => ModelCacheText.Summary(HasModelCache, ModelCount, FetchedAtText);
    public string ModelSummaryTooltip => ModelCacheText.SummaryTooltip(HasModelCache, ModelCount, FetchedAtText);
    public string DefaultModelText => string.IsNullOrEmpty(DefaultModelId) ? "未设置" : DefaultModelId;

    /// <summary>该配置正在进行模型拉取或保存，期间禁止编辑保存/删除以免互相覆盖。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public void Update(RelayProfile profile)
    {
        if (profile.Id != _profile.Id)
        {
            return;
        }

        _profile = profile;
        OnPropertyChanged(string.Empty);
    }

    public override string ToString() => Name;
}

public sealed class ProfileStore
{
    public ObservableCollection<ProfileItem> Items { get; } = [];

    /// <summary>某个配置内容被更新（参数为其 Id）。</summary>
    public event EventHandler<Guid>? ProfileUpdated;

    public ProfileItem? Find(Guid id) => Items.FirstOrDefault(p => p.Id == id);

    public void Load(IEnumerable<RelayProfile> profiles)
    {
        var incoming = profiles.ToList();
        foreach (var stale in Items.Where(i => incoming.All(p => p.Id != i.Id)).ToList())
        {
            Items.Remove(stale);
        }

        foreach (var profile in incoming)
        {
            Upsert(profile);
        }
    }

    public ProfileItem Upsert(RelayProfile profile)
    {
        var existing = Find(profile.Id);
        if (existing is null)
        {
            existing = new ProfileItem(profile);
            var index = 0;
            while (index < Items.Count && string.Compare(Items[index].Name, profile.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
            {
                index++;
            }

            Items.Insert(index, existing);
        }
        else
        {
            existing.Update(profile);
        }

        ProfileUpdated?.Invoke(this, profile.Id);
        return existing;
    }

    public void Remove(Guid id)
    {
        var existing = Find(id);
        if (existing is not null)
        {
            Items.Remove(existing);
        }
    }
}
