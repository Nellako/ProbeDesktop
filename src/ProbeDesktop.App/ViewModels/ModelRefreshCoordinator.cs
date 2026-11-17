using System.Windows.Input;
using ProbeDesktop.App.Logic;
using ProbeDesktop.App.Mvvm;
using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.ViewModels;

public sealed record RefreshOutcome(Guid ProfileId, string ProfileName, BatchItemOutcome Outcome, string Detail, bool IsBatch);

/// <summary>
/// 模型列表拉取的共享协调器：配置页的“拉取所选配置 / 全部拉取”和模型页 Step 1 的单拉都走这里，复用同一个 ProfileStore。
/// 规则：
/// - 同一时间只有一次拉取（单拉与批量互斥）；
/// - 批量只确认一次（统一列出接收方、协议和费用/风险），确认后严格串行，每个配置只请求一次，不重试；
/// - 单个配置失败不影响其余，失败保持原缓存；成功（含空列表）才更新缓存；
/// - 每个请求发出前才用最新的配置生成 10 分钟有效的同意凭据，并核对 Base URL、协议、Key 版本与确认时一致；
///   不一致（已删除、已编辑、正在保存）则跳过，不发请求；
/// - 拉取期间该配置 IsBusy，编辑保存与删除被禁用。
/// 所有成员在 UI 线程使用。
/// </summary>
public sealed class ModelRefreshCoordinator : ObservableObject
{
    private sealed record Snap(Guid Id, string Name, string BaseUrl, ApiProtocol Protocol, int KeyRevision, string Recipient);

    private readonly IDesktopService _service;
    private readonly ProfileStore _store;
    private readonly IDialogService _dialogs;
    private readonly CancellationToken _lifetime;
    private bool _isRunning;
    private string _progressText = "";
    private string _lastSummary = "";

    public ModelRefreshCoordinator(IDesktopService service, ProfileStore store, IDialogService dialogs, CancellationToken lifetime)
    {
        _service = service;
        _store = store;
        _dialogs = dialogs;
        _lifetime = lifetime;
    }

    /// <summary>每个配置处理完（成功/失败/跳过/取消）后触发。</summary>
    public event EventHandler<RefreshOutcome>? Outcome;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>最近一次拉取（单拉或批量）的汇总说明。</summary>
    public string LastSummary
    {
        get => _lastSummary;
        private set => SetProperty(ref _lastSummary, value);
    }

    // ───────────── 入口 ─────────────

    public async Task RefreshOneAsync(ProfileItem item)
    {
        if (IsRunning || item.IsBusy)
        {
            return;
        }

        var snap = Prepare(item, out var problem);
        if (snap is null)
        {
            item.SetRefresh(RefreshState.Skipped, problem);
            _dialogs.Error("无法拉取模型列表", problem);
            return;
        }

        IsRunning = true;
        try
        {
            var message =
                "即将由本程序直接向中转站请求模型列表：\n\n" +
                $"GET {snap.Recipient}\n\n" +
                $"配置：{snap.Name}\n" +
                $"协议：{ProtocolInfo.Display(snap.Protocol)}\n" +
                $"请求头会携带该配置保存的 API Key（{ProtocolInfo.AuthHeaderText(snap.Protocol)}，Key 版本 {snap.KeyRevision}，此处不显示）。\n\n" +
                "• 接收方仅为上述中转站地址，不经过 BazaarLink。\n" +
                "• 程序不会自动补 /v1，不会跟随重定向，也不会在失败后改用其他协议重试。\n" +
                "• 只请求这一个地址，不会自动翻页；若服务端提示还有更多页，本次会被拒绝而不是只保存部分列表。\n" +
                "• 失败时保留上次成功的模型缓存；成功但返回空列表会把缓存更新为空。\n\n" +
                "确认向该地址发送请求吗？";
            if (!_dialogs.Confirm("确认拉取模型列表", message))
            {
                LastSummary = "已取消，未发送任何请求。";
                return;
            }

            await RunAsync([snap], batch: false);
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
        }
    }

