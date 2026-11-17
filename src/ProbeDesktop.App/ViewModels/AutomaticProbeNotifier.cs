using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

/// <summary>
/// 身份识别巡检（定时）发起的 Probe 记录进入最终状态时的身份问题通知（异常、失败、结果未知；只有真正一致才算恢复）。
/// 记录由 ProbeRunCoordinator 轮询；这里只监听其变化，不发任何网络请求。
/// 去重：每条记录记住上次上报的“最终状态签名”，签名变化才上报；Core 再按目标与问题类型去重。
/// Start 时已有的记录只登记当前状态、不上报，因此重启不会为旧结果重复通知。所有成员在 UI 线程使用。
/// </summary>
public sealed class AutomaticProbeNotifier : IDisposable
{
    private readonly ProbeRunCoordinator _runs;
    private readonly ProblemNotificationCoordinator? _notifier;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<Guid, string> _signatures = [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private bool _started;
    private bool _disposed;

    public AutomaticProbeNotifier(ProbeRunCoordinator runs, ProblemNotificationCoordinator? notifier, CancellationToken lifetime)
    {
        _runs = runs;
        _notifier = notifier;
        _lifetime = lifetime;
    }

    /// <summary>登记现有记录的状态并开始监听。可重复调用，只生效一次。</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        foreach (var vm in _runs.Records.Where(r => ProblemObservations.IsAutomaticIdentity(r.Record)))
        {
            _signatures[vm.LocalId] = Signature(vm);
        }

        _runs.RecordsChanged += OnRecordsChanged;
    }

    private string Signature(ProbeRecordViewModel vm)
    {
        var (final, state, problem) = ProblemObservations.ClassifyProbe(vm.Record, _runs.IsAwaitingAutoResult(vm));
        return final ? $"{state}:{problem}" : "";
    }

    private void OnRecordsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _notifier is null)
        {
            return;
        }

        List<(ProbeRecord Record, bool Waiting)>? changed = null;
        foreach (var vm in _runs.Records.Where(r => ProblemObservations.IsAutomaticIdentity(r.Record)).ToList())
        {
            var signature = Signature(vm);
            _signatures.TryGetValue(vm.LocalId, out var previous);
            _signatures[vm.LocalId] = signature;
            if (signature.Length > 0 && signature != previous)
            {
                (changed ??= []).Add((vm.Record, _runs.IsAwaitingAutoResult(vm)));
            }
        }

        if (changed is not null)
        {
            _ = ObserveAsync(changed);
        }
    }

    private async Task ObserveAsync(List<(ProbeRecord Record, bool Waiting)> records)
    {
        try
        {
            await _serial.WaitAsync(_lifetime);
            try
            {
                var items = records
                    .Select(r => ProblemObservations.FromAutomaticProbe(r.Record, r.Waiting))
                    .OfType<ObservedItem>()
                    .ToList();
                await _notifier!.ObserveAutomaticAsync(NotificationCategory.Identity, items, _lifetime);
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (Exception)
        {
            // 通知故障不影响检测与记录。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runs.RecordsChanged -= OnRecordsChanged;
    }
}
