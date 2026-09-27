namespace Tedide.Debug;

/// <summary>
/// Thrown when VICE answers a binary monitor request with a nonzero error code - e.g. a checkpoint
/// set/delete it rejected. The reply body is typically empty in that case, so decoding it as a
/// normal success reply would otherwise fail with an unhelpful index-out-of-range instead.
/// </summary>
public sealed class ViceMonitorException : Exception
{
    public byte ErrorCode { get; }

    internal ViceMonitorException(ViceMonitorCommand command, byte errorCode)
        : base($"VICE rejected {command} (0x{(byte)command:X2}) with error code 0x{errorCode:X2}.")
    {
        ErrorCode = errorCode;
    }
}
