using System.Net;
using System.Text;

namespace ProbeDesktop.Tests;

public sealed class RecordedRequest
{
    public required string Method { get; init; }
    public required Uri Uri { get; init; }
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }
    public required string Body { get; init; }
}

/// <summary>
/// In-memory transport. Never opens a socket; every response is scripted by the test.
/// Records the exact method, absolute URI, request headers and request body text.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<RecordedRequest> Requests { get; } = new();
    public int Count => Requests.Count;

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    public FakeHttpHandler Enqueue(Func<HttpResponseMessage> responder) => Enqueue(_ => responder());

    public FakeHttpHandler EnqueueJson(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null)
        => Enqueue(() =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            configure?.Invoke(response);
            return response;
        });

    public FakeHttpHandler EnqueueStatus(HttpStatusCode status, Action<HttpResponseMessage>? configure = null)
        => Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8, "text/html") };
            configure?.Invoke(response);
            return response;
        });

    public FakeHttpHandler EnqueueRaw(HttpStatusCode status, string mediaType, string body)
        => Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    public FakeHttpHandler EnqueueException(Exception exception)
        => Enqueue(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = "";
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = header.Value.ToArray();
        if (request.Content is not null)
            foreach (var header in request.Content.Headers)
                headers[header.Key] = header.Value.ToArray();

        Requests.Add(new RecordedRequest
        {
            Method = request.Method.Method,
            Uri = request.RequestUri ?? throw new InvalidOperationException("request URI missing"),
            Headers = headers,
            Body = body
        });

        if (_responses.Count == 0)
            throw new InvalidOperationException($"FakeHttpHandler has no scripted response for {request.Method} {request.RequestUri}");

        return _responses.Dequeue()(request);
    }
}

/// <summary>Reversible protector whose Protect can be forced to fail to exercise atomic-save rollback.</summary>
public sealed class ToggleFailProtector : ProbeDesktop.Core.ISecretProtector
{
    private readonly ReversibleTestProtector _inner = new();
    public bool FailProtect { get; set; }
    public bool FailUnprotect { get; set; }

    public byte[] Protect(byte[] plaintext)
    {
        if (FailProtect) throw new System.Security.Cryptography.CryptographicException("simulated protect failure");
        return _inner.Protect(plaintext);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (FailUnprotect) throw new System.Security.Cryptography.CryptographicException("simulated unprotect failure");
        return _inner.Unprotect(ciphertext);
    }
}

/// <summary>
/// Stream that returns a small prefix, then blocks until cancelled. ReadAsync honours the token,
/// mirroring a real network body that aborts when the reader cancels.
/// </summary>
public sealed class SlowHonouringStream : Stream
{
    private int _position;
    private readonly byte[] _prefix = Encoding.UTF8.GetBytes("{\"status\":\"running\",");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _prefix.Length)
        {
            int n = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        // Never completes unless the reader cancels: this is the "server stalls after headers" case.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Content that streams without buffering, so the consumer's cancellation token reaches the stream.
/// </summary>
public sealed class SlowBodyContent : HttpContent
{
    public SlowBodyContent()
    {
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => throw new NotSupportedException("slow content is read via CreateContentReadStreamAsync");

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowHonouringStream());

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new SlowHonouringStream());
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>
/// Deterministic reversible stand-in for DPAPI. Not plaintext: output is transformed and prefixed,
/// so a file containing only the protected bytes cannot contain the raw key literal.
/// </summary>
public sealed class ReversibleTestProtector : ProbeDesktop.Core.ISecretProtector
{
    private static readonly byte[] Mask = Encoding.UTF8.GetBytes("probe-desktop-test-mask-v1");
    public const string Marker = "ENC1:";

    public byte[] Protect(byte[] plaintext)
    {
        var transformed = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
            transformed[i] = (byte)(plaintext[i] ^ Mask[i % Mask.Length] ^ (byte)(i * 31 + 7));
        var marker = Encoding.ASCII.GetBytes(Marker);
        var result = new byte[marker.Length + transformed.Length];
        marker.CopyTo(result, 0);
        transformed.CopyTo(result, marker.Length);
        return result;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        var marker = Encoding.ASCII.GetBytes(Marker);
        if (ciphertext.Length < marker.Length)
            throw new System.Security.Cryptography.CryptographicException("ciphertext too short");
        for (int i = 0; i < marker.Length; i++)
            if (ciphertext[i] != marker[i])
                throw new System.Security.Cryptography.CryptographicException("ciphertext marker mismatch");
        int len = ciphertext.Length - marker.Length;
        var result = new byte[len];
        for (int i = 0; i < len; i++)
            result[i] = (byte)(ciphertext[marker.Length + i] ^ Mask[i % Mask.Length] ^ (byte)(i * 31 + 7));
        return result;
    }
}
