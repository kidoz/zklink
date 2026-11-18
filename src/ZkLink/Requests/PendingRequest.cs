using ZkLink.Protocol;

namespace ZkLink.Requests;

internal enum RequestPhase
{
    Preparing,
    Queued,
    Writing,
    AwaitingReply,
    CompletingPreparation,
    CompletingWrite,
    Completed,
    Released,
}

internal sealed class PendingRequest
{
    internal readonly TaskCompletionSource<WireReply> Completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    internal PendingRequest(
        RequestScheduler owner,
        ZooKeeperRequestInfo info,
        int bytes,
        long generation,
        long started,
        CancellationToken token,
        bool control = false
    )
    {
        Owner = owner;
        Info = info;
        ReservedBytes = bytes;
        Generation = generation;
        Started = started;
        Token = token;
        Control = control;
        Task = AwaitAsync(Completion.Task);
    }

    private static async Task<WireReply> AwaitAsync(Task<WireReply> task) =>
        await task.ConfigureAwait(false);

    internal Task<WireReply> Task { get; }
    internal readonly RequestScheduler Owner;
    internal ZooKeeperRequestInfo Info { get; private set; }
    internal readonly int ReservedBytes;
    internal readonly long Generation;
    internal readonly long Started;
    internal readonly CancellationToken Token;
    internal readonly bool Control;
    internal LinkedListNode<PendingRequest>? Node;
    internal byte[]? Packet;
    internal WireRequest? Shape;
    internal IRequestObserver? Observer;
    internal RequestPhase Phase { get; private set; } = RequestPhase.Preparing;
    internal bool Sent => Info.RequestId.HasValue;
    internal bool Completed =>
        Phase
            is RequestPhase.CompletingPreparation
                or RequestPhase.CompletingWrite
                or RequestPhase.Completed
                or RequestPhase.Released;

    // A caller may leave while the writer still owns the buffer. The ordered reply
    // must still be consumed even after the caller has received its outcome.
    internal bool Tombstone { get; private set; }

    internal void MarkTombstone()
    {
        if (Phase is not (RequestPhase.Writing or RequestPhase.AwaitingReply))
        {
            throw new InvalidOperationException(
                "Only an outstanding transmission can become a tombstone."
            );
        }
        Tombstone = true;
    }

    internal void ConsumeTombstone() => Tombstone = false;

    internal void FinishPreparation() =>
        TransitionTo(
            Phase == RequestPhase.CompletingPreparation
                ? RequestPhase.Completed
                : RequestPhase.Queued
        );

    internal void BeginWrite(ZooKeeperRequestInfo assigned)
    {
        if (assigned.RequestId is null)
        {
            throw new InvalidOperationException("Writing requires an assigned request ID.");
        }
        Info = assigned;
        TransitionTo(RequestPhase.Writing);
    }

    internal void FinishWrite()
    {
        if (Phase is RequestPhase.Writing or RequestPhase.CompletingWrite)
        {
            TransitionTo(
                Phase == RequestPhase.Writing ? RequestPhase.AwaitingReply : RequestPhase.Completed
            );
        }
    }

    internal void Complete()
    {
        if (!Completed)
        {
            TransitionTo(
                Phase switch
                {
                    RequestPhase.Preparing => RequestPhase.CompletingPreparation,
                    RequestPhase.Writing => RequestPhase.CompletingWrite,
                    _ => RequestPhase.Completed,
                }
            );
        }
    }

    internal bool TryRelease()
    {
        if (Phase != RequestPhase.Completed)
        {
            return false;
        }
        TransitionTo(RequestPhase.Released);
        return true;
    }

    private void TransitionTo(RequestPhase next)
    {
        if (!IsAllowed(Phase, next))
        {
            throw new InvalidOperationException(
                $"Invalid request transition from {Phase} to {next}."
            );
        }
        Phase = next;
    }

    internal static bool IsAllowed(RequestPhase from, RequestPhase to) =>
        from switch
        {
            RequestPhase.Preparing => to
                is RequestPhase.Queued
                    or RequestPhase.CompletingPreparation,
            RequestPhase.Queued => to is RequestPhase.Writing or RequestPhase.Completed,
            RequestPhase.Writing => to
                is RequestPhase.AwaitingReply
                    or RequestPhase.CompletingWrite,
            RequestPhase.AwaitingReply
            or RequestPhase.CompletingPreparation
            or RequestPhase.CompletingWrite => to == RequestPhase.Completed,
            RequestPhase.Completed => to == RequestPhase.Released,
            _ => false,
        };

    internal CancellationTokenRegistration Cancellation;
    internal ITimer? Timer;
    internal RequestOutcome Outcome => Sent ? RequestOutcome.Unknown : RequestOutcome.NotSent;
}
