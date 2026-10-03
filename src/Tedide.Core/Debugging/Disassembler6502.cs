namespace Tedide.Core.Debugging;

/// <summary>One decoded instruction. <see cref="Cycles"/> is the base cycle count as text, with a
/// trailing "*" where crossing a page adds one and "**" for a branch (one more if taken, another if
/// that crosses a page). <see cref="Target"/> is the address an operand refers to, for labelling.</summary>
public sealed record DisassembledInstruction(
    ushort Address, byte[] Bytes, string Mnemonic, string Operand, ushort? Target, string Cycles, bool IsKnown)
{
    public int Length => Bytes.Length;

    public string Text => Operand.Length == 0 ? Mnemonic : $"{Mnemonic} {Operand}";

    public string BytesText => string.Join(' ', Bytes.Select(b => b.ToString("X2")));
}

/// <summary>
/// A 6502 disassembler for the debugger's Disassembly tab: every documented NMOS opcode, plus the
/// 65C02 additions (BRA, STZ, PHX/PHY/PLX/PLY, TRB/TSB, INC/DEC A, (zp) addressing, ...) when
/// <c>cmos</c> is set - the CPU cc65 targets for the enhanced Apple //e, the Lynx and a SuperCPU
/// C64. Anything else (NMOS "illegal" opcodes included) shows as a one-byte <c>.byte</c>. Mnemonics
/// are lowercase, matching cc65's own assembly and listings.
/// </summary>
public static class Disassembler6502
{
    private enum Mode
    {
        Implied, Accumulator, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY,
        Indirect, IndirectX, IndirectY, Relative, ZeroPageIndirect, AbsoluteIndirectX,
    }

    private readonly record struct Opcode(string Mnemonic, Mode Mode, string Cycles);

    private static readonly Dictionary<byte, Opcode> Nmos = BuildNmos();
    private static readonly Dictionary<byte, Opcode> Cmos = BuildCmos();

    private static Dictionary<byte, Opcode> BuildNmos()
    {
        var t = new Dictionary<byte, Opcode>();
        void Add(string mnemonic, params (int Code, Mode Mode, string Cycles)[] forms)
        {
            foreach (var (code, mode, cycles) in forms)
                t[(byte)code] = new Opcode(mnemonic, mode, cycles);
        }

        // The eight "group one" instructions share their addressing-mode layout.
        void GroupOne(string mnemonic, int baseCode, bool isStore = false)
        {
            if (!isStore)
                Add(mnemonic, (baseCode + 0x09 - 0x01, Mode.Immediate, "2"));
            Add(mnemonic,
                (baseCode + 0x04, Mode.ZeroPage, "3"), (baseCode + 0x14, Mode.ZeroPageX, "4"),
                (baseCode + 0x0C, Mode.Absolute, "4"),
                (baseCode + 0x1C, Mode.AbsoluteX, isStore ? "5" : "4*"), (baseCode + 0x18, Mode.AbsoluteY, isStore ? "5" : "4*"),
                (baseCode + 0x00, Mode.IndirectX, "6"), (baseCode + 0x10, Mode.IndirectY, isStore ? "6" : "5*"));
        }
        GroupOne("ora", 0x01);
        GroupOne("and", 0x21);
        GroupOne("eor", 0x41);
        GroupOne("adc", 0x61);
        GroupOne("sta", 0x81, isStore: true);
        GroupOne("lda", 0xA1);
        GroupOne("cmp", 0xC1);
        GroupOne("sbc", 0xE1);

        // Shifts and rotates, then INC/DEC.
        foreach (var (mnemonic, code) in new[] { ("asl", 0x00), ("rol", 0x20), ("lsr", 0x40), ("ror", 0x60) })
            Add(mnemonic, (code + 0x0A, Mode.Accumulator, "2"), (code + 0x06, Mode.ZeroPage, "5"), (code + 0x16, Mode.ZeroPageX, "6"),
                (code + 0x0E, Mode.Absolute, "6"), (code + 0x1E, Mode.AbsoluteX, "7"));
        Add("dec", (0xC6, Mode.ZeroPage, "5"), (0xD6, Mode.ZeroPageX, "6"), (0xCE, Mode.Absolute, "6"), (0xDE, Mode.AbsoluteX, "7"));
        Add("inc", (0xE6, Mode.ZeroPage, "5"), (0xF6, Mode.ZeroPageX, "6"), (0xEE, Mode.Absolute, "6"), (0xFE, Mode.AbsoluteX, "7"));

        Add("ldx", (0xA2, Mode.Immediate, "2"), (0xA6, Mode.ZeroPage, "3"), (0xB6, Mode.ZeroPageY, "4"), (0xAE, Mode.Absolute, "4"), (0xBE, Mode.AbsoluteY, "4*"));
        Add("ldy", (0xA0, Mode.Immediate, "2"), (0xA4, Mode.ZeroPage, "3"), (0xB4, Mode.ZeroPageX, "4"), (0xAC, Mode.Absolute, "4"), (0xBC, Mode.AbsoluteX, "4*"));
        Add("stx", (0x86, Mode.ZeroPage, "3"), (0x96, Mode.ZeroPageY, "4"), (0x8E, Mode.Absolute, "4"));
        Add("sty", (0x84, Mode.ZeroPage, "3"), (0x94, Mode.ZeroPageX, "4"), (0x8C, Mode.Absolute, "4"));
        Add("cpx", (0xE0, Mode.Immediate, "2"), (0xE4, Mode.ZeroPage, "3"), (0xEC, Mode.Absolute, "4"));
        Add("cpy", (0xC0, Mode.Immediate, "2"), (0xC4, Mode.ZeroPage, "3"), (0xCC, Mode.Absolute, "4"));
        Add("bit", (0x24, Mode.ZeroPage, "3"), (0x2C, Mode.Absolute, "4"));

        foreach (var (mnemonic, code) in new[] { ("bpl", 0x10), ("bmi", 0x30), ("bvc", 0x50), ("bvs", 0x70), ("bcc", 0x90), ("bcs", 0xB0), ("bne", 0xD0), ("beq", 0xF0) })
            Add(mnemonic, (code, Mode.Relative, "2**"));

        Add("jmp", (0x4C, Mode.Absolute, "3"), (0x6C, Mode.Indirect, "5"));
        Add("jsr", (0x20, Mode.Absolute, "6"));
        Add("brk", (0x00, Mode.Implied, "7"));
        Add("rti", (0x40, Mode.Implied, "6"));
        Add("rts", (0x60, Mode.Implied, "6"));
        Add("pha", (0x48, Mode.Implied, "3"));
        Add("php", (0x08, Mode.Implied, "3"));
        Add("pla", (0x68, Mode.Implied, "4"));
        Add("plp", (0x28, Mode.Implied, "4"));
        foreach (var (mnemonic, code) in new[]
        {
            ("clc", 0x18), ("sec", 0x38), ("cli", 0x58), ("sei", 0x78), ("clv", 0xB8), ("cld", 0xD8), ("sed", 0xF8),
            ("tax", 0xAA), ("tay", 0xA8), ("tsx", 0xBA), ("txa", 0x8A), ("txs", 0x9A), ("tya", 0x98),
            ("inx", 0xE8), ("iny", 0xC8), ("dex", 0xCA), ("dey", 0x88), ("nop", 0xEA),
        })
            Add(mnemonic, (code, Mode.Implied, "2"));
        return t;
    }

