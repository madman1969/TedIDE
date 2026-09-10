namespace Tedide.Debug;

/// <summary>Binary monitor command type bytes (request header offset 10 / response header offset 6)
/// - see <see cref="ViceMonitorProtocol"/>'s own doc comment for where this table came from.</summary>
internal enum ViceMonitorCommand : byte
{
    MemoryGet = 0x01,
    MemorySet = 0x02,
    CheckpointGet = 0x11,
    CheckpointSet = 0x12,
    CheckpointDelete = 0x13,
    CheckpointList = 0x14,
    CheckpointToggle = 0x15,
    RegistersGet = 0x31,
    RegistersSet = 0x32,
    AdvanceInstructions = 0x71,
    Ping = 0x81,
    RegistersAvailable = 0x83,
    ExitMonitor = 0xaa,
    Quit = 0xbb,
    Reset = 0xcc,
}

/// <summary>Unsolicited response types (arrive with request id <see cref="ViceMonitorProtocol.EventRequestId"/>) not tied to any request Tedide sent.</summary>
internal enum ViceMonitorEventType : byte
{
    Jam = 0x61,
    Stopped = 0x62,
    Resumed = 0x63,
}

/// <summary>
/// Encodes/decodes VICE's binary monitor protocol wire format - kept free of any socket/connection
/// code (see <see cref="ViceMonitorClient"/> for that) so it's testable as pure byte-array
/// round-trips. Confirmed against the official VICE Manual, chapter 13 "Binary monitor"
/// (https://vice-emu.sourceforge.io/vice_13.html) - every byte offset/width/command value below was
/// fetched and cross-checked against that page's own tables during implementation, not assumed from
/// this project's own planning notes. All multibyte values are little-endian.
///
/// Request header (11 bytes + body): STX(1)=0x02, ApiVersion(1)=0x02, BodyLength(4), RequestId(4),
/// CommandType(1), then the command-specific body.
/// Response header (12 bytes + body): STX(1)=0x02, ApiVersion(1)=0x02, BodyLength(4), ResponseType(1),
/// ErrorCode(1), RequestId(4), then the response-specific body. An unsolicited event (not a reply to
/// anything Tedide sent - e.g. hitting a checkpoint, or the user pausing/resuming from VICE's own UI)
/// carries RequestId = 0xFFFFFFFF instead of echoing a real request id.
/// </summary>
internal static class ViceMonitorProtocol
{
    public const byte Stx = 0x02;
    public const byte ApiVersion = 0x02;
    public const uint EventRequestId = 0xFFFFFFFF;
    public const int RequestHeaderLength = 11;
    public const int ResponseHeaderLength = 12;

    public readonly record struct ResponseHeader(byte ResponseType, byte ErrorCode, uint RequestId, uint BodyLength);

    public static byte[] EncodeRequest(uint requestId, ViceMonitorCommand command, ReadOnlySpan<byte> body)
    {
        var buffer = new byte[RequestHeaderLength + body.Length];
        buffer[0] = Stx;
        buffer[1] = ApiVersion;
        WriteUInt32LE(buffer, 2, (uint)body.Length);
        WriteUInt32LE(buffer, 6, requestId);
        buffer[10] = (byte)command;
        body.CopyTo(buffer.AsSpan(RequestHeaderLength));
        return buffer;
    }

    /// <summary>Encodes a response frame (header + body) - VICE itself is the only real sender of
    /// these, but this is also how <c>ViceMonitorClientTests</c>' fake server plays back canned
    /// solicited replies and unsolicited events without duplicating the header layout.</summary>
    public static byte[] EncodeResponse(uint requestId, byte responseType, byte errorCode, ReadOnlySpan<byte> body)
    {
        var buffer = new byte[ResponseHeaderLength + body.Length];
        buffer[0] = Stx;
        buffer[1] = ApiVersion;
        WriteUInt32LE(buffer, 2, (uint)body.Length);
        buffer[6] = responseType;
        buffer[7] = errorCode;
        WriteUInt32LE(buffer, 8, requestId);
        body.CopyTo(buffer.AsSpan(ResponseHeaderLength));
        return buffer;
    }

    public readonly record struct RequestHeader(uint RequestId, ViceMonitorCommand Command, uint BodyLength);

