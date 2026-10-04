using System.Net;
using System.Net.Sockets;
using Tedide.Debug;

namespace Tedide.Tests.Shared;

/// <summary>
/// A stand-in for VICE's binary monitor: a real <see cref="TcpListener"/> on an ephemeral port that
/// answers each request through a handler the test supplies, and can send unsolicited events.
/// Shared by Tedide.Debug.Tests (the client itself) and Tedide.App.Tests (a whole debug session).
/// </summary>
internal sealed class FakeViceMonitorServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient? _accepted;
    private NetworkStream? _stream;
    private Func<uint, ViceMonitorCommand, byte[], IReadOnlyList<byte[]>>? _handler;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _clientConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Test hook: widens the window between the accept completing and the server
    /// recording the connection - the race behind an intermittent 5-second hang.</summary>
    public TimeSpan DelayAfterAccept { get; init; }

    public async Task StartAsync()
    {
        _listener.Start();
        _ = AcceptAndServeAsync();
        await Task.CompletedTask;
    }

    public void OnRequest(Func<uint, ViceMonitorCommand, byte[], byte[]> handler) =>
        _handler = (requestId, command, body) => [handler(requestId, command, body)];

    /// <summary>A handler whose answer is several frames, written in order - a reply, then the
    /// events VICE sends after it (Stopped after a step, say).</summary>
    public void OnRequestFrames(Func<uint, ViceMonitorCommand, byte[], IReadOnlyList<byte[]>> handler) => _handler = handler;

    public async Task SendUnsolicitedAsync(byte responseType, byte[] body)
    {
        while (_stream is null)
            await Task.Delay(10);
        var frame = ViceMonitorProtocol.EncodeResponse(ViceMonitorProtocol.EventRequestId, responseType, 0, body);
        await _stream.WriteAsync(frame);
    }

    private async Task AcceptAndServeAsync()
    {
        TcpClient accepted;
        try
        {
            accepted = await _listener.AcceptTcpClientAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            return; // Disposed before anyone connected.
        }
        if (DelayAfterAccept > TimeSpan.Zero)
            await Task.Delay(DelayAfterAccept);

        lock (_gate)
        {
            // Disposed while the accept was completing: close this late connection too, or the
            // client never sees the server go away and its next request hangs.
            if (_disposed)
            {
                accepted.Dispose();
                return;
            }
            _accepted = accepted;
            _stream = _accepted.GetStream();
        }
        _clientConnected.TrySetResult();

        var headerBuffer = new byte[ViceMonitorProtocol.RequestHeaderLength];
        while (true)
        {
            if (!await ReadExactlyOrFalseAsync(_stream, headerBuffer))
                return;

            var requestHeader = ViceMonitorProtocol.DecodeRequestHeader(headerBuffer);
            var body = new byte[requestHeader.BodyLength];
            if (requestHeader.BodyLength > 0 && !await ReadExactlyOrFalseAsync(_stream, body))
                return;

            if (_handler is { } handler)
            {
                foreach (var frame in handler(requestHeader.RequestId, requestHeader.Command, body))
                    await _stream.WriteAsync(frame);
            }
        }
    }

    private static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>Completes once the client's connection has been accepted - wait for this rather
    /// than a fixed delay before tearing the server down.</summary>
    public Task WaitForClientAsync() => _clientConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stream?.Dispose();
            _accepted?.Dispose();
        }
        _listener.Stop();
    }
}
