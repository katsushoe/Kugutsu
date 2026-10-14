namespace Kugutsu.McpProxy;

/// <summary>Bounds each incoming UTF-8 stdio frame before the SDK allocates its JSON line.</summary>
internal sealed class BoundedLineStream(Stream inner, int limit, Action? onEnd = null) : Stream
{
    private int _lineBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Reads bounded frames without logging or retaining their contents.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);
        if (count == 0 && !buffer.IsEmpty) { onEnd?.Invoke(); }
        Inspect(buffer.Span[..count]);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') { _lineBytes = 0; }
            else if (++_lineBytes > limit) { throw new InvalidDataException("MCP input frame exceeds the limit."); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); }
        base.Dispose(disposing);
    }
}
