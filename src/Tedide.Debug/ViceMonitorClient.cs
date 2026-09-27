using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Tedide.Debug;

/// <summary>
/// A client for VICE's binary monitor protocol (enabled by launching VICE with -binarymonitor - see
/// <see cref="Tedide.Build.ViceEmulator"/>), used for source-level debugging: setting/clearing
/// breakpoints, reading/writing registers and memory, and stepping/continuing execution. Wire-format
/// encode/decode lives in <see cref="ViceMonitorProtocol"/>, kept separate from this class so it's
/// testable as pure byte-array round-trips without a socket.
///
/// Connects over a single persistent TCP connection; every request gets a fresh request id and its
/// own awaited reply via a background read loop that also raises <see cref="CheckpointHit"/>/
/// <see cref="Stopped"/>/<see cref="Resumed"/> for VICE's own unsolicited events (e.g. the user
/// pausing/resuming from VICE's own UI, not just from Tedide) - see
/// <see cref="ViceMonitorProtocol"/>'s doc comment for how solicited vs. unsolicited responses are
/// told apart on the wire.
/// </summary>
public sealed class ViceMonitorClient : IAsyncDisposable
{
    private readonly TcpClient _tcpClient = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(byte ResponseType, byte ErrorCode, byte[] Body)>> _pending = new();
    private NetworkStream? _stream;
    private Task? _readLoop;
    private int _nextRequestId;
    /// <summary>Set (once) by the read loop when the connection goes away - see <see cref="SendAsync"/>
    /// for why a request sent after this point has to check it rather than just waiting.</summary>
    private volatile IOException? _closedException;
    /// <summary>Armed by <see cref="RunUntilStoppedAsync"/> just before it sends a command that
    /// resumes the CPU, and completed with the PC from VICE's next Stopped event.</summary>
    private TaskCompletionSource<ushort>? _pendingStop;
    /// <summary>Fetched once per connection - see <see cref="GetRegistersAsync"/>.</summary>
    private IReadOnlyList<RegisterDescriptor>? _registerDescriptors;

    /// <summary>Raised when a checkpoint stops execution - the same event fires whether the
    /// checkpoint was set by this client or already existed in VICE.</summary>
    public event Action<CheckpointHitEventArgs>? CheckpointHit;

    /// <summary>Raised when VICE's monitor takes control (any reason, not just a checkpoint - e.g. a JAM or the user opening VICE's own monitor UI), with the PC it stopped at.</summary>
    public event Action<ushort>? Stopped;

    /// <summary>Raised when VICE's monitor resumes execution, with the PC it resumed from.</summary>
    public event Action<ushort>? Resumed;

    /// <summary>Raised (on the read-loop thread) when a <see cref="CheckpointHit"/>/<see cref="Stopped"/>/
    /// <see cref="Resumed"/> subscriber throws. The exception is contained rather than propagated -
    /// letting it escape into the read loop would tear down the whole connection and fail every
    /// pending request over what's only a bug in one subscriber - so this is how it gets reported.</summary>
    public event Action<Exception>? EventHandlerFailed;

    public async Task ConnectAsync(string host = "127.0.0.1", int port = 6502, CancellationToken cancellationToken = default)
    {
        await _tcpClient.ConnectAsync(host, port, cancellationToken);
        _stream = _tcpClient.GetStream();
        _readLoop = Task.Run(() => ReadLoopAsync(_stream), CancellationToken.None);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var (_, errorCode, _) = await SendAsync(ViceMonitorCommand.Ping, ReadOnlyMemory<byte>.Empty, cancellationToken, throwOnError: false);
        return errorCode == 0;
    }

    /// <summary>Sets a breakpoint/watchpoint over <paramref name="startAddress"/>..<paramref name="endAddress"/> (a single address if equal), returning VICE's own checkpoint number for later deletion.</summary>
    public async Task<CheckpointInfo> SetCheckpointAsync(
        ushort startAddress, ushort? endAddress = null, bool stopWhenHit = true, bool enabled = true,
        ViceCheckpointOperation operation = ViceCheckpointOperation.Exec, bool temporary = false,
        CancellationToken cancellationToken = default)
    {
        var body = ViceMonitorProtocol.EncodeCheckpointSetBody(startAddress, endAddress ?? startAddress, stopWhenHit, enabled, operation, temporary);
        var (_, _, responseBody) = await SendAsync(ViceMonitorCommand.CheckpointSet, body, cancellationToken);
        return ViceMonitorProtocol.DecodeCheckpointInfoBody(responseBody);
    }

    public async Task DeleteCheckpointAsync(uint checkpointNumber, CancellationToken cancellationToken = default)
    {
        var body = ViceMonitorProtocol.EncodeCheckpointDeleteBody(checkpointNumber);
        await SendAsync(ViceMonitorCommand.CheckpointDelete, body, cancellationToken);
    }

