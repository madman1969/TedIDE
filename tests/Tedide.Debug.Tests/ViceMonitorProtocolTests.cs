namespace Tedide.Debug.Tests;

public class ViceMonitorProtocolTests
{
    [Fact]
    public void EncodeRequest_WritesTheHeaderExactlyAsTheViceManualDocumentsIt()
    {
        // STX(1)=0x02, ApiVersion(1)=0x02, BodyLength(4 LE), RequestId(4 LE), CommandType(1), body.
        var request = ViceMonitorProtocol.EncodeRequest(0x00000005, ViceMonitorCommand.Ping, [0xAA, 0xBB]);

        Assert.Equal(
            new byte[] { 0x02, 0x02, 0x02, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x81, 0xAA, 0xBB },
            request);
    }

    [Fact]
    public void EncodeRequest_OmitsTheBody_WhenGivenNone()
    {
        var request = ViceMonitorProtocol.EncodeRequest(1, ViceMonitorCommand.Ping, []);

        Assert.Equal(ViceMonitorProtocol.RequestHeaderLength, request.Length);
    }

    [Fact]
    public void DecodeResponseHeader_ReadsEveryFieldAtItsDocumentedOffset()
    {
        // STX(1)=0x02, ApiVersion(1)=0x02, BodyLength(4 LE)=3, ResponseType(1)=0x31, ErrorCode(1)=0x00,
        // RequestId(4 LE)=7.
        byte[] header = [0x02, 0x02, 0x03, 0x00, 0x00, 0x00, 0x31, 0x00, 0x07, 0x00, 0x00, 0x00];

        var decoded = ViceMonitorProtocol.DecodeResponseHeader(header);

        Assert.Equal(0x31, decoded.ResponseType);
        Assert.Equal(0x00, decoded.ErrorCode);
        Assert.Equal(7u, decoded.RequestId);
        Assert.Equal(3u, decoded.BodyLength);
    }

