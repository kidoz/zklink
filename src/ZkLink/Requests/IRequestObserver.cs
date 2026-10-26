using ZkLink.Protocol;

namespace ZkLink.Requests;

// Internal reader/admission hooks only, always under the shared session gate; never user callbacks.
internal interface IRequestObserver
{
    void Replied(PendingRequest pending, WireReply reply);
    void Canceled(PendingRequest pending);
    void Abandoned(PendingRequest pending);
}
