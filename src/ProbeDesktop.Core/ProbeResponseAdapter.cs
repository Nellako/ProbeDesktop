using System.Text.Json;

namespace ProbeDesktop.Core;

internal static class ProbeResponseAdapter
{
    private static readonly HashSet<string> IdentityStatuses = new(StringComparer.Ordinal)
        { "match", "mismatch", "uncertain", "insufficient_data" };
    private static readonly HashSet<string> VerdictStatuses = new(StringComparer.Ordinal)
        { "clean_match", "clean_match_family_only", "clean_match_submodel_mismatch", "plain_mismatch",
          "spoof_behavior_induced", "spoof_selfclaim_forged", "ambiguous", "insufficient_data" };
    private static readonly HashSet<string> ItemStatuses = new(StringComparer.Ordinal) { "pending", "running", "done", "error" };

    internal static string? AcknowledgedId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? id = String(root, "runId") ?? String(root, "id");
        return OutboundSecurity.IsSafeRunId(id) ? id : null;
    }

    internal static ProbeRecord Apply(ProbeRecord previous, JsonElement root, SensitiveText sanitizer, bool isSubmission)
    {
        if (root.ValueKind != JsonValueKind.Object) return Unknown(previous, isSubmission, "服务响应不是可识别的任务对象。");
        // Some documented acknowledgments include a run object; never search arbitrary nested response properties.
        JsonElement run = Object(root, "run") ?? root;
        string? returnedId = AcknowledgedId(root) ?? AcknowledgedId(run);
        if (returnedId is not null && sanitizer.CleanAddress(returnedId) != returnedId)
            return Unknown(previous, isSubmission, "服务返回的任务标识含敏感凭据，已拒绝保存或查询该标识；请勿重新提交。");
        string? id = previous.RemoteRunId ?? returnedId;
        if (returnedId is not null && previous.RemoteRunId is not null && returnedId != previous.RemoteRunId)
            return Unknown(previous, isSubmission, "响应任务标识与当前任务不匹配，已停止解释响应。");
        if (isSubmission && id is null)
            return Unknown(previous, true, "服务未返回可安全查询的任务标识；提交结果未知，请勿自动重新提交。");
        ProbeRecord record = previous with { RemoteRunId = id, Error = null };
        string? status = String(run, "status");
        // A runId-only POST acknowledgment is sufficient to poll, but never to infer completion.
        if (isSubmission && status is null)
            return record with { State = LocalRunState.Running, RemoteStatus = "running" };
        if (status is not ("running" or "completed" or "failed" or "aborted"))
            return Unknown(record, isSubmission, "服务任务状态不受当前适配器支持；保留任务标识，可稍后查询。");

        JsonElement? assessment = Object(run, "identityAssessment");
        JsonElement? verdict = Object(run, "verdict");
        string? identityStatus = assessment is { } a ? String(a, "status") : null;
        string? verdictStatus = verdict is { } v ? String(v, "status") : null;
        if (verdict is null && assessment is { } container) verdict = Object(container, "verdict");
        if (verdictStatus is null && verdict is { } nestedVerdict) verdictStatus = String(nestedVerdict, "status");
        bool knownAssessment = identityStatus is not null && IdentityStatuses.Contains(identityStatus);
        bool knownVerdict = verdictStatus is not null && VerdictStatuses.Contains(verdictStatus);

        List<ProbeItem> items = [];
        bool validItems = TryItems(run, sanitizer, items);
        if (status == "completed" && (!validItems || (!knownAssessment && !knownVerdict)))
            return Unknown(record with { RemoteStatus = status }, isSubmission,
                "服务声称已完成，但结果结构不兼容，不能据此认定检测完成。");
        if ((identityStatus is not null && !knownAssessment) || (verdictStatus is not null && !knownVerdict))
            return Unknown(record with { RemoteStatus = status }, isSubmission, "服务结果枚举发生变化，当前版本无法可靠解释。");

        List<ProbeWarning> warnings = [];
        ReadWarnings(run, sanitizer, warnings);
        if (assessment is { } assessmentValue) ReadWarnings(assessmentValue, sanitizer, warnings);
        if (verdict is { } verdictValue) ReadWarnings(verdictValue, sanitizer, warnings);
        List<string> reasoning = [];
        if (verdict is { } reasoningOwner) ReadReasoning(reasoningOwner, sanitizer, reasoning);
        if (reasoning.Count == 0 && assessment is { } fallback) ReadReasoning(fallback, sanitizer, reasoning);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return sanitizer.Record(record with
        {
            State = status switch
            {
                "running" => LocalRunState.Running,
                "completed" => LocalRunState.Completed,
                "failed" => LocalRunState.Failed,
                _ => LocalRunState.Aborted
            },
            RemoteStatus = status,
            CompletedAt = status == "running" ? null : Timestamp(run, "completedAt") ?? now,
            Phase = Phase(run, sanitizer),
            Error = sanitizer.Optional(String(run, "error"), 2048),
            IdentityStatus = identityStatus,
            VerdictStatus = verdictStatus,
            InferredFamily = verdict is { } family ? sanitizer.Optional(String(family, "trueFamily"), 256) : null,
            InferredModel = verdict is { } model ? sanitizer.Optional(String(model, "trueModel"), 256) : null,
            Confidence = verdict is { } confidence ? sanitizer.Optional(Scalar(confidence, "confidence"), 128) : null,
            Reasoning = reasoning,
            Warnings = warnings.Distinct().Take(100).ToList(),
            Items = items,
            TotalInputTokens = NonnegativeLong(run, "totalInputTokens"),
            TotalOutputTokens = NonnegativeLong(run, "totalOutputTokens"),
            ReportUrl = sanitizer.CleanReport(String(run, "reportUrl"))
        });
    }

    private static ProbeRecord Unknown(ProbeRecord record, bool submission, string message) => record with
    {
        State = submission ? LocalRunState.SubmissionUnknown : LocalRunState.IncompatibleResponse,
        Error = message
    };

    private static bool TryItems(JsonElement run, SensitiveText sanitizer, List<ProbeItem> result)
    {
        if (!run.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 500)
            return false;
        bool valid = true;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { valid = false; continue; }
            string? status = String(item, "status");
            string? id = String(item, "probeId") ?? String(item, "id");
            if (status is null || !ItemStatuses.Contains(status) || string.IsNullOrEmpty(id)) { valid = false; continue; }
            string? passed = Scalar(item, "passed");
            if (passed is not (null or "true" or "false" or "warning")) { valid = false; continue; }
            string? detail = String(item, "passReason");
            string? error = String(item, "error");
            if (error is not null) detail = detail is null ? error : detail + "\n" + error;
            result.Add(new ProbeItem
            {
                ProbeId = sanitizer.Text(id, 128),
                Label = sanitizer.Text(String(item, "label"), 256),
                Group = sanitizer.Text(String(item, "group"), 128),
                Status = status,
                Passed = passed,
                Detail = sanitizer.Optional(detail, 2048),
                DurationMs = NonnegativeDouble(item, "durationMs"),
                InputTokens = NonnegativeLong(item, "inputTokens"),
                OutputTokens = NonnegativeLong(item, "outputTokens")
            });
        }
        return valid;
    }

    private static void ReadWarnings(JsonElement source, SensitiveText sanitizer, List<ProbeWarning> result)
    {
        if (!source.TryGetProperty("warnings", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement warning in list.EnumerateArray().Take(100))
        {
            if (result.Count >= 100) break;
            if (warning.ValueKind == JsonValueKind.String)
            {
                result.Add(new ProbeWarning("服务警告", "warning", sanitizer.Text(warning.GetString(), 2048)));
            }
            else if (warning.ValueKind == JsonValueKind.Object)
            {
                string? evidence = String(warning, "evidence") ?? String(warning, "message");
                if (evidence is null && warning.TryGetProperty("evidence", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                    evidence = string.Join("\n", array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Take(20).Select(x => sanitizer.Text(x.GetString(), 256)));
                result.Add(new ProbeWarning(sanitizer.Text(String(warning, "label") ?? String(warning, "type") ?? "服务警告", 256),
                    sanitizer.Text(String(warning, "severity") ?? "warning", 64), sanitizer.Text(evidence, 2048)));
            }
        }
    }

    private static void ReadReasoning(JsonElement source, SensitiveText sanitizer, List<string> result)
    {
        if (!source.TryGetProperty("reasoning", out JsonElement reasoning)) return;
        if (reasoning.ValueKind == JsonValueKind.String) result.Add(sanitizer.Text(reasoning.GetString(), 2048));
        else if (reasoning.ValueKind == JsonValueKind.Array)
            foreach (JsonElement line in reasoning.EnumerateArray().Take(100))
                if (line.ValueKind == JsonValueKind.String) result.Add(sanitizer.Text(line.GetString(), 2048));
    }

    private static string? Phase(JsonElement source, SensitiveText sanitizer)
    {
        string? phase = String(source, "phase");
        if (phase is not null) return sanitizer.Text(phase, 256);
        List<string> phases = [];
        foreach (string name in new[] { "analysisPhase", "linguisticPhase", "effortVerificationPhase" })
            if (Object(source, name) is { } value && String(value, "status") is string status)
                phases.Add(name + ": " + sanitizer.Text(status, 64));
        return phases.Count == 0 ? null : string.Join("; ", phases);
    }

    internal static string? String(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement? Object(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static string? Scalar(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number) => value.GetRawText(),
            _ => null
        };
    }
    private static long? NonnegativeLong(JsonElement owner, string name) => owner.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt64IfNumber(out long number) && number >= 0 ? number : null;
    private static double? NonnegativeDouble(JsonElement owner, string name) => owner.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0 ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement owner, string name)
    {
        if (NonnegativeLong(owner, name) is not long value) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value); } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static bool TryGetInt64IfNumber(this JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result);
    }
}
