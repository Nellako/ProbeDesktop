using System.Text.RegularExpressions;
using ProbeDesktop.App.Logic;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.Services;

/// <summary>Windows 托盘通知出口。由 TrayService 实现，返回值只表示“已提交给通知区域图标”，不代表系统已送达或用户已看到。</summary>
public interface IProblemToastSink
{
    bool IsReady { get; }

    bool Show(string title, string text, bool warning);
}

/// <summary>
/// 自动结果通知协调：把一轮自动结果逐目标交给 Core 的 INotificationService 判定（偏好、同目标同问题去重、恢复后再通知由 Core 负责），
/// 再把需要通知的同类问题合并成一条托盘通知。
/// 任何一步失败都被吞掉，绝不抛出、不阻塞调用方，也不改变检测结果、授权或状态文本。只用于自动轮次，手动运行不调用。
/// </summary>
public sealed partial class ProblemNotificationCoordinator
{
    private const int MaxTitle = 60;
    private const int MaxText = 240;

    private readonly INotificationService? _service;
    private readonly IProblemToastSink? _sink;

    public ProblemNotificationCoordinator(INotificationService? service, IProblemToastSink? sink)
    {
        _service = service;
        _sink = sink;
    }

    /// <summary>
    /// 观察一批同类自动结果。托盘图标不可用时直接返回、不向 Core 上报，避免“已记为已通知”却从未显示。
    /// 在 UI 线程调用；托盘显示留在 UI 线程。
    /// </summary>
    public async Task ObserveAutomaticAsync(NotificationCategory category, IReadOnlyList<ObservedItem> items, CancellationToken cancellationToken)
    {
        if (_service is null || _sink is not { IsReady: true } || items.Count == 0)
        {
            return;
        }

        try
        {
            var toNotify = new List<(string Label, string? Problem, string? Hint)>();
            foreach (var item in items)
            {
                try
                {
                    var decision = await _service.ObserveNotificationAsync(item.Observation, cancellationToken);
                    if (decision.ShouldNotify)
                    {
                        toNotify.Add((item.Label, decision.ProblemType, item.Hint));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // 单个目标判定失败（例如配置刚被修改）不影响其他目标。
                }
            }

            if (toNotify.Count == 0 || _sink is not { IsReady: true })
            {
                return;
            }

            var title = ProblemObservations.Title(category);
            var text = ProblemObservations.Compose(category, toNotify);
            _sink.Show(Clip(Scrub(title), MaxTitle), Clip(Scrub(text), MaxText), warning: true);
        }
        catch (Exception)
        {
            // 通知故障不影响检测。
        }
    }

    /// <summary>兜底遮蔽：完整地址与长不透明串。Core 已脱敏，这里只防御性处理。</summary>
    internal static string Scrub(string? text)
    {
        var scrubbed = UrlPattern().Replace(UserMessages.Redact(text), "（地址已省略）");
        return scrubbed.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
