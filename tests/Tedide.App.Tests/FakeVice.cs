using System.Text;
using Tedide.Core.Debugging;
using Tedide.Debug;
using Tedide.Tests.Shared;

namespace Tedide.App.Tests;

/// <summary>
/// Just enough of VICE's binary monitor for a debug session: a program loaded into 64K of memory,
/// the registers, and checkpoints. Continue stops at the next armed checkpoint (the lowest-numbered
/// one not yet hit), as if the program had run there; a step moves the PC one instruction on -
/// over a JSR, along a JMP, never taking a branch - and, as VICE does, replies before reporting
/// the stop. Nothing actually executes.
/// </summary>
internal sealed class FakeVice : IDisposable
{
    private static readonly string[] RegisterNames = ["PC", "A", "X", "Y", "SP", "FL"];

    private readonly FakeViceMonitorServer _server = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, ushort> _checkpoints = [];
    private readonly HashSet<uint> _hit = [];
    private uint _nextCheckpoint = 1;

    public FakeVice(string prgPath)
    {
        var prg = File.ReadAllBytes(prgPath);
        var loadAddress = prg[0] | prg[1] << 8;
        prg.AsSpan(2).CopyTo(Memory.AsSpan(loadAddress));
        _server.OnRequestFrames(Answer);
    }

    public byte[] Memory { get; } = new byte[0x10000];
    public ushort Pc { get; set; }
    public ushort Sp { get; set; } = 0x1FF;

    /// <summary>Every request, in order - to check what the session asked VICE to do.</summary>
    public List<ViceMonitorCommand> Requests { get; } = [];

    /// <summary>The checkpoints set now, by number.</summary>
    public IReadOnlyDictionary<uint, ushort> Checkpoints
    {
        get
        {
            lock (_gate)
                return new Dictionary<uint, ushort>(_checkpoints);
        }
    }

    public int Port => _server.Port;

    public async Task StartAsync() => await _server.StartAsync();

    /// <summary>Closes the connection, as VICE does when it's closed.</summary>
    public void Dispose() => _server.Dispose();

    private IReadOnlyList<byte[]> Answer(uint requestId, ViceMonitorCommand command, byte[] body)
    {
        lock (_gate)
        {
            Requests.Add(command);
            byte[] Reply(byte[]? replyBody = null) => ViceMonitorProtocol.EncodeResponse(requestId, (byte)command, 0, replyBody ?? []);

            switch (command)
            {
                case ViceMonitorCommand.CheckpointSet:
                {
                    var number = _nextCheckpoint++;
                    var address = (ushort)(body[0] | body[1] << 8);
                    _checkpoints[number] = address;
                    return [Reply(ViceMonitorProtocol.EncodeCheckpointInfoBody(Info(number, address, hit: false)))];
                }
                case ViceMonitorCommand.CheckpointDelete:
                    _checkpoints.Remove(BitConverter.ToUInt32(body, 0));
                    return [Reply()];
                case ViceMonitorCommand.RegistersAvailable:
                    return [Reply(RegistersAvailableBody())];
                case ViceMonitorCommand.RegistersGet:
                    return [Reply(RegistersBody())];
                case ViceMonitorCommand.MemoryGet:
                {
                    var start = body[1] | body[2] << 8;
                    var end = body[3] | body[4] << 8;
                    var length = end - start + 1;
                    var reply = new byte[2 + length];
                    reply[0] = (byte)length;
                    reply[1] = (byte)(length >> 8);
                    Memory.AsSpan(start, length).CopyTo(reply.AsSpan(2));
                    return [Reply(reply)];
                }
                case ViceMonitorCommand.AdvanceInstructions:
                    Pc = NextInstruction(Pc);
                    return [Reply(), Event(ViceMonitorEventType.Stopped, Pc)];
                case ViceMonitorCommand.ExecuteUntilReturn:
                    Pc = (ushort)(Pc + 1);
                    return [Reply(), Event(ViceMonitorEventType.Stopped, Pc)];
                case ViceMonitorCommand.ExitMonitor:
                {
                    var frames = new List<byte[]> { Reply(), Event(ViceMonitorEventType.Resumed, Pc) };
                    var next = _checkpoints.Where(c => !_hit.Contains(c.Key)).OrderBy(c => c.Key).FirstOrDefault();
                    if (next.Key != 0)
                    {
                        _hit.Add(next.Key);
                        Pc = next.Value;
                        frames.Add(ViceMonitorProtocol.EncodeResponse(ViceMonitorProtocol.EventRequestId, (byte)ViceMonitorCommand.CheckpointGet, 0,
                            ViceMonitorProtocol.EncodeCheckpointInfoBody(Info(next.Key, next.Value, hit: true))));
                        frames.Add(Event(ViceMonitorEventType.Stopped, Pc));
                    }
                    return frames;
                }
                default:
                    // ConditionSet, Ping and the rest: accepted, nothing to say.
                    return [Reply()];
            }
        }
    }

    private static CheckpointInfo Info(uint number, ushort address, bool hit) =>
        new(number, hit, address, address, true, true, ViceCheckpointOperation.Exec, false, hit ? 1u : 0, 0, false, 0);

    private static byte[] Event(ViceMonitorEventType type, ushort pc) =>
        ViceMonitorProtocol.EncodeResponse(ViceMonitorProtocol.EventRequestId, (byte)type, 0, [(byte)pc, (byte)(pc >> 8)]);

    /// <summary>Where the instruction at <paramref name="pc"/> goes on to: past it, or a JMP's target.</summary>
    private ushort NextInstruction(ushort pc)
    {
        var instruction = Disassembler6502.Decode(Memory, pc, 0);
        return Memory[pc] == 0x4C && instruction.Target is { } target ? target : (ushort)(pc + instruction.Length);
    }

    // "RC(2) [ IS(1) RI(1) RS(1) NL(1) NAME ]*RC"
    private static byte[] RegistersAvailableBody()
    {
        var body = new List<byte> { (byte)RegisterNames.Length, 0 };
        for (var id = 0; id < RegisterNames.Length; id++)
        {
            var name = Encoding.ASCII.GetBytes(RegisterNames[id]);
            body.AddRange([(byte)(3 + name.Length), (byte)id, (byte)(RegisterNames[id] is "PC" ? 16 : 8), (byte)name.Length]);
            body.AddRange(name);
        }
        return [.. body];
    }

    // "RC(2) [ IS(1) RI(1) RV(2) ]*RC"
    private byte[] RegistersBody()
    {
        ushort[] values = [Pc, 0, 0, 0, Sp, 0];
        var body = new List<byte> { (byte)values.Length, 0 };
        for (var id = 0; id < values.Length; id++)
            body.AddRange([3, (byte)id, (byte)values[id], (byte)(values[id] >> 8)]);
        return [.. body];
    }
}
