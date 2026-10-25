namespace ZkLink.Protocol.Tests.Peers;

// A bounded duplex peer: caller-controlled chunks, EOF, delayed delivery and partial write failure.
internal sealed class ScriptedPeerStream : Stream
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly Queue<byte[]> incoming = new();
    private readonly MemoryStream outgoing = new();
    private readonly object gate = new();
    private TaskCompletionSource ready = NewSignal();
    private int offset;
    private int bufferedBytes;
    private bool completed;
    private bool disposed;

    internal int? FailWriteAfterBytes { get; init; }
    internal byte[] WrittenBytes => outgoing.ToArray();
    internal int PendingBytes
    {
        get
        {
            lock (gate)
                return bufferedBytes;
        }
    }

    internal void Enqueue(params byte[][] chunks)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed)
                throw new InvalidOperationException("Peer input is complete.");
            int additionalBytes = 0;
            foreach (byte[] chunk in chunks)
            {
                if (chunk.Length > MaxBytes - bufferedBytes - additionalBytes)
                    throw new InvalidOperationException("Peer input budget exceeded.");
                additionalBytes += chunk.Length;
            }
            foreach (byte[] chunk in chunks)
            {
                if (chunk.Length > MaxBytes - bufferedBytes)
                    throw new InvalidOperationException("Peer input budget exceeded.");
                if (chunk.Length == 0)
                    continue;
                incoming.Enqueue((byte[])chunk.Clone());
                bufferedBytes += chunk.Length;
            }
            if (incoming.Count > 0)
                ready.TrySetResult();
        }
    }

    internal void CompleteInput()
    {
        lock (gate)
        {
            completed = true;
            ready.TrySetResult();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.IsEmpty)
            return 0;
        while (true)
        {
            Task wait;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (incoming.TryPeek(out byte[]? chunk))
                {
                    int count = Math.Min(buffer.Length, chunk.Length - offset);
                    chunk.AsMemory(offset, count).CopyTo(buffer);
                    offset += count;
                    bufferedBytes -= count;
                    if (offset == chunk.Length)
                    {
                        incoming.Dequeue();
                        offset = 0;
                    }
                    if (incoming.Count == 0 && !completed)
                        ready = NewSignal();
                    return count;
                }
                if (completed)
                    return 0;
                wait = ready.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        int count = buffer.Length;
        if (FailWriteAfterBytes is { } limit)
            count = (int)Math.Min(count, Math.Max(0, limit - outgoing.Length));
        if (count > MaxBytes - outgoing.Length)
            throw new IOException("Peer output budget exceeded.");
        outgoing.Write(buffer.Span[..count]);
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
            lock (gate)
            {
                disposed = true;
                incoming.Clear();
                bufferedBytes = 0;
                ready.TrySetResult();
            }
            outgoing.Dispose();
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
