namespace ProbeDesktop.App.Logic;

/// <summary>
/// 模型缓存的中性展示文案（纯文本逻辑，不依赖 WPF，可被测试项目直接链接）。
/// ModelsFetchedAt 只表示“本地缓存记录的时间”：旧版本导入的配置也可能带有该时间，并非一次真实联网拉取，
/// 因此界面不得把它写成“拉取于/已获取”，也不得暗示列表完整。不改动存储字段，旧状态无需迁移。
/// </summary>
public static class ModelCacheText
{
    /// <summary>没有缓存时间记录（从未拉取，也未导入）。</summary>
    public const string NoCache = "未拉取";

    /// <summary>说明性后缀：缓存时间的含义与限制。</summary>
    public const string Disclaimer = "缓存时间是本地缓存记录的时间，旧状态或导入的配置可能并非联网拉取；不代表联网获取，也不代表列表完整。";

    /// <summary>列表单元格用的短摘要，例如“12 个模型（缓存 2026-10-30 18:42:10）”。</summary>
    public static string Summary(bool hasCache, int count, string timeText)
        => hasCache ? $"{count} 个模型（缓存 {timeText}）" : NoCache;

    /// <summary>悬浮提示：摘要加缓存时间含义说明。</summary>
    public static string SummaryTooltip(bool hasCache, int count, string timeText)
        => hasCache ? $"{count} 个模型，缓存时间（可能为导入）：{timeText}。{Disclaimer}" : "尚无模型缓存。";

    /// <summary>模型页的数量说明。</summary>
    public static string CountLine(int total, int shown, string timeText)
        => $"共 {total} 个模型，显示 {shown} 个；缓存时间（可能为导入）：{timeText}。{Disclaimer}";

    /// <summary>网络刷新成功后的结果文案；用“缓存已更新”，不再称“已获取”。</summary>
    public static string RefreshedLine(int count, string timeText)
        => count == 0
            ? "服务返回了空模型列表，缓存已更新为空（巡检范围随之缩小）。"
            : $"缓存已更新：{count} 个模型（更新时间 {timeText}）。";
}