    public async Task RefreshAllAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var snaps = new List<Snap>();
        var skipped = 0;
        foreach (var item in _store.Items.ToList())
        {
            if (item.IsBusy)
            {
                item.SetRefresh(RefreshState.Skipped, "正在保存或拉取，未发送请求。");
                skipped++;
                continue;
            }

            var snap = Prepare(item, out var problem);
            if (snap is null)
            {
                item.SetRefresh(RefreshState.Skipped, problem);
                skipped++;
            }
            else
            {
                snaps.Add(snap);
            }
        }

        if (snaps.Count == 0)
        {
            LastSummary = _store.Items.Count == 0
                ? "还没有配置，无可拉取。"
                : $"没有可拉取的配置（{skipped} 个因地址无效或正在保存被跳过）。";
            _dialogs.Info("无法全部拉取", LastSummary);
            return;
        }

        IsRunning = true;
        try
        {
            var listing = string.Join("\n", snaps.Select((s, i) =>
                $"{i + 1}. {s.Name}（{ProtocolInfo.Display(s.Protocol)}）GET {s.Recipient}"));

            var message =
                $"即将由本程序依次（串行）向 {snaps.Count} 个中转站各请求一次模型列表：\n\n" +
                listing + "\n\n" +
                (skipped > 0 ? $"另有 {skipped} 个配置因地址无效或正在保存而跳过，不会发送。\n\n" : "") +
                "• 每个请求携带该配置自己保存的 API Key（按各自协议的认证头，Key 不显示），只发给该配置的地址，不经过 BazaarLink 或其他第三方。\n" +
                "• 每个配置只请求一次：不自动补 /v1，不跟随重定向，不自动翻页，不重试，也不改用其他协议。\n" +
                "• 某个配置失败不影响其他配置；失败的配置保留原模型缓存，成功才更新。\n" +
                "• 成功但返回空列表会把该配置的缓存更新为空，巡检可选范围随之缩小。\n" +
                "• 模型列表请求通常不按模型计费，但是否计入额度或请求数以各中转站为准。\n\n" +
                "仅确认后才会发送。确认向以上全部地址发送请求吗？";
            if (!_dialogs.Confirm("确认全部拉取模型列表", message))
            {
                LastSummary = "已取消，未发送任何请求。";
                return;
            }

            await RunAsync(snaps, batch: true);
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
        }
    }

    // ───────────── 执行 ─────────────

    private Snap? Prepare(ProfileItem item, out string problem)
    {
        var profile = item.Profile;
        if (!Endpoints.TryValidateBaseUrl(profile.BaseUrl, out _, out var error))
        {
            problem = "Base URL 无效，未发送：" + error;
            return null;
        }

        var recipient = ProtocolInfo.TryModelsUrl(profile.BaseUrl, profile.Protocol);
        if (recipient is null)
        {
            problem = "无法按该配置的协议和 Base URL 生成模型列表地址，未发送；请先到“配置管理”检查。";
            return null;
        }

        problem = "";
        return new Snap(profile.Id, profile.Name, profile.BaseUrl, profile.Protocol, profile.KeyRevision, recipient);
    }

    private async Task RunAsync(IReadOnlyList<Snap> snaps, bool batch)
    {
        foreach (var s in snaps)
        {
            _store.Find(s.Id)?.SetRefresh(RefreshState.Queued);
        }

        var done = 0;
        var results = await SerialBatchRunner.RunAsync(
            snaps,
            (snap, ct) => StepAsync(snap, ct),
            snap =>
            {
                ProgressText = batch ? $"正在拉取 {done + 1}/{snaps.Count}：{snap.Name}" : $"正在拉取：{snap.Name}";
                _store.Find(snap.Id)?.SetRefresh(RefreshState.Running);
            },
            r =>
            {
                done++;
                var snap = r.Key;
                var item = _store.Find(snap.Id);
                var detail = r.Outcome == BatchItemOutcome.Failed
                    ? (r.Error is null ? "未知错误。" : UserMessages.Describe(r.Error)) + " 已保留原模型缓存。"
                    : r.Detail;
                item?.SetRefresh(ToState(r.Outcome), detail);
                Outcome?.Invoke(this, new RefreshOutcome(snap.Id, snap.Name, r.Outcome, detail, batch));
            },
            _lifetime);

        var ok = results.Count(r => r.Outcome == BatchItemOutcome.Succeeded);
        var failed = results.Count(r => r.Outcome == BatchItemOutcome.Failed);
        var skip = results.Count(r => r.Outcome == BatchItemOutcome.Skipped);
        var cancelled = results.Count(r => r.Outcome == BatchItemOutcome.Cancelled);
        LastSummary = batch
            ? $"全部拉取完成：成功 {ok}，失败 {failed}，跳过 {skip}" + (cancelled > 0 ? $"，取消 {cancelled}" : "") + "。失败的配置保留了原缓存。"
            : results.Count == 1 ? results[0].Outcome switch
            {
                BatchItemOutcome.Succeeded => "拉取完成。",
                BatchItemOutcome.Failed => "拉取失败，已保留上次缓存。",
                BatchItemOutcome.Skipped => "未发送请求。",
                _ => "已取消。",
            } : "";

        if (!batch && results.Count == 1 && results[0] is { Outcome: BatchItemOutcome.Failed, Error: { } error } && !_lifetime.IsCancellationRequested)
        {
            _dialogs.ShowException("拉取模型列表失败", error);
        }
    }

    private async Task<BatchStepResult> StepAsync(Snap snap, CancellationToken ct)
    {
        // 发送前用“此刻”的配置重新核对：确认之后发生的删除、编辑、保存中，都不能让旧确认继续生效。
        var item = _store.Find(snap.Id);
        if (item is null)
        {
            return new BatchStepResult(BatchItemOutcome.Skipped, "配置已被删除，未发送请求。");
        }

        if (item.IsBusy)
        {
            return new BatchStepResult(BatchItemOutcome.Skipped, "该配置正在保存，未发送请求；可稍后重新拉取。");
        }

        var profile = item.Profile;
        var recipient = ProtocolInfo.TryModelsUrl(profile.BaseUrl, profile.Protocol);
        if (!string.Equals(profile.BaseUrl, snap.BaseUrl, StringComparison.Ordinal)
            || profile.Protocol != snap.Protocol
            || profile.KeyRevision != snap.KeyRevision
            || !string.Equals(recipient, snap.Recipient, StringComparison.Ordinal))
        {
            return new BatchStepResult(BatchItemOutcome.Skipped, "确认后该配置的地址、协议或 Key 已变化，未发送请求；请重新确认。");
        }

        var consent = new OutboundConsent(
            profile.Id, profile.KeyRevision, profile.BaseUrl, OutboundPurpose.ModelList, recipient!, null, DateTimeOffset.UtcNow,
            Protocol: profile.Protocol);

        item.IsBusy = true;
        try
        {
            var updated = await _service.RefreshModelsAsync(profile.Id, consent, ct);
            _store.Upsert(updated);
            var defaultNote = !string.IsNullOrEmpty(profile.DefaultModelId) && string.IsNullOrEmpty(updated.DefaultModelId)
                ? $" 原默认模型“{profile.DefaultModelId}”不在新列表中，已清除默认设置。"
                : "";
            var text = ModelCacheText.RefreshedLine(updated.Models.Count, Format.Time(updated.ModelsFetchedAt));
            return new BatchStepResult(BatchItemOutcome.Succeeded, text + defaultNote);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static RefreshState ToState(BatchItemOutcome outcome) => outcome switch
    {
        BatchItemOutcome.Succeeded => RefreshState.Succeeded,
        BatchItemOutcome.Failed => RefreshState.Failed,
        BatchItemOutcome.Skipped => RefreshState.Skipped,
        _ => RefreshState.Cancelled,
    };
}
