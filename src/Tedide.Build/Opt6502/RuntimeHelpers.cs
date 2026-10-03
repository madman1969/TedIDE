namespace Tedide.Build.Opt6502;

/// <summary>
/// One cc65 runtime helper <see cref="RuntimeInliner"/> may inline. <see cref="Body"/> holds ca65
/// statements, where <c>{1}</c>..<c>{4}</c> are the body's own local labels and <c>{E}</c> the
/// label placed after the block. <see cref="CyclesSaved"/> is for the path that takes no exit
/// part-way through: the JSR+RTS pair (12), plus 3 for helpers that were a
/// <c>ldy #n / jmp other</c> stub, less 3 where that path now runs <c>JMP {E}</c> instead of an RTS.
/// <see cref="ZeroPage"/> names the zero-page symbols the body uses; a call is only inlined in a
/// file that imports them all.
/// </summary>
internal sealed record RuntimeHelper(string Name, string[] Body, int CyclesSaved, string[] ZeroPage);

/// <summary>
/// The helpers <see cref="RuntimeInliner"/> knows. Every body is cc65's own code, from cc65's
/// <c>libsrc/runtime</c> at git b75f872 (the cc65 2.19 build Tedide is tested with; cc65 is under
/// the zlib licence), changed only mechanically: a final RTS dropped, an RTS elsewhere turned into
/// <c>JMP {E}</c>, a <c>jmp</c> to another helper replaced by that helper's body, <c>add</c>/<c>sub</c>
/// (macpack generic) spelled out as CLC/ADC and SEC/SBC, and labels renamed. So each leaves A, X,
/// Y, the flags and memory exactly as the real helper does.
///
/// Where a helper has a 65SC02 variant (<c>.if .cpu</c>), the 6502 one is used - for every helper
/// here both leave identical state, so the inlined copy is right whichever library the program
/// links: the 6502 one every Commodore target uses, or a 65C02 one. Helpers whose variants differ
/// are left out: popax, tosaddax and pusheax (Y), incax1 (carry). So are multiply, divide and
/// modulo, which run 100+ cycles - 12 saved doesn't justify their size.
/// </summary>
internal static class RuntimeHelpers
{
    private static readonly string[] PushaxBody =
        ["pha", "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
         "{1}: ldy #1", "txa", "sta (sp),y", "pla", "dey", "sta (sp),y"];

