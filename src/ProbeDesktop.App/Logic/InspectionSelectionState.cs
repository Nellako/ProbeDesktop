namespace ProbeDesktop.App.Logic;

/// <summary>可持久化的单个配置选择：是否纳入巡检 + 已选模型稳定 ID。取消纳入不会清除 ModelIds。</summary>
public sealed record SelectionEntry(Guid ProfileId, bool Included, IReadOnlyList<string> ModelIds);

/// <summary>
/// 巡检选择状态（纯逻辑）。规则：
/// 1) 以 ProfileId + 模型 ID 字符串为稳定标识，不使用名称或下标；
/// 2) 首次为空：没有任何配置被纳入，也没有任何模型被选；
/// 3) 取消配置勾选只改变 Included，子模型选择保留，重新勾选后恢复；
/// 4) 模型缓存更新（Reconcile）：保留仍存在的已选模型，已不存在的移除，新增模型一律不选；
/// 5) 有效范围只包含“已纳入且至少选中一个现存模型”的配置；空选择得到空范围，不发请求、不扩大授权。
/// </summary>
public sealed class InspectionSelectionState
{
    private sealed class Entry
    {
        public bool Included;
        public HashSet<string> Selected = new(StringComparer.Ordinal);
        public HashSet<string> Cached = new(StringComparer.Ordinal);
        public bool CacheKnown;
    }

    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>载入已保存的选择（尚未知道缓存时先原样保留，等待 Reconcile 修剪）。</summary>
    public void Load(IEnumerable<SelectionEntry> saved)
    {
        _entries.Clear();
        foreach (var s in saved)
        {
            _entries[s.ProfileId] = new Entry
            {
                Included = s.Included,
                Selected = new HashSet<string>(s.ModelIds, StringComparer.Ordinal),
            };
        }
    }

    /// <summary>用当前各配置的缓存模型 ID 对齐：移除不存在的配置与模型；新增模型保持未选；新配置默认未纳入。</summary>
    public void Reconcile(IEnumerable<(Guid ProfileId, IReadOnlyCollection<string> CachedModelIds)> current)
    {
        var list = current.ToList();
        var present = list.Select(c => c.ProfileId).ToHashSet();
        foreach (var gone in _entries.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _entries.Remove(gone);
        }

        foreach (var (id, cached) in list)
        {
            if (!_entries.TryGetValue(id, out var e))
            {
                e = new Entry();
                _entries[id] = e;
            }

            e.Cached = new HashSet<string>(cached, StringComparer.Ordinal);
            e.CacheKnown = true;
            e.Selected.IntersectWith(e.Cached);
        }
    }

    public bool IsIncluded(Guid profileId) => _entries.TryGetValue(profileId, out var e) && e.Included;

    public void SetIncluded(Guid profileId, bool included)
    {
        if (_entries.TryGetValue(profileId, out var e))
        {
            e.Included = included;
        }
    }

    public bool IsModelSelected(Guid profileId, string modelId)
        => _entries.TryGetValue(profileId, out var e) && e.Selected.Contains(modelId);

    /// <summary>勾选/取消某个模型。只接受当前缓存中存在的模型 ID，其他 ID 被忽略。</summary>
    public bool SetModelSelected(Guid profileId, string modelId, bool selected)
    {
        if (!_entries.TryGetValue(profileId, out var e) || (e.CacheKnown && !e.Cached.Contains(modelId)))
        {
            return false;
        }

        return selected ? e.Selected.Add(modelId) : e.Selected.Remove(modelId);
    }

    /// <summary>用户明确点击“全选”才会选中该配置当前缓存的全部模型；不会因勾选配置而隐式全选。</summary>
    public void SelectAll(Guid profileId)
    {
        if (_entries.TryGetValue(profileId, out var e))
        {
            e.Selected = new HashSet<string>(e.Cached, StringComparer.Ordinal);
        }
    }

    public void ClearModels(Guid profileId)
    {
        if (_entries.TryGetValue(profileId, out var e))
        {
            e.Selected.Clear();
        }
    }

    public int SelectedCount(Guid profileId)
        => _entries.TryGetValue(profileId, out var e) ? e.Selected.Count : 0;

    /// <summary>用于持久化：包含未纳入配置的子选择；模型 ID 按序号排序以保证稳定。</summary>
    public IReadOnlyList<SelectionEntry> Snapshot()
        => _entries
            .Where(kv => kv.Value.Included || kv.Value.Selected.Count > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new SelectionEntry(kv.Key, kv.Value.Included, kv.Value.Selected.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();

    /// <summary>有效范围：已纳入且至少选中一个现存模型的配置及其模型。空选择 → 空列表。</summary>
    public IReadOnlyList<(Guid ProfileId, IReadOnlyList<string> ModelIds)> EffectiveScope()
        => _entries
            .Where(kv => kv.Value.Included && kv.Value.Selected.Count > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.Selected.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();

    public int EffectiveModelCount => EffectiveScope().Sum(s => s.ModelIds.Count);

    /// <summary>与另一份已保存的选择是否等价（忽略“未纳入且无子选择”的空条目与顺序）。用于判断本地修剪后是否需要回写。</summary>
    public bool IsSameAs(IEnumerable<SelectionEntry> other)
    {
        static Dictionary<Guid, (bool Included, string[] Ids)> Normalize(IEnumerable<SelectionEntry> entries)
            => entries
                .Where(e => e.Included || e.ModelIds.Count > 0)
                .ToDictionary(e => e.ProfileId, e => (e.Included, e.ModelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));

        var mine = Normalize(Snapshot());
        var theirs = Normalize(other);
        if (mine.Count != theirs.Count)
        {
            return false;
        }

        foreach (var (id, value) in mine)
        {
            if (!theirs.TryGetValue(id, out var o) || o.Included != value.Included || !o.Ids.SequenceEqual(value.Ids, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
