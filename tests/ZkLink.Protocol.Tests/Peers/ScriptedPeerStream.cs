namespace ZkLink.Protocol.Tests.Peers;

// A bounded duplex peer: caller-controlled chunks, EOF, delayed delivery and partial write failure.
internal sealed class ScriptedPeerStream : Stream
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly Queue<byte[]> _incoming = new();
    private readonly MemoryStream _outgoing = new();
    private readonly object _gate = new();
    private TaskCompletionSource _ready = NewSignal();
    private int _offset;
    private int _bufferedBytes;
    private bool _completed;
    private bool _disposed;

    internal int? FailWriteAfterBytes { get; init; }
    internal byte[] WrittenBytes => _outgoing.ToArray();
    internal int PendingBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    internal void Enqueue(params byte[][] chunks)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("Peer input is complete.");
            }
            var additionalBytes = 0;
            foreach (var chunk in chunks)
            {
                if (chunk.Length > MaxBytes - _bufferedBytes - additionalBytes)
                {
                    throw new InvalidOperationException("Peer input budget exceeded.");
                }
                additionalBytes += chunk.Length;
            }
            foreach (var chunk in chunks)
            {
                if (chunk.Length > MaxBytes - _bufferedBytes)
                {
                    throw new InvalidOperationException("Peer input budget exceeded.");
                }
                if (chunk.Length == 0)
                {
                    continue;
                }
                _incoming.Enqueue((byte[])chunk.Clone());
                _bufferedBytes += chunk.Length;
            }
            if (_incoming.Count > 0)
            {
                _ready.TrySetResult();
            }
        }
    }

    internal void CompleteInput()
    {
        lock (_gate)
        {
            _completed = true;
            _ready.TrySetResult();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_incoming.TryPeek(out var chunk))
                {
                    var count = Math.Min(buffer.Length, chunk.Length - _offset);
                    chunk.AsMemory(_offset, count).CopyTo(buffer);
                    _offset += count;
                    _bufferedBytes -= count;
                    if (_offset == chunk.Length)
                    {
                        _incoming.Dequeue();
                        _offset = 0;
                    }
                    if (_incoming.Count == 0 && !_completed)
                    {
                        _ready = NewSignal();
                    }
                    return count;
                }
                if (_completed)
                {
                    return 0;
                }
                wait = _ready.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var count = buffer.Length;
        if (FailWriteAfterBytes is { } limit)
        {
            count = (int)Math.Min(count, Math.Max(0, limit - _outgoing.Length));
        }
        if (count > MaxBytes - _outgoing.Length)
        {
            throw new IOException("Peer output budget exceeded.");
        }
        _outgoing.Write(buffer.Span[..count]);
        return count == buffer.Length
            ? ValueTask.CompletedTask
            : ValueTask.FromException(new IOException("Injected partial write failure."));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _incoming.Clear();
                _bufferedBytes = 0;
                _ready.TrySetResult();
            }
            _outgoing.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() =>
        throw new NotSupportedException("Synchronous I/O is forbidden in this peer.");

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous I/O is forbidden in this peer.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous I/O is forbidden in this peer.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