    private static Dictionary<byte, Opcode> BuildCmos()
    {
        var t = new Dictionary<byte, Opcode>(Nmos);
        foreach (var (mnemonic, code) in new[] { ("ora", 0x12), ("and", 0x32), ("eor", 0x52), ("adc", 0x72), ("sta", 0x92), ("lda", 0xB2), ("cmp", 0xD2), ("sbc", 0xF2) })
            t[(byte)code] = new Opcode(mnemonic, Mode.ZeroPageIndirect, "5");
        t[0x80] = new Opcode("bra", Mode.Relative, "3*");
        t[0x89] = new Opcode("bit", Mode.Immediate, "2");
        t[0x34] = new Opcode("bit", Mode.ZeroPageX, "4");
        t[0x3C] = new Opcode("bit", Mode.AbsoluteX, "4*");
        t[0x1A] = new Opcode("inc", Mode.Accumulator, "2");
        t[0x3A] = new Opcode("dec", Mode.Accumulator, "2");
        t[0x7C] = new Opcode("jmp", Mode.AbsoluteIndirectX, "6");
        t[0x6C] = new Opcode("jmp", Mode.Indirect, "6");
        t[0xDA] = new Opcode("phx", Mode.Implied, "3");
        t[0x5A] = new Opcode("phy", Mode.Implied, "3");
        t[0xFA] = new Opcode("plx", Mode.Implied, "4");
        t[0x7A] = new Opcode("ply", Mode.Implied, "4");
        t[0x64] = new Opcode("stz", Mode.ZeroPage, "3");
        t[0x74] = new Opcode("stz", Mode.ZeroPageX, "4");
        t[0x9C] = new Opcode("stz", Mode.Absolute, "4");
        t[0x9E] = new Opcode("stz", Mode.AbsoluteX, "5");
        t[0x04] = new Opcode("tsb", Mode.ZeroPage, "5");
        t[0x0C] = new Opcode("tsb", Mode.Absolute, "6");
        t[0x14] = new Opcode("trb", Mode.ZeroPage, "5");
        t[0x1C] = new Opcode("trb", Mode.Absolute, "6");
        return t;
    }