    [Fact]
    public void DecodeResponseHeader_RecognizesTheEventRequestId()
    {
        byte[] header = [0x02, 0x02, 0x00, 0x00, 0x00, 0x00, 0x62, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

        var decoded = ViceMonitorProtocol.DecodeResponseHeader(header);

        Assert.Equal(ViceMonitorProtocol.EventRequestId, decoded.RequestId);
    }

    [Fact]
    public void DecodeResponseHeader_ThrowsIfTheFirstByteIsNotStx()
    {
        byte[] header = new byte[12];
        header[0] = 0x00;

        Assert.Throws<InvalidDataException>(() => ViceMonitorProtocol.DecodeResponseHeader(header));
    }

    [Fact]
    public void CheckpointSetBody_EncodesEveryFieldInTheDocumentedOrder()
    {
        // "SA SA | EA EA | ST | EN | OP | TM | MS": start=0x0840, end=0x0850, stop=true, enabled=true,
        // op=Exec(0x04), temporary=false, memspace=main(0x00).
        var body = ViceMonitorProtocol.EncodeCheckpointSetBody(0x0840, 0x0850, true, true, ViceCheckpointOperation.Exec, false);

        Assert.Equal(
            new byte[] { 0x40, 0x08, 0x50, 0x08, 0x01, 0x01, 0x04, 0x00, 0x00 },
            body);
    }

    [Fact]
    public void CheckpointInfoBody_DecodesEveryFieldAtItsDocumentedOffset()
    {
        // "CN(4) CH(1) SA(2) EA(2) ST(1) EN(1) OP(1) TM(1) HC(4) IC(4) CE(1) MS(1)"
        byte[] body =
        [
            0x01, 0x00, 0x00, 0x00, // CN = 1
            0x01,                   // CH = true
            0x40, 0x08,             // SA = 0x0840
            0x50, 0x08,             // EA = 0x0850
            0x01,                   // ST = true
            0x01,                   // EN = true
            0x04,                   // OP = Exec
            0x00,                   // TM = false
            0x02, 0x00, 0x00, 0x00, // HC = 2
            0x00, 0x00, 0x00, 0x00, // IC = 0
            0x00,                   // CE = false
            0x00,                   // MS = main
        ];

        var info = ViceMonitorProtocol.DecodeCheckpointInfoBody(body);

        Assert.Equal(1u, info.Number);
        Assert.True(info.CurrentlyHit);
        Assert.Equal((ushort)0x0840, info.StartAddress);
        Assert.Equal((ushort)0x0850, info.EndAddress);
        Assert.True(info.StopWhenHit);
        Assert.True(info.Enabled);
        Assert.Equal(ViceCheckpointOperation.Exec, info.Operation);
        Assert.False(info.Temporary);
        Assert.Equal(2u, info.HitCount);
        Assert.Equal(0u, info.IgnoreCount);
        Assert.False(info.HasCondition);
        Assert.Equal((byte)0, info.Memspace);
    }

    [Fact]
    public void CheckpointDeleteBody_EncodesTheCheckpointNumberAsFourBytes()
    {
        var body = ViceMonitorProtocol.EncodeCheckpointDeleteBody(0x00000042);

        Assert.Equal(new byte[] { 0x42, 0x00, 0x00, 0x00 }, body);
    }

    [Fact]
    public void RegistersAvailableBody_DecodesEachItemsIdSizeAndName()
    {
        // "RC(2) [ IS(1) RI(1) RS(1) NL(1) RN(NL) ]*RC" - one register named "A", 8 bits, id=0.
        // IS excludes itself: RI(1)+RS(1)+NL(1)+RN(1) = 4.
        byte[] body = [0x01, 0x00, 0x04, 0x00, 0x08, 0x01, (byte)'A'];

        var registers = ViceMonitorProtocol.DecodeRegistersAvailableBody(body);

        var register = Assert.Single(registers);
        Assert.Equal((byte)0, register.Id);
        Assert.Equal("A", register.Name);
        Assert.Equal((byte)8, register.SizeBits);
    }

    [Fact]
    public void RegistersAvailableBody_DecodesMultipleItems()
    {
        byte[] body =
        [
            0x02, 0x00,                         // RC = 2
            0x04, 0x00, 0x08, 0x01, (byte)'A',  // id=0 "A" 8 bits
            0x05, 0x01, 0x10, 0x02, (byte)'P', (byte)'C', // id=1 "PC" 16 bits
        ];

        var registers = ViceMonitorProtocol.DecodeRegistersAvailableBody(body);

        Assert.Equal(2, registers.Count);
        Assert.Equal("A", registers[0].Name);
        Assert.Equal("PC", registers[1].Name);
        Assert.Equal((byte)16, registers[1].SizeBits);
    }

    [Fact]
    public void RegistersGetBody_DecodesEachItemsIdAndValue()
    {
        // "RC(2) [ IS(1) RI(1) RV(2) ]*RC" - IS excludes itself: RI(1)+RV(2) = 3.
        byte[] body = [0x01, 0x00, 0x03, 0x02, 0x40, 0x08];

        var values = ViceMonitorProtocol.DecodeRegistersGetBody(body);

        Assert.Equal((ushort)0x0840, values[2]);
    }

    [Fact]
    public void RegistersSetBody_EncodesTheSamePerItemShapeAsRegistersGetsResponse()
    {
        var body = ViceMonitorProtocol.EncodeRegistersSetBody(new Dictionary<byte, ushort> { [2] = 0x0840 });

        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x03, 0x02, 0x40, 0x08 }, body);
    }

    [Fact]
    public void MemoryGetBody_EncodesEveryFieldInTheDocumentedOrder()
    {
        // "FX(1) SA(2) EA(2) MS(1) BI(2)"
        var body = ViceMonitorProtocol.EncodeMemoryGetBody(0x0840, 0x0850);

        Assert.Equal(new byte[] { 0x00, 0x40, 0x08, 0x50, 0x08, 0x00, 0x00, 0x00 }, body);
    }

    [Fact]
    public void MemoryGetBody_DecodesTheLengthPrefixedContents()
    {
        // "ML(2) MM(ML)"
        byte[] body = [0x02, 0x00, 0xAA, 0xBB];

        var memory = ViceMonitorProtocol.DecodeMemoryGetBody(body);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, memory);
    }

    [Fact]
    public void MemorySetBody_EncodesTheAddressRangeThenTheData()
    {
        // "FX(1) SA(2) EA(2) MS(1) BI(2) MM(...)"
        var body = ViceMonitorProtocol.EncodeMemorySetBody(0x0840, 0x0841, [0xAA, 0xBB]);

        Assert.Equal(new byte[] { 0x00, 0x40, 0x08, 0x41, 0x08, 0x00, 0x00, 0x00, 0xAA, 0xBB }, body);
    }

    [Fact]
    public void AdvanceInstructionsBody_EncodesStepOverFlagThenCount()
    {
        // "SO(1) IC(2)"
        var body = ViceMonitorProtocol.EncodeAdvanceInstructionsBody(instructionCount: 1, stepOverSubroutines: false);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x00 }, body);
    }

    [Fact]
    public void ProgramCounterBody_DecodesTheTwoByteAddress()
    {
        byte[] body = [0x40, 0x08];

        Assert.Equal((ushort)0x0840, ViceMonitorProtocol.DecodeProgramCounterBody(body));
    }
}
