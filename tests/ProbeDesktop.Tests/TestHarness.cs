using ProbeDesktop.Core;

namespace ProbeDesktop.Tests;

public sealed class TestFailure : Exception
{
    public TestFailure(string message) : base(message) { }
}

public static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailure($"{message}: expected <{expected}> actual <{actual}>");
    }

    public static void NotEqual<T>(T unexpected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new TestFailure($"{message}: value should differ from <{unexpected}>");
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null) throw new TestFailure($"{message}: expected non-null");
    }

    public static void Null(object? value, string message)
    {
        if (value is not null) throw new TestFailure($"{message}: expected null but was <{value}>");
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new TestFailure($"{message}: expected to contain <{needle}>");
    }

    public static void NotContains(string haystack, string needle, string message)
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new TestFailure($"{message}: must NOT contain <{needle}>");
    }

    public static async Task<ClientOperationException> ThrowsClientAsync(Func<Task> action, string? expectedCode, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (ClientOperationException ex)
        {
            if (expectedCode is not null) Equal(expectedCode, ex.Code, message + " [code]");
            return ex;
        }
        catch (Exception ex)
        {
            throw new TestFailure($"{message}: expected ClientOperationException but got {ex.GetType().Name}: {ex.Message}");
        }
        throw new TestFailure($"{message}: expected ClientOperationException but no exception was thrown");
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProbeDesktopTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}

public sealed class TestResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Passed { get; set; }
    public string? Failure { get; set; }
    public long ElapsedMs { get; set; }
}

public static class TestRunner
{
    public static async Task<int> RunAllAsync(IReadOnlyList<(string Id, string Name, Func<Task> Body)> tests)
    {
        int exitCode = 1;
        try
        {
            var results = new List<TestResult>();
            foreach (var (id, name, body) in tests)
            {
                var result = new TestResult { Id = id, Name = name };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Hard per-test guard so a hanging body cannot wedge the whole run.
                    Task bodyTask = body();
                    Task completed = await Task.WhenAny(bodyTask, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
                    if (completed != bodyTask)
                        throw new TestFailure("test exceeded 60s watchdog and was abandoned");
                    await bodyTask.ConfigureAwait(false);
                    result.Passed = true;
                }
                catch (TestFailure ex)
                {
                    result.Passed = false;
                    result.Failure = ex.Message;
                }
                catch (Exception ex)
                {
                    result.Passed = false;
                    result.Failure = $"{ex.GetType().Name}: {ex.Message}";
                }
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                results.Add(result);
                Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {id} {name} ({result.ElapsedMs} ms)");
                if (!result.Passed) Console.WriteLine($"     -> {result.Failure}");
                Console.Out.Flush();
            }

            int passed = results.Count(r => r.Passed);
            int failed = results.Count - passed;
            Console.WriteLine();
            Console.WriteLine($"TOTAL {results.Count}  PASSED {passed}  FAILED {failed}");
            if (failed > 0)
            {
                Console.WriteLine("FAILED TESTS:");
                foreach (var r in results.Where(r => !r.Passed))
                    Console.WriteLine($"  {r.Id} {r.Name} :: {r.Failure}");
            }
            Console.Out.Flush();
            exitCode = failed == 0 && results.Count == tests.Count ? 0 : 1;
        }
        catch (Exception fatal)
        {
            Console.WriteLine("RUNNER FATAL " + fatal);
            Console.Out.Flush();
            exitCode = 2;
        }
        return exitCode;
    }
}