    /// <summary>Whether <paramref name="cpu"/> (a cc65 --cpu name) has the 65C02 instructions.</summary>
    public static bool IsCmos(string cpu) => cpu is "65c02" or "65sc02" or "65816";

    private static int OperandLength(Mode mode) => mode switch
    {
        Mode.Implied or Mode.Accumulator => 0,
        Mode.Absolute or Mode.AbsoluteX or Mode.AbsoluteY or Mode.Indirect or Mode.AbsoluteIndirectX => 2,
        _ => 1,
    };

    /// <summary>Decodes the instruction at <paramref name="offset"/> in <paramref name="memory"/>,
    /// which holds the bytes from <paramref name="baseAddress"/> on. One that runs past the end of
    /// <paramref name="memory"/> comes back as a <c>.byte</c>.</summary>
    public static DisassembledInstruction Decode(ReadOnlySpan<byte> memory, int offset, ushort baseAddress, bool cmos = false)
    {
        var address = (ushort)(baseAddress + offset);
        var code = memory[offset];
        var table = cmos ? Cmos : Nmos;
        if (!table.TryGetValue(code, out var opcode) || offset + 1 + OperandLength(opcode.Mode) > memory.Length)
            return new DisassembledInstruction(address, [code], ".byte", $"${code:X2}", null, "", false);

        var length = 1 + OperandLength(opcode.Mode);
        var bytes = memory.Slice(offset, length).ToArray();
        var value = length switch { 2 => bytes[1], 3 => bytes[1] | (bytes[2] << 8), _ => 0 };
        ushort? target = opcode.Mode switch
        {
            Mode.Relative => (ushort)(address + 2 + (sbyte)bytes[1]),
            Mode.Implied or Mode.Accumulator or Mode.Immediate => null,
            _ => (ushort)value,
        };
        var operand = opcode.Mode switch
        {
            Mode.Implied => "",
            Mode.Accumulator => "a",
            Mode.Immediate => $"#${value:X2}",
            Mode.ZeroPage => $"${value:X2}",
            Mode.ZeroPageX => $"${value:X2},x",
            Mode.ZeroPageY => $"${value:X2},y",
            Mode.Absolute => $"${value:X4}",
            Mode.AbsoluteX => $"${value:X4},x",
            Mode.AbsoluteY => $"${value:X4},y",
            Mode.Indirect => $"(${value:X4})",
            Mode.IndirectX => $"(${value:X2},x)",
            Mode.IndirectY => $"(${value:X2}),y",
            Mode.ZeroPageIndirect => $"(${value:X2})",
            Mode.AbsoluteIndirectX => $"(${value:X4},x)",
            Mode.Relative => $"${target:X4}",
            _ => "",
        };
        return new DisassembledInstruction(address, bytes, opcode.Mnemonic, operand, target, opcode.Cycles, true);
    }

    /// <summary>Decodes consecutive instructions from <paramref name="startOffset"/> until
    /// <paramref name="memory"/> runs out or <paramref name="maxCount"/> are decoded.</summary>
    public static List<DisassembledInstruction> Disassemble(ReadOnlySpan<byte> memory, ushort baseAddress, int startOffset = 0, int maxCount = int.MaxValue, bool cmos = false)
    {
        var result = new List<DisassembledInstruction>();
        for (var offset = startOffset; offset < memory.Length && result.Count < maxCount;)
        {
            var instruction = Decode(memory, offset, baseAddress, cmos);
            result.Add(instruction);
            offset += instruction.Length;
        }
        return result;
    }

    /// <summary>
    /// Where to start disassembling so the listing shows some instructions before
    /// <paramref name="targetOffset"/> (the PC) and still lands exactly on it. 6502 code can't be
    /// decoded backwards, so this tries each start up to <paramref name="maxBack"/> bytes earlier and
    /// keeps the furthest one whose instructions line up with the target, preferring a run of real
    /// instructions over one that needs <c>.byte</c>s to get there. Returns the target itself if
    /// nothing earlier lines up.
    /// </summary>
    public static int FindStartBefore(ReadOnlySpan<byte> memory, int targetOffset, int maxBack, bool cmos = false)
    {
        var best = targetOffset;
        var bestUnknown = int.MaxValue;
        for (var back = Math.Min(maxBack, targetOffset); back > 0; back--)
        {
            var offset = targetOffset - back;
            var unknown = 0;
            while (offset < targetOffset)
            {
                var instruction = Decode(memory, offset, 0, cmos);
                if (!instruction.IsKnown)
                    unknown++;
                offset += instruction.Length;
            }
            // Furthest start wins only among those with the fewest .bytes.
            if (offset == targetOffset && unknown < bestUnknown)
            {
                best = targetOffset - back;
                bestUnknown = unknown;
            }
        }
        return best;
    }
}