    private static string[] DecspBody(int n) =>
        ["lda sp", "sec", $"sbc #{n}", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1"];

    private static readonly string[] AddyspBody =
        ["pha", "clc", "tya", "adc sp", "sta sp", "bcc {1}", "inc sp+1", "{1}: pla"];

    private static readonly string[] Incsp2Body =
        ["inc sp", "beq {1}", "inc sp", "beq {2}", "jmp {E}", "{1}: inc sp", "{2}: inc sp+1"];

    private static readonly string[] PushwyspBody =
        ["lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
         "{1}: lda (sp),y", "tax", "dey", "lda (sp),y", "ldy #$00", "sta (sp),y", "iny", "txa", "sta (sp),y"];

    private static readonly string[] AddeqyspBody =
        ["clc", "adc (sp),y", "sta (sp),y", "pha", "iny", "txa", "adc (sp),y", "sta (sp),y", "tax", "pla"];

    private static readonly string[] SubeqyspBody =
        ["sec", "eor #$FF", "adc (sp),y", "sta (sp),y", "pha", "iny", "txa", "eor #$FF", "adc (sp),y", "sta (sp),y", "tax", "pla"];

    private static readonly string[] LeaaxspBody = ["clc", "adc sp", "pha", "txa", "adc sp+1", "tax", "pla"];

    // The caller branches on the flags tosicmp leaves - both exits keep them, since JMP {E} touches none.
    private static readonly string[] TosicmpBody =
        ["sta sreg", "stx sreg+1", "ldy #$00", "lda (sp),y", "tax", "inc sp", "bne {1}", "inc sp+1",
         "{1}: lda (sp),y", "inc sp", "bne {2}", "inc sp+1",
         "{2}: sec", "sbc sreg+1", "bne {4}", "cpx sreg", "beq {3}", "adc #$FF", "ora #$01",
         "{3}: jmp {E}", "{4}: bvc {3}", "eor #$FF", "ora #$01"];

    // staxspidx ends `jmp incsp2`, so incsp2's body follows. Its 65SC02 variant differs only before
    // `ldy tmp1`, which reloads Y and resets the flags.
    private static readonly string[] StaxspidxBody =
        ["sty tmp1", "pha", "ldy #1", "lda (sp),y", "sta ptr1+1", "dey", "lda (sp),y", "sta ptr1", "ldy tmp1",
         "iny", "txa", "sta (ptr1),y", "dey", "pla", "sta (ptr1),y", .. Incsp2Body];

    private static readonly string[] IncaxyBody = ["sty tmp1", "clc", "adc tmp1", "bcc {1}", "inx", "{1}:"];

    private static readonly string[] Aslax1Body = ["stx tmp1", "asl a", "rol tmp1", "ldx tmp1"];
    private static readonly string[] Aslax2Body = ["stx tmp1", "asl a", "rol tmp1", "asl a", "rol tmp1", "ldx tmp1"];

    private static string[] MulaxBody(int shifts) =>
        ["sta ptr1", "stx ptr1+1", .. Enumerable.Repeat<string[]>(["asl a", "rol ptr1+1"], shifts).SelectMany(s => s),
         "clc", "adc ptr1", "pha", "txa", "adc ptr1+1", "tax", "pla"];

    private static readonly string[] LdaxidxBody = ["sta ptr1", "stx ptr1+1", "lda (ptr1),y", "tax", "dey", "lda (ptr1),y"];

    // Both laddeq variants reach `pha` with Y=1 and the same carry, and TXA resets N/Z straight after.
    private static readonly string[] LaddeqBody =
        ["sty ptr1+1", "clc", "ldy #$00", "adc (ptr1),y", "sta (ptr1),y", "iny",
         "pha", "txa", "adc (ptr1),y", "sta (ptr1),y", "tax", "iny",
         "lda sreg", "adc (ptr1),y", "sta (ptr1),y", "sta sreg", "iny",
         "lda sreg+1", "adc (ptr1),y", "sta (ptr1),y", "sta sreg+1", "pla"];

    private static readonly string[] Sp = ["sp"];

    public static readonly IReadOnlyDictionary<string, RuntimeHelper> ByName = new[]
    {
        // pushax.s, pusha.s, pushwsp.s
        new RuntimeHelper("pushax", PushaxBody, 12, Sp),
        new RuntimeHelper("pusha0", ["ldx #0", .. PushaxBody], 12, Sp),
        new RuntimeHelper("push0", ["lda #0", "ldx #0", .. PushaxBody], 12, Sp),
        new RuntimeHelper("pusha", ["ldy sp", "beq {1}", "dec sp", "ldy #0", "sta (sp),y", "jmp {E}",
                                    "{1}: dec sp+1", "dec sp", "sta (sp),y"], 9, Sp),
        new RuntimeHelper("pushwysp", PushwyspBody, 12, Sp),
        new RuntimeHelper("pushw0sp", ["ldy #3", .. PushwyspBody], 12, Sp),

        // decsp1-8.s
        new RuntimeHelper("decsp1", ["ldy sp", "bne {1}", "dec sp+1", "{1}: dec sp"], 12, Sp),
        new RuntimeHelper("decsp2", DecspBody(2), 9, Sp),
        new RuntimeHelper("decsp3", DecspBody(3), 9, Sp),
        new RuntimeHelper("decsp4", DecspBody(4), 9, Sp),
        new RuntimeHelper("decsp5", DecspBody(5), 9, Sp),
        new RuntimeHelper("decsp6", DecspBody(6), 9, Sp),
        new RuntimeHelper("decsp7", DecspBody(7), 9, Sp),
        new RuntimeHelper("decsp8", DecspBody(8), 9, Sp),

        // incsp1.s, incsp2.s, addysp.s, incsp3-8.s (each `ldy #n / jmp addysp`)
        new RuntimeHelper("incsp1", ["inc sp", "bne {1}", "inc sp+1", "{1}:"], 12, Sp),
        new RuntimeHelper("incsp2", Incsp2Body, 9, Sp),
        new RuntimeHelper("addysp", AddyspBody, 12, Sp),
        new RuntimeHelper("addysp1", ["iny", .. AddyspBody], 12, Sp),
        new RuntimeHelper("incsp3", ["ldy #3", .. AddyspBody], 15, Sp),
        new RuntimeHelper("incsp4", ["ldy #4", .. AddyspBody], 15, Sp),
        new RuntimeHelper("incsp5", ["ldy #5", .. AddyspBody], 15, Sp),
        new RuntimeHelper("incsp6", ["ldy #6", .. AddyspBody], 15, Sp),
        new RuntimeHelper("incsp7", ["ldy #7", .. AddyspBody], 15, Sp),
        new RuntimeHelper("incsp8", ["ldy #8", .. AddyspBody], 15, Sp),

        // ldaxsp.s, staxsp.s, addeqsp.s, subeqsp.s, leaaxsp.s, staxspi.s
        new RuntimeHelper("ldaxysp", ["lda (sp),y", "tax", "dey", "lda (sp),y"], 12, Sp),
        new RuntimeHelper("ldax0sp", ["ldy #1", "lda (sp),y", "tax", "dey", "lda (sp),y"], 12, Sp),
        new RuntimeHelper("staxysp", ["sta (sp),y", "iny", "pha", "txa", "sta (sp),y", "pla"], 12, Sp),
        new RuntimeHelper("stax0sp", ["ldy #0", "sta (sp),y", "iny", "pha", "txa", "sta (sp),y", "pla"], 12, Sp),
        new RuntimeHelper("addeqysp", AddeqyspBody, 12, Sp),
        new RuntimeHelper("addeq0sp", ["ldy #0", .. AddeqyspBody], 12, Sp),
        new RuntimeHelper("subeqysp", SubeqyspBody, 12, Sp),
        new RuntimeHelper("subeq0sp", ["ldy #0", .. SubeqyspBody], 12, Sp),
        new RuntimeHelper("leaaxsp", LeaaxspBody, 12, Sp),
        new RuntimeHelper("leaa0sp", ["ldx #$00", .. LeaaxspBody], 12, Sp),
        new RuntimeHelper("staxspidx", StaxspidxBody, 12, ["sp", "tmp1", "ptr1"]),

        // icmp.s
        new RuntimeHelper("tosicmp", TosicmpBody, 9, ["sp", "sreg"]),
        new RuntimeHelper("tosicmp0", ["ldx #$00", .. TosicmpBody], 9, ["sp", "sreg"]),

        // incax2.s, incaxy.s (incax4 / incaxy), incax3/5-8.s (each `ldy #n / jmp incaxy`)
        new RuntimeHelper("incax2", ["clc", "adc #2", "bcc {1}", "inx", "{1}:"], 12, []),
        new RuntimeHelper("incaxy", IncaxyBody, 12, ["tmp1"]),
        new RuntimeHelper("incax3", ["ldy #3", .. IncaxyBody], 15, ["tmp1"]),
        new RuntimeHelper("incax4", ["ldy #4", .. IncaxyBody], 12, ["tmp1"]),
        new RuntimeHelper("incax5", ["ldy #5", .. IncaxyBody], 15, ["tmp1"]),
        new RuntimeHelper("incax6", ["ldy #6", .. IncaxyBody], 15, ["tmp1"]),
        new RuntimeHelper("incax7", ["ldy #7", .. IncaxyBody], 15, ["tmp1"]),
        new RuntimeHelper("incax8", ["ldy #8", .. IncaxyBody], 15, ["tmp1"]),

        // aslax1.s, aslax2.s, mulax3/5/9.s, ldaxi.s, laddeq.s
        new RuntimeHelper("aslax1", Aslax1Body, 12, ["tmp1"]),
        new RuntimeHelper("shlax1", Aslax1Body, 12, ["tmp1"]),
        new RuntimeHelper("aslax2", Aslax2Body, 12, ["tmp1"]),
        new RuntimeHelper("shlax2", Aslax2Body, 12, ["tmp1"]),
        new RuntimeHelper("mulax3", MulaxBody(1), 12, ["ptr1"]),
        new RuntimeHelper("mulax5", MulaxBody(2), 12, ["ptr1"]),
        new RuntimeHelper("mulax9", MulaxBody(3), 12, ["ptr1"]),
        new RuntimeHelper("ldaxidx", LdaxidxBody, 12, ["ptr1"]),
        new RuntimeHelper("ldaxi", ["ldy #1", .. LdaxidxBody], 12, ["ptr1"]),
        new RuntimeHelper("laddeq", LaddeqBody, 12, ["sreg", "ptr1"]),
        new RuntimeHelper("laddeqa", ["ldx #$00", "stx sreg", "stx sreg+1", .. LaddeqBody], 12, ["sreg", "ptr1"]),
        new RuntimeHelper("laddeq1", ["lda #$01", "ldx #$00", "stx sreg", "stx sreg+1", .. LaddeqBody], 12, ["sreg", "ptr1"]),
    }.ToDictionary(helper => helper.Name, StringComparer.Ordinal);
}
