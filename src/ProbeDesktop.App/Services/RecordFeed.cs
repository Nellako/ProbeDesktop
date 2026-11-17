namespace ProbeDesktop.App.Services;

/// <summary>
/// 直连连接记录发生变化的进程内通知。
/// 手动测试结束、巡检每个模型完成与每轮结束、删除单条、清空、重新读取时由对应 VM 调用 NotifyConnection，
/// 订阅者（如模型页“该模型最近结果”）收到后自行重新读取本地数据。事件不携带数据。
/// 鉴别记录的变化已有 ProbeRunCoordinator.RecordsChanged（新增、更新、删除均会触发），不重复建通道。
/// 所有成员在 UI 线程使用。
/// </summary>
public sealed class RecordFeed
{
    public event EventHandler? ConnectionRecordsChanged;

    public void NotifyConnection() => ConnectionRecordsChanged?.Invoke(this, EventArgs.Empty);
}
