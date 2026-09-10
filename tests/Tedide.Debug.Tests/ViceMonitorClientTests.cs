using System.Net;
using System.Net.Sockets;

namespace Tedide.Debug.Tests;

/// <summary>
/// Exercises <see cref="ViceMonitorClient"/>'s request/response correlation and unsolicited-event
/// dispatch against a minimal fake TCP server (a real <see cref="TcpListener"/> on an ephemeral
/// port, playing back canned bytes) rather than a real VICE instance - the first precedent for this
/// kind of test double in the repo (see Tedide.Debug's own project comment); kept intentionally
/// small rather than a general mocking framework.
/// </summary>
public class ViceMonitorClientTests
{
    [Fact]
    public async Task PingAsync_ReturnsTrue_WhenTheServerRepliesWithNoError()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
        {
            Assert.Equal(ViceMonitorCommand.Ping, command);
            return ViceMonitorProtocol.EncodeResponse(requestId, (byte)ViceMonitorCommand.Ping, errorCode: 0, body: []);
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var result = await client.PingAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task SetCheckpointAsync_DecodesTheServersCheckpointInfoReply()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
        {
            Assert.Equal(ViceMonitorCommand.CheckpointSet, command);
            var info = new CheckpointInfo(3, false, 0x0840, 0x0840, true, true, ViceCheckpointOperation.Exec, false, 0, 0, false, 0);
            var infoBody = ViceMonitorProtocol.EncodeCheckpointInfoBody(info);
            return ViceMonitorProtocol.EncodeResponse(requestId, (byte)ViceMonitorCommand.CheckpointSet, errorCode: 0, body: infoBody);
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var checkpoint = await client.SetCheckpointAsync(0x0840);

        Assert.Equal(3u, checkpoint.Number);
        Assert.Equal((ushort)0x0840, checkpoint.StartAddress);
    }

    [Fact]
    public async Task CheckpointHit_FiresWhenTheServerSendsAnUnsolicitedCheckpointInfoEvent()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var hitSignal = new TaskCompletionSource<CheckpointHitEventArgs>();
        client.CheckpointHit += args => hitSignal.TrySetResult(args);

        var info = new CheckpointInfo(3, true, 0x0840, 0x0840, true, true, ViceCheckpointOperation.Exec, false, 1, 0, false, 0);
        var infoBody = ViceMonitorProtocol.EncodeCheckpointInfoBody(info);
        await server.SendUnsolicitedAsync((byte)ViceMonitorCommand.CheckpointGet, infoBody);

        var received = await hitSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3u, received.Checkpoint.Number);
        Assert.True(received.Checkpoint.CurrentlyHit);
    }

    private sealed class FakeViceMonitorServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _accepted;
        private NetworkStream? _stream;
        private Func<uint, ViceMonitorCommand, byte[], byte[]>? _handler;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task StartAsync()
        {
            _listener.Start();
            _ = AcceptAndServeAsync();
            await Task.CompletedTask;
        }

        public void OnRequest(Func<uint, ViceMonitorCommand, byte[], byte[]> handler) => _handler = handler;

        public async Task SendUnsolicitedAsync(byte responseType, byte[] body)
        {
            while (_stream is null)
                await Task.Delay(10);
            var frame = ViceMonitorProtocol.EncodeResponse(ViceMonitorProtocol.EventRequestId, responseType, 0, body);
            await _stream.WriteAsync(frame);
        }

        private async Task AcceptAndServeAsync()
        {
            _accepted = await _listener.AcceptTcpClientAsync();
            _stream = _accepted.GetStream();

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
                    var response = handler(requestHeader.RequestId, requestHeader.Command, body);
                    await _stream.WriteAsync(response);
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

        public void Dispose()
        {
            _stream?.Dispose();
            _accepted?.Dispose();
            _listener.Stop();
        }
    }
}
