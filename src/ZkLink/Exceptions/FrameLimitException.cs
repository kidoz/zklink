namespace ZkLink;

// An inbound frame declared a length above MaxFrameBytes before any payload was read.
// It derives from IOException so each reader treats it as transport loss rather than
// as a terminal protocol fault; it never escapes to application code.
internal sealed class FrameLimitException : IOException
{
    internal FrameLimitException(int declaredLength, int limit)
        : base(
            FormattableString.Invariant(
                $"Inbound frame length {declaredLength} exceeds the configured limit {limit}."
            )
        )
    {
        DeclaredLength = declaredLength;
    }

    internal int DeclaredLength { get; }
}
