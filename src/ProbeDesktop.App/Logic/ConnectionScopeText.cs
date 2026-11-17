using ProbeDesktop.App.Services;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.Logic;

/// <summary>
/// 连接巡检范围确认框的正文（纯逻辑，只依赖 Core 类型，便于无界面测试）。
/// 披露内容必须与实际行为一致：每个目标每轮最多尝试 <see cref="ConnectionRoundDisplay.MaxAttempts"/> 次，
/// 最坏请求次数相应放大为原来的 3 倍，只披露次数，不承诺费用上限或费用倍数，实际费用按中转站计价；
/// 401/403 暂停该配置、429 主机冷却时不再继续重试。
/// </summary>
public static class ConnectionScopeText
{
    public static string Build(IReadOnlyList<InspectionTarget> scope, int minutes, Func<InspectionTarget, string> nameOf)
    {
        var models = scope.Sum(t => t.Models.Count);
        var attempts = ConnectionRoundDisplay.MaxAttempts;
        // 逐配置完整列出：名称、协议、精确 POST 接收地址和每个所选模型 ID；不省略任何接收方或模型。
        // Inference 对无效地址会抛异常，由调用方的 catch 处理并保持关闭，不会发送任何请求。
        var listing = string.Join("\n\n", scope.Select((t, i) =>
            $"{i + 1}. {nameOf(t)}（{ProtocolInfo.Display(t.Protocol)}）\n" +
            $"   POST {ModelApiEndpoints.Inference(t.BaseUrl, t.Protocol)}\n" +
            $"   模型（{t.Models.Count} 个）：\n" +
            string.Join("\n", t.Models.Select(m => $"     - {m}"))));

        var hosts = scope
            .Select(t => Uri.TryCreate(t.BaseUrl, UriKind.Absolute, out var u) ? u.Authority : t.BaseUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var protocols = scope.Select(t => ProtocolInfo.Display(t.Protocol)).Distinct().ToList();
        var rounds = 60.0 / minutes;
        return
            $"将巡检你勾选的 {scope.Count} 个配置、共 {models} 个模型（只含你勾选的模型，不含其他缓存模型）：\n{listing}\n" +
            $"中转站地址：{string.Join("、", hosts)}\n" +
            $"协议：{string.Join("、", protocols)}。\n" +
            $"每个模型每轮先直连发送{ProtocolInfo.ConnectionInputNote}；只有明确确认到模型响应（成功）才会停止。成功（可能）和失败（含底层结果未知）会继续尝试，每个模型每轮最多 {attempts} 次（首次加最多 {attempts - 1} 次重试，并非必须跑满）。" +
            "OpenAI Responses 协议请求参数为 max_output_tokens=64，其他协议为 max_tokens=64（均为预计的输出上限，不是费用上限）。标记读取仅校验本次按格式返回三个标记，不证明模型真实身份或能力；标记不匹配不等于断连或降智，不因此新增重试或问题通知。\n" +
            $"按每 {minutes} 分钟一轮估算，约每小时 {rounds:0.##} 轮：全部一次成功时约 {models * rounds:0.#} 次请求；最坏每个模型每轮 {attempts} 次，约 {models * rounds * attempts:0.#} 次请求（单次请求次数的 {attempts} 倍）。最多 {attempts} 次请求可能增加费用，实际费用按中转站计价，本程序不估算金额，也不承诺费用上限。\n" +
            "结果未知（例如超时）时中转站可能已经处理并计费，再次请求可能产生额外计费；本程序无法知道是否已计费。\n" +
            "例外：收到 HTTP 401/403 会立即暂停该配置，不再重试；收到 HTTP 429 会对该主机进入冷却，不再为凑次数继续请求。取消、关闭巡检、配置变化或授权失效也会立即停止后续发送。\n" +
            "记录与统计：每次尝试都会保存在连接记录；本轮统计、最新一轮结果和问题通知取每个模型本轮已完成尝试中的最高状态（成功 ＞ 成功（可能）＞ 失败，同级取最新），标记、时间和原因均来自同一条代表记录，不拼接多次或跨轮借用旧成功。\n" +
            "只发给你确认过的中转站，不调用 BazaarLink 鉴别检测（Probe）。关闭开关后立即停止后续请求。此确认只适用于连接巡检；手动单次连接测试不自动重试，身份识别巡检另行确认且不自动重试。";
    }
}