    public async Task<IReadOnlyList<RegisterDescriptor>> GetAvailableRegistersAsync(CancellationToken cancellationToken = default)
    {
        var body = ViceMonitorProtocol.EncodeRegistersAvailableBody();
        var (_, _, responseBody) = await SendAsync(ViceMonitorCommand.RegistersAvailable, body, cancellationToken);
        return ViceMonitorProtocol.DecodeRegistersAvailableBody(responseBody);
    }

    /// <summary>Reads every register's current value, resolved to names via <see cref="GetAvailableRegistersAsync"/>.
    /// VICE assigns register ids dynamically per session, so that mapping is fetched on the first
    /// call and reused for the rest of this connection rather than re-requested every time.</summary>
    public async Task<RegisterSnapshot> GetRegistersAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = _registerDescriptors ??= await GetAvailableRegistersAsync(cancellationToken);
        var body = ViceMonitorProtocol.EncodeRegistersGetBody();
        var (_, _, responseBody) = await SendAsync(ViceMonitorCommand.RegistersGet, body, cancellationToken);
        var valuesById = ViceMonitorProtocol.DecodeRegistersGetBody(responseBody);

        var valuesByName = new Dictionary<string, ushort>();
        foreach (var descriptor in descriptors)
        {
            if (valuesById.TryGetValue(descriptor.Id, out var value))
                valuesByName[descriptor.Name] = value;
        }
        return new RegisterSnapshot(valuesByName);
    }

    public async Task<byte[]> GetMemoryAsync(ushort startAddress, ushort endAddress, CancellationToken cancellationToken = default)
    {
        var body = ViceMonitorProtocol.EncodeMemoryGetBody(startAddress, endAddress);
        var (_, _, responseBody) = await SendAsync(ViceMonitorCommand.MemoryGet, body, cancellationToken);
        return ViceMonitorProtocol.DecodeMemoryGetBody(responseBody);
    }

    public async Task SetMemoryAsync(ushort startAddress, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var endAddress = (ushort)(startAddress + data.Length - 1);
        var body = ViceMonitorProtocol.EncodeMemorySetBody(startAddress, endAddress, data.Span);
        await SendAsync(ViceMonitorCommand.MemorySet, body, cancellationToken);
    }

    /// <summary>Executes one instruction (or a whole subroutine call as one, if
    /// <paramref name="stepOverSubroutines"/> is true), and returns the PC once VICE has stopped again.</summary>
    public Task<ushort> StepAsync(bool stepOverSubroutines = false, CancellationToken cancellationToken = default) =>
        RunUntilStoppedAsync(
            ViceMonitorCommand.AdvanceInstructions,
            ViceMonitorProtocol.EncodeAdvanceInstructionsBody(instructionCount: 1, stepOverSubroutines),
            cancellationToken);

    /// <summary>Runs until just after the next RTS/RTI executes, and returns the PC it stopped at.
    /// "Next" isn't nesting-aware: from inside a routine that itself calls others, the first
    /// nested call's RTS is where this stops, so a caller wanting the routine's own return must
    /// repeat it until the PC lands somewhere it recognizes.</summary>
    public Task<ushort> ExecuteUntilReturnAsync(CancellationToken cancellationToken = default) =>
        RunUntilStoppedAsync(ViceMonitorCommand.ExecuteUntilReturn, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    /// Sends a command that resumes the CPU and waits for VICE to stop again, returning the PC
    /// from its Stopped event. Waiting for the command's own reply isn't enough: verified against
    /// a live VICE 3.9, the reply to Advance Instructions/Execute Until Return arrives immediately,
    /// *before* the CPU has run - the Stopped event follows up to tens of milliseconds later (a
    /// step over a JSR). Reading registers on the reply alone would catch VICE mid-run, and any
    /// command sent then halts it wherever it happens to be.
    /// </summary>
    private async Task<ushort> RunUntilStoppedAsync(ViceMonitorCommand command, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        // Armed before sending, so a Stopped event that arrives right after the reply can't be missed.
        var stop = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingStop, stop);
        if (_closedException is { } closed)
            stop.TrySetException(new IOException(closed.Message, closed));

        try
        {
            await SendAsync(command, body, cancellationToken);
            return await stop.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingStop, null, stop);
        }
    }

    /// <summary>Resumes emulation until the next checkpoint (or the user pausing it again) - VICE's binary monitor protocol calls this "exit monitor" (0xaa), not a separate "continue" command.</summary>
    public async Task ContinueAsync(CancellationToken cancellationToken = default) =>
        await SendAsync(ViceMonitorCommand.ExitMonitor, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    /// Sends one request and awaits its reply. Throws <see cref="ViceMonitorException"/> if VICE
    /// answers with a nonzero error code (unless <paramref name="throwOnError"/> is false, for
    /// callers like <see cref="PingAsync"/> that report the error code themselves), or
    /// <see cref="IOException"/> if the connection is or goes away before a reply arrives.
    /// </summary>
    private async Task<(byte ResponseType, byte ErrorCode, byte[] Body)> SendAsync(
        ViceMonitorCommand command, ReadOnlyMemory<byte> body, CancellationToken cancellationToken, bool throwOnError = true)
    {
        if (_stream is null)
            throw new InvalidOperationException("Not connected - call ConnectAsync first.");

        var requestId = (uint)Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<(byte, byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            // Checked *after* registering in _pending: the read loop sets _closedException before
            // failing everything in _pending, so a request registered too late for that sweep is
            // guaranteed to see the flag here instead of waiting forever for a reply that can't come.
            if (_closedException is { } closed)
                throw new IOException(closed.Message, closed);

            var request = ViceMonitorProtocol.EncodeRequest(requestId, command, body.Span);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _stream.WriteAsync(request, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            (byte ResponseType, byte ErrorCode, byte[] Body) response;
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                response = await completion.Task;
            }

            if (throwOnError && response.ErrorCode != 0)
                throw new ViceMonitorException(command, response.ErrorCode);
            return response;
        }
        finally
        {
            // A no-op after a normal reply (Dispatch already removed it), but a cancelled or failed
            // request would otherwise leave its entry in _pending for the rest of the session.
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        var headerBuffer = new byte[ViceMonitorProtocol.ResponseHeaderLength];
        try
        {
            while (true)
            {
                await ReadExactlyAsync(stream, headerBuffer);
                var header = ViceMonitorProtocol.DecodeResponseHeader(headerBuffer);

                var bodyBuffer = header.BodyLength == 0 ? [] : new byte[header.BodyLength];
                if (header.BodyLength > 0)
                    await ReadExactlyAsync(stream, bodyBuffer);

                Dispatch(header, bodyBuffer);
            }
        }
        catch (Exception ex)
        {
            // Connection closed (VICE exited, or DisposeAsync tore it down) - every still-pending
            // request would otherwise hang forever, so fail them all instead of leaving them stuck.
            // The flag is set first so a request that registers after this sweep fails too - see SendAsync.
            var closed = new IOException("VICE monitor connection closed.", ex);
            _closedException = closed;
            foreach (var pending in _pending.Values)
                pending.TrySetException(closed);
            Volatile.Read(ref _pendingStop)?.TrySetException(closed);
        }
    }

    private void Dispatch(ViceMonitorProtocol.ResponseHeader header, byte[] body)
    {
        if (header.RequestId != ViceMonitorProtocol.EventRequestId)
        {
            if (_pending.TryRemove(header.RequestId, out var completion))
                completion.TrySetResult((header.ResponseType, header.ErrorCode, body));
            return;
        }

        switch ((ViceMonitorEventType)header.ResponseType)
        {
            case ViceMonitorEventType.Stopped:
                var pc = ViceMonitorProtocol.DecodeProgramCounterBody(body);
                Volatile.Read(ref _pendingStop)?.TrySetResult(pc);
                RaiseSafely(Stopped, pc);
                break;
            case ViceMonitorEventType.Resumed:
                RaiseSafely(Resumed, ViceMonitorProtocol.DecodeProgramCounterBody(body));
                break;
            default:
                // A checkpoint hit is reported by resending the same "Checkpoint info" (0x11) shape
                // used for SetCheckpointAsync's own reply, just unsolicited this time - see
                // ViceMonitorProtocol's doc comment.
                if (header.ResponseType == (byte)ViceMonitorCommand.CheckpointGet)
                    RaiseSafely(CheckpointHit, new CheckpointHitEventArgs(ViceMonitorProtocol.DecodeCheckpointInfoBody(body)));
                break;
        }
    }

    /// <summary>Invokes each subscriber separately, containing any exception one throws (reported
    /// via <see cref="EventHandlerFailed"/>) so it can neither skip the remaining subscribers nor
    /// escape into <see cref="ReadLoopAsync"/>, where it would close the connection.</summary>
    private void RaiseSafely<T>(Action<T>? handlers, T args)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(args);
            }
            catch (Exception ex)
            {
                try { EventHandlerFailed?.Invoke(ex); }
                catch { /* A failing error reporter mustn't take the connection down either. */ }
            }
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..]);
            if (n == 0)
                throw new IOException("VICE monitor connection closed while reading a response.");
            read += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_readLoop is not null)
        {
            _stream?.Close();
            try { await _readLoop; } catch { /* already handled inside ReadLoopAsync */ }
        }
        _tcpClient.Dispose();
        _writeLock.Dispose();
    }
}