    /// <summary>Decodes a request frame's header - the client (<see cref="ViceMonitorClient"/>)
    /// never needs this (it only sends requests), but <c>ViceMonitorClientTests</c>' fake server
    /// does, to read what Tedide sent it.</summary>
    public static RequestHeader DecodeRequestHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < RequestHeaderLength)
            throw new ArgumentException($"Request header must be at least {RequestHeaderLength} bytes.", nameof(header));
        if (header[0] != Stx)
            throw new InvalidDataException($"Expected STX (0x02) at request header offset 0, got 0x{header[0]:X2}.");

        return new RequestHeader(
            RequestId: ReadUInt32LE(header, 6),
            Command: (ViceMonitorCommand)header[10],
            BodyLength: ReadUInt32LE(header, 2));
    }

    public static ResponseHeader DecodeResponseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < ResponseHeaderLength)
            throw new ArgumentException($"Response header must be at least {ResponseHeaderLength} bytes.", nameof(header));
        if (header[0] != Stx)
            throw new InvalidDataException($"Expected STX (0x02) at response header offset 0, got 0x{header[0]:X2}.");

        return new ResponseHeader(
            ResponseType: header[6],
            ErrorCode: header[7],
            RequestId: ReadUInt32LE(header, 8),
            BodyLength: ReadUInt32LE(header, 2));
    }

    /// <summary>Encodes a checkpoint info body - the counterpart to <see cref="DecodeCheckpointInfoBody"/>,
    /// used by <c>ViceMonitorClientTests</c>' fake server to play back a canned reply/event; VICE
    /// itself is the only real encoder of this on the wire.</summary>
    public static byte[] EncodeCheckpointInfoBody(CheckpointInfo info)
    {
        var body = new byte[23];
        WriteUInt32LE(body, 0, info.Number);
        body[4] = info.CurrentlyHit ? (byte)1 : (byte)0;
        WriteUInt16LE(body, 5, info.StartAddress);
        WriteUInt16LE(body, 7, info.EndAddress);
        body[9] = info.StopWhenHit ? (byte)1 : (byte)0;
        body[10] = info.Enabled ? (byte)1 : (byte)0;
        body[11] = (byte)info.Operation;
        body[12] = info.Temporary ? (byte)1 : (byte)0;
        WriteUInt32LE(body, 13, info.HitCount);
        WriteUInt32LE(body, 17, info.IgnoreCount);
        body[21] = info.HasCondition ? (byte)1 : (byte)0;
        body[22] = info.Memspace;
        return body;
    }

    // ---- Checkpoint set (0x12) request: "SA SA | EA EA | ST | EN | OP | TM | MS" ----
    public static byte[] EncodeCheckpointSetBody(ushort startAddress, ushort endAddress, bool stopWhenHit, bool enabled, ViceCheckpointOperation operation, bool temporary, byte memspace = 0)
    {
        var body = new byte[9];
        WriteUInt16LE(body, 0, startAddress);
        WriteUInt16LE(body, 2, endAddress);
        body[4] = stopWhenHit ? (byte)1 : (byte)0;
        body[5] = enabled ? (byte)1 : (byte)0;
        body[6] = (byte)operation;
        body[7] = temporary ? (byte)1 : (byte)0;
        body[8] = memspace;
        return body;
    }

    // ---- Checkpoint info (0x11) response: "CN(4) CH(1) SA(2) EA(2) ST(1) EN(1) OP(1) TM(1) HC(4) IC(4) CE(1) MS(1)" ----
    public static CheckpointInfo DecodeCheckpointInfoBody(ReadOnlySpan<byte> body) => new(
        Number: ReadUInt32LE(body, 0),
        CurrentlyHit: body[4] != 0,
        StartAddress: ReadUInt16LE(body, 5),
        EndAddress: ReadUInt16LE(body, 7),
        StopWhenHit: body[9] != 0,
        Enabled: body[10] != 0,
        Operation: (ViceCheckpointOperation)body[11],
        Temporary: body[12] != 0,
        HitCount: ReadUInt32LE(body, 13),
        IgnoreCount: ReadUInt32LE(body, 17),
        HasCondition: body[21] != 0,
        Memspace: body[22]);

    // ---- Checkpoint delete (0x13) request: "CN CN CN CN" ----
    public static byte[] EncodeCheckpointDeleteBody(uint checkpointNumber)
    {
        var body = new byte[4];
        WriteUInt32LE(body, 0, checkpointNumber);
        return body;
    }

    // ---- Registers available (0x83) request: "MS"; response: "RC(2) [ IS(1) RI(1) RS(1) NL(1) RN(NL) ]*RC" ----
    public static byte[] EncodeRegistersAvailableBody(byte memspace = 0) => [memspace];

    public static IReadOnlyList<RegisterDescriptor> DecodeRegistersAvailableBody(ReadOnlySpan<byte> body)
    {
        var count = ReadUInt16LE(body, 0);
        var registers = new List<RegisterDescriptor>(count);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            var itemSize = body[offset];
            var registerId = body[offset + 1];
            var sizeBits = body[offset + 2];
            var nameLength = body[offset + 3];
            var name = System.Text.Encoding.ASCII.GetString(body.Slice(offset + 4, nameLength));
            registers.Add(new RegisterDescriptor(registerId, name, sizeBits));
            offset += 1 + itemSize; // itemSize excludes the IS byte itself
        }
        return registers;
    }

    // ---- Registers get (0x31) request: "MS"; response: "RC(2) [ IS(1) RI(1) RV(2) ]*RC" ----
    public static byte[] EncodeRegistersGetBody(byte memspace = 0) => [memspace];

    public static IReadOnlyDictionary<byte, ushort> DecodeRegistersGetBody(ReadOnlySpan<byte> body)
    {
        var count = ReadUInt16LE(body, 0);
        var values = new Dictionary<byte, ushort>(count);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            var itemSize = body[offset];
            var registerId = body[offset + 1];
            var value = ReadUInt16LE(body, offset + 2);
            values[registerId] = value;
            offset += 1 + itemSize;
        }
        return values;
    }

    // ---- Registers set (0x32) request: "MS | RC(2) [ IS(1) RI(1) RV(2) ]*RC" (same per-item shape as Get's response) ----
    public static byte[] EncodeRegistersSetBody(IReadOnlyDictionary<byte, ushort> values, byte memspace = 0)
    {
        var body = new byte[1 + 2 + values.Count * 4];
        body[0] = memspace;
        WriteUInt16LE(body, 1, (ushort)values.Count);
        var offset = 3;
        foreach (var (id, value) in values)
        {
            body[offset] = 3; // item size excludes this byte: RI(1) + RV(2) = 3
            body[offset + 1] = id;
            WriteUInt16LE(body, offset + 2, value);
            offset += 4;
        }
        return body;
    }

    // ---- Memory get (0x01) request: "FX(1) SA(2) EA(2) MS(1) BI(2)"; response: "ML(2) MM(ML)" ----
    public static byte[] EncodeMemoryGetBody(ushort startAddress, ushort endAddress, bool sideEffects = false, byte memspace = 0, ushort bankId = 0)
    {
        var body = new byte[8];
        body[0] = sideEffects ? (byte)1 : (byte)0;
        WriteUInt16LE(body, 1, startAddress);
        WriteUInt16LE(body, 3, endAddress);
        body[5] = memspace;
        WriteUInt16LE(body, 6, bankId);
        return body;
    }

    public static byte[] DecodeMemoryGetBody(ReadOnlySpan<byte> body)
    {
        var length = ReadUInt16LE(body, 0);
        return body.Slice(2, length).ToArray();
    }

    // ---- Memory set (0x02) request: "FX(1) SA(2) EA(2) MS(1) BI(2) MM(EA-SA+1)" ----
    public static byte[] EncodeMemorySetBody(ushort startAddress, ushort endAddress, ReadOnlySpan<byte> data, bool sideEffects = false, byte memspace = 0, ushort bankId = 0)
    {
        var body = new byte[8 + data.Length];
        body[0] = sideEffects ? (byte)1 : (byte)0;
        WriteUInt16LE(body, 1, startAddress);
        WriteUInt16LE(body, 3, endAddress);
        body[5] = memspace;
        WriteUInt16LE(body, 6, bankId);
        data.CopyTo(body.AsSpan(8));
        return body;
    }

    // ---- Advance instructions / step (0x71) request: "SO(1) IC(2)" ----
    public static byte[] EncodeAdvanceInstructionsBody(ushort instructionCount, bool stepOverSubroutines = false)
    {
        var body = new byte[3];
        body[0] = stepOverSubroutines ? (byte)1 : (byte)0;
        WriteUInt16LE(body, 1, instructionCount);
        return body;
    }

    // ---- Stopped (0x62) / Resumed (0x63) event body: "PC(2)" ----
    public static ushort DecodeProgramCounterBody(ReadOnlySpan<byte> body) => ReadUInt16LE(body, 0);

    private static void WriteUInt16LE(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32LE(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static ushort ReadUInt16LE(ReadOnlySpan<byte> buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static uint ReadUInt32LE(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}
