namespace Cuelify.Infrastructure.Storage;

// 只观察成功读写的字节数；底层流的生命周期仍由调用方管理。
public sealed class ProgressStream(Stream inner, Action<long> report) : Stream
{
    private long _bytes;
    private long _reportedAt;
    private void Advance(int count)
    {
        _bytes += count;
        var now = Environment.TickCount64;
        if (count == 0 || now - _reportedAt >= 200) { report(_bytes); _reportedAt = now; }
    }
    public void ReportCompleted() => report(_bytes);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Advance(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { var read = await inner.ReadAsync(buffer, token); Advance(read); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Advance(count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { await inner.WriteAsync(buffer, token); Advance(buffer.Length); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
}
