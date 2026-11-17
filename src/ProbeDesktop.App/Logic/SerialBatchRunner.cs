namespace ProbeDesktop.App.Logic;

public enum BatchItemOutcome { Succeeded, Failed, Skipped, Cancelled }

/// <summary>单项执行的结果。Detail 是给用户看的短句，不得包含密钥。</summary>
public sealed record BatchStepResult(BatchItemOutcome Outcome, string Detail);

public sealed record BatchItemResult<TKey>(TKey Key, BatchItemOutcome Outcome, string Detail, Exception? Error);

/// <summary>
/// 严格串行的批量执行器：一次只处理一项，每项只尝试一次（不重试），单项失败不影响后续项。
/// 取消后尚未开始的项记为 Cancelled，不会再被调用。
/// 纯逻辑，只依赖 BCL，便于以源文件链接方式被无 WPF 的测试工程使用。
/// </summary>
public static class SerialBatchRunner
{
    public static async Task<IReadOnlyList<BatchItemResult<TKey>>> RunAsync<TKey>(
        IReadOnlyList<TKey> keys,
        Func<TKey, CancellationToken, Task<BatchStepResult>> step,
        Action<TKey>? onStart,
        Action<BatchItemResult<TKey>>? onDone,
        CancellationToken cancellationToken)
    {
        var results = new List<BatchItemResult<TKey>>(keys.Count);
        foreach (var key in keys)
        {
            BatchItemResult<TKey> result;
            if (cancellationToken.IsCancellationRequested)
            {
                result = new BatchItemResult<TKey>(key, BatchItemOutcome.Cancelled, "已取消，未发送请求。", null);
            }
            else
            {
                onStart?.Invoke(key);
                try
                {
                    var r = await step(key, cancellationToken).ConfigureAwait(true);
                    result = new BatchItemResult<TKey>(key, r.Outcome, r.Detail, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = new BatchItemResult<TKey>(key, BatchItemOutcome.Cancelled, "已取消；若请求已发出，结果未知。", null);
                }
                catch (Exception ex)
                {
                    result = new BatchItemResult<TKey>(key, BatchItemOutcome.Failed, "", ex);
                }
            }

            results.Add(result);
            onDone?.Invoke(result);
        }

        return results;
    }
}
