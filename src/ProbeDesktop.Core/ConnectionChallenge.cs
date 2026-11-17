using System.Security.Cryptography;
using System.Text;

namespace ProbeDesktop.Core;

/// <summary>A small synthetic reading check, not an identity or long-context benchmark.</summary>
internal sealed record ConnectionChallenge(string Prompt, string Expected)
{
    internal const int Version = 1;
    internal const int OutputTokens = 64;

    internal static ConnectionChallenge Create()
    {
        string first = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        string middle = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        string last = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        // Plain English keeps the request near 256 input tokens across common tokenizers.
        // Protocol wrappers and model-specific tokenization are not included in this estimate.
        string prompt = $"""
            Read this small test note. Find the values of FIRST, MIDDLE, and LAST. Reply only with those three values in that order, separated by a vertical bar. Do not add spaces, quotes, labels, or an explanation. Copy the values exactly, including letter case.
            FIRST={first}
            A team keeps a quiet room for reading. Each morning they open the door and put a clean sheet on the table. The room has a window, a lamp, and a green chair. These details are background text, not values to return. No tool or outside knowledge is needed. Keep reading until the end of the note before you answer.
            MIDDLE={middle}
            Later the team checks the room again. The lamp is off and the sheet is still on the table. A blue book sits near the window. Nothing in this story changes the task. Use only the three named values, not words from the story. This is a simple request to read and copy, not to judge the story or describe the room.
            Before replying, check the first value against the start of the note and the second value against its middle. Keep the original order. Do not replace any value with a summary or a guessed answer.
            LAST={last}
            Return the three values now.
            """;
        return new(prompt, $"{first}|{middle}|{last}");
    }
}

/// <summary>Ephemeral bounded text; only the check status is persisted, never the answer.</summary>
internal sealed class ConnectionTextCapture
{
    private const int MaximumChars = 2048;
    private readonly StringBuilder _text = new();
    private bool _overflow;
    private bool _ambiguous;
    private string? _source;
    private int? _lastContentIndex;

    internal void Invalidate() => _ambiguous = true;

    internal void ObserveSource(string source, int? contentIndex = null)
    {
        if (_source is not null && _source != source) Invalidate();
        _source ??= source;
        // Ordered blocks in one message may be joined, never interleaved/reversed blocks.
        if (_lastContentIndex is int previous && contentIndex is int next && next < previous) Invalidate();
        if (contentIndex is not null) _lastContentIndex = contentIndex;
    }

    internal void Append(string? text)
    {
        if (text is null || _overflow) return;
        if (_text.Length + text.Length > MaximumChars) { _overflow = true; _text.Clear(); return; }
        _text.Append(text);
    }

    internal void Final(string? text)
    {
        // An explicit final snapshot supersedes deltas, even when empty. Safety limits
        // and ambiguous output identity remain sticky for the entire response.
        _text.Clear();
        Append(text);
    }

    internal ConnectionContextCheck Check(ConnectionTestStage stage, string expected) =>
        stage != ConnectionTestStage.ModelResponded || _overflow || _ambiguous ? ConnectionContextCheck.Inconclusive :
        string.Equals(_text.ToString().Trim(), expected, StringComparison.Ordinal) ? ConnectionContextCheck.Passed :
        ConnectionContextCheck.Mismatch;
}
