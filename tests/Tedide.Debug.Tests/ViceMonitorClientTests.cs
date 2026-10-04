using Tedide.Tests.Shared;

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

    [Fact]
    public async Task SetConditionAsync_SendsCommand0x22_WithTheCheckpointNumberAndCondition()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        byte[]? received = null;
        server.OnRequest((requestId, command, body) =>
        {
            Assert.Equal(ViceMonitorCommand.ConditionSet, command);
            received = body;
            return ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: []);
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await client.SetConditionAsync(7, "A == $05");

        Assert.Equal(ViceMonitorProtocol.EncodeConditionSetBody(7, "A == $05"), received);
    }

    [Fact]
    public async Task SetConditionAsync_Throws_WhenViceRejectsTheCondition()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        // 0x8f: what a live VICE 3.9 answers for a condition it can't parse.
        server.OnRequest((requestId, command, _) => ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0x8f, body: []));

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var ex = await Assert.ThrowsAsync<ViceMonitorException>(() => client.SetConditionAsync(1, "garbage(("));
        Assert.Equal(0x8f, ex.ErrorCode);
    }

    [Fact]
    public async Task SetCheckpointAsync_ThrowsViceMonitorException_WhenTheServerRepliesWithAnErrorCode()
    {
        // VICE's error replies carry an empty body - decoding one as a checkpoint info reply used
        // to fail with an IndexOutOfRangeException instead of saying what actually went wrong.
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
            ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0x81, body: []));

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var ex = await Assert.ThrowsAsync<ViceMonitorException>(() => client.SetCheckpointAsync(0x0840));

        Assert.Equal(0x81, ex.ErrorCode);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_RatherThanThrowing_WhenTheServerRepliesWithAnErrorCode()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
            ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0x81, body: []));

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task Requests_FailWithIOException_InsteadOfHanging_OnceTheConnectionHasClosed()
    {
        var server = new FakeViceMonitorServer();
        await server.StartAsync();

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);
        await server.WaitForClientAsync();

        server.Dispose();
        await Task.Delay(300); // Let the client's read loop notice the connection is gone.

        // WaitAsync turns a hang into a TimeoutException, which ThrowsAnyAsync<IOException> rejects.
        await Assert.ThrowsAnyAsync<IOException>(() => client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FakeServer_ClosesTheConnection_EvenWhenDisposedBeforeItFinishedAccepting()
    {
        // The harness race behind Requests_FailWithIOException_...'s occasional failure under load:
        // Dispose ran while the accept was still completing, so the socket accepted afterwards was
        // never closed and the client's request hung until its 5-second timeout.
        var server = new FakeViceMonitorServer { DelayAfterAccept = TimeSpan.FromMilliseconds(300) };
        await server.StartAsync();

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        server.Dispose();
        await Task.Delay(600); // Past DelayAfterAccept, then time for the client to notice.

        await Assert.ThrowsAnyAsync<IOException>(() => client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AThrowingEventSubscriber_IsReported_AndDoesNotCloseTheConnection()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
            ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: []));

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var failure = new TaskCompletionSource<Exception>();
        client.CheckpointHit += _ => throw new InvalidOperationException("subscriber bug");
        client.EventHandlerFailed += ex => failure.TrySetResult(ex);

        var info = new CheckpointInfo(3, true, 0x0840, 0x0840, true, true, ViceCheckpointOperation.Exec, false, 1, 0, false, 0);
        await server.SendUnsolicitedAsync((byte)ViceMonitorCommand.CheckpointGet, ViceMonitorProtocol.EncodeCheckpointInfoBody(info));

        var reported = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("subscriber bug", reported.Message);
        Assert.True(await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StepAsync_WaitsForTheStoppedEvent_AndReturnsItsPc_RatherThanCompletingOnTheReply()
    {
        // Matches a live VICE 3.9: the Advance Instructions reply arrives immediately, before the
        // CPU has run - only the later Stopped event says where it ended up.
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, body) =>
        {
            Assert.Equal(ViceMonitorCommand.AdvanceInstructions, command);
            Assert.Equal(1, body[0]); // step-over flag
            return ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: []);
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var step = client.StepAsync(stepOverSubroutines: true);
        await Task.Delay(200);
        Assert.False(step.IsCompleted, "StepAsync completed on the reply alone, before any Stopped event.");

        await server.SendUnsolicitedAsync((byte)ViceMonitorEventType.Stopped, [0x49, 0x08]);

        Assert.Equal((ushort)0x0849, await step.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExecuteUntilReturnAsync_SendsCommand0x73_AndReturnsThePcItStoppedAt()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
        {
            Assert.Equal(ViceMonitorCommand.ExecuteUntilReturn, command);
            return
            [
                .. ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: []),
                .. ViceMonitorProtocol.EncodeResponse(ViceMonitorProtocol.EventRequestId, (byte)ViceMonitorEventType.Stopped, 0, [0x49, 0x08]),
            ];
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        Assert.Equal((ushort)0x0849, await client.ExecuteUntilReturnAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StepAsync_FailsInsteadOfHanging_WhenTheConnectionClosesBeforeVICEStops()
    {
        var server = new FakeViceMonitorServer();
        await server.StartAsync();
        server.OnRequest((requestId, command, _) =>
            ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: []));

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await server.WaitForClientAsync();
        var step = client.StepAsync();
        await Task.Delay(200); // Let the step's request go out and its reply come back first.
        server.Dispose();

        await Assert.ThrowsAnyAsync<IOException>(() => step.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task GetRegistersAsync_FetchesTheRegisterNamesOncePerConnection()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        var availableRequests = 0;
        server.OnRequest((requestId, command, _) =>
        {
            byte[] body = command switch
            {
                // One register: id 3, 16 bits, named "PC".
                ViceMonitorCommand.RegistersAvailable => [1, 0, 5, 3, 16, 2, (byte)'P', (byte)'C'],
                // One value: id 3 = $0846.
                ViceMonitorCommand.RegistersGet => [1, 0, 3, 3, 0x46, 0x08],
                _ => [],
            };
            if (command == ViceMonitorCommand.RegistersAvailable)
                Interlocked.Increment(ref availableRequests);
            return ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, errorCode: 0, body: body);
        });

        await using var client = new ViceMonitorClient();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var first = await client.GetRegistersAsync();
        var second = await client.GetRegistersAsync();

        Assert.Equal((ushort)0x0846, first["PC"]);
        Assert.Equal((ushort)0x0846, second["PC"]);
        Assert.Equal(1, availableRequests);
    }

    [Fact]
    public async Task Disconnected_FiresWhenVICEClosesTheConnection()
    {
        var server = new FakeViceMonitorServer();
        await server.StartAsync();
        await using var client = new ViceMonitorClient();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync("127.0.0.1", server.Port);
        await server.WaitForClientAsync();

        server.Dispose();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disconnected_DoesNotFire_WhenTheClientIsDisposedOnPurpose()
    {
        using var server = new FakeViceMonitorServer();
        await server.StartAsync();
        var client = new ViceMonitorClient();
        var fired = false;
        client.Disconnected += () => fired = true;
        await client.ConnectAsync("127.0.0.1", server.Port);

        await client.DisposeAsync();
        await Task.Delay(200);

        Assert.False(fired);
    }
}
