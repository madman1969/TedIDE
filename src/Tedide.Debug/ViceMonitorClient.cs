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

    /// <summary>Raised when a checkpoint stops execution - the same event fires whether the
    /// checkpoint was set by this client or already existed in VICE.</summary>
    public event Action<CheckpointHitEventArgs>? CheckpointHit;

    /// <summary>Raised when VICE's monitor takes control (any reason, not just a checkpoint - e.g. a JAM or the user opening VICE's own monitor UI), with the PC it stopped at.</summary>
    public event Action<ushort>? Stopped;

    /// <summary>Raised when VICE's monitor resumes execution, with the PC it resumed from.</summary>
    public event Action<ushort>? Resumed;

    public async Task ConnectAsync(string host = "127.0.0.1", int port = 6502, CancellationToken cancellationToken = default)
    {
        await _tcpClient.ConnectAsync(host, port, cancellationToken);
        _stream = _tcpClient.GetStream();
        _readLoop = Task.Run(() => ReadLoopAsync(_stream), CancellationToken.None);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var (_, errorCode, _) = await SendAsync(ViceMonitorCommand.Ping, ReadOnlyMemory<byte>.Empty, cancellationToken);
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

    /// <summary>Reads every register's current value, resolved to names via <see cref="GetAvailableRegistersAsync"/> (VICE assigns register ids dynamically per session, so this is fetched fresh each call rather than cached).</summary>
    public async Task<RegisterSnapshot> GetRegistersAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await GetAvailableRegistersAsync(cancellationToken);
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

    /// <summary>Executes one instruction (or steps over the next subroutine call, if <paramref name="stepOverSubroutines"/> is true) and stops again.</summary>
    public async Task StepAsync(bool stepOverSubroutines = false, CancellationToken cancellationToken = default)
    {
        var body = ViceMonitorProtocol.EncodeAdvanceInstructionsBody(instructionCount: 1, stepOverSubroutines);
        await SendAsync(ViceMonitorCommand.AdvanceInstructions, body, cancellationToken);
    }

    /// <summary>Resumes emulation until the next checkpoint (or the user pausing it again) - VICE's binary monitor protocol calls this "exit monitor" (0xaa), not a separate "continue" command.</summary>
    public async Task ContinueAsync(CancellationToken cancellationToken = default) =>
        await SendAsync(ViceMonitorCommand.ExitMonitor, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private async Task<(byte ResponseType, byte ErrorCode, byte[] Body)> SendAsync(ViceMonitorCommand command, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("Not connected - call ConnectAsync first.");

        var requestId = (uint)Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<(byte, byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

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

        await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            return await completion.Task;
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
        catch (Exception)
        {
            // Connection closed (VICE exited, or DisposeAsync tore it down) - every still-pending
            // request would otherwise hang forever, so fail them all instead of leaving them stuck.
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("VICE monitor connection closed."));
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
                Stopped?.Invoke(ViceMonitorProtocol.DecodeProgramCounterBody(body));
                break;
            case ViceMonitorEventType.Resumed:
                Resumed?.Invoke(ViceMonitorProtocol.DecodeProgramCounterBody(body));
                break;
            default:
                // A checkpoint hit is reported by resending the same "Checkpoint info" (0x11) shape
                // used for SetCheckpointAsync's own reply, just unsolicited this time - see
                // ViceMonitorProtocol's doc comment.
                if (header.ResponseType == (byte)ViceMonitorCommand.CheckpointGet)
                    CheckpointHit?.Invoke(new CheckpointHitEventArgs(ViceMonitorProtocol.DecodeCheckpointInfoBody(body)));
                break;
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
