/**
 * @file inline_runtime.c
 * @brief Speed mode: inline cc65 runtime helper calls inside loops (Tedide addition)
 *
 * cc65 compiles most stack traffic to calls into its runtime library - `jsr pushax`, `jsr
 * ldaxysp`, `jsr incsp2` - because that keeps code small. Each call costs a JSR and an RTS (12
 * cycles) on top of the helper's own work. In -speed mode, calls inside loops are replaced with
 * the helper's body.
 *
 * Correctness rests on the bodies below being cc65's own code: they are copied from cc65's
 * libsrc/runtime at git b75f872 (cc65 2.19, the version the prebuilt libraries of that release
 * run). The only changes are mechanical: a final RTS is dropped, an RTS elsewhere becomes a JMP
 * to the end of the inlined block, `sub #2` (macpack generic) is spelled out as SEC / SBC, and
 * local labels are renamed. So every path leaves A, X, Y, the flags and memory exactly as the
 * real helper does - only the hardware stack no longer holds a return address, which none of
 * these helpers look at.
 *
 * Where a helper has a 65SC02 variant (`.if .cpu`), the 6502 one is used; for every helper here
 * both variants leave identical state (pushax's final `sta (sp)` vs `sta (sp),y` runs with Y
 * already 0), so it's right whichever library the program links - the 6502 one every Commodore
 * target uses, or a 65C02 one. Helpers whose variants differ are deliberately absent: popax,
 * tosaddax and pusheax (Y), incax1 (carry).
 *
 * Checked by tests/sim65 (run_sim65_tests.sh), against both the 6502 and 65C02 libraries:
 * stress.c and helpers.c give identical output with and without this pass at every cc65 -O level,
 * and regs.s (from gen_regs.py) calls every helper below 256 times with varied registers, sp and
 * memory and checks the inlined copies leave exactly the same registers, flags and memory.
 *
 * Because the bodies are version-specific, this only runs on cc65 2.19 output that imports the
 * zero-page `sp` (later cc65 renamed it c_sp) and includes `.macpack longbranch` (needed to fix up
 * branch ranges - see fix_branch_ranges).
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"
#include "../ast/ast.h"
#include "../ast/parser.h"
#include <stdio.h>
#include <stdlib.h>
#include <ctype.h>

/*
 * One inlinable helper. Lines are ca65 statements; "{1}"/"{2}" are the body's own local labels
 * and "{E}" the label placed after the block (only emitted when something jumps to it).
 * cycles_saved is for the path that doesn't take any mid-body exit: the JSR+RTS pair (12), plus 3
 * for helpers that were a `ldy #n / jmp other` stub, less 3 where that path now runs a JMP {E}.
 * zp names the zero-page symbols the body uses; a call is only inlined in a file that imports all
 * of them.
 */
typedef struct {
    const char *name;
    const char *const *lines;
    int cycles_saved;
    const char *zp;
} RuntimeHelper;

/* pushax.s: push0 / pusha0 / pushax */
static const char *const PUSHAX[] = {
    "pha", "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
    "{1}: ldy #1", "txa", "sta (sp),y", "pla", "dey", "sta (sp),y", NULL};
static const char *const PUSHA0[] = {
    "ldx #0",
    "pha", "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
    "{1}: ldy #1", "txa", "sta (sp),y", "pla", "dey", "sta (sp),y", NULL};
static const char *const PUSH0[] = {
    "lda #0", "ldx #0",
    "pha", "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
    "{1}: ldy #1", "txa", "sta (sp),y", "pla", "dey", "sta (sp),y", NULL};

/* pusha.s: pusha */
static const char *const PUSHA[] = {
    "ldy sp", "beq {1}", "dec sp", "ldy #0", "sta (sp),y", "jmp {E}",
    "{1}: dec sp+1", "dec sp", "sta (sp),y", NULL};

/* decsp2.s, decsp4.s */
static const char *const DECSP2[] = {
    "lda sp", "sec", "sbc #2", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};
static const char *const DECSP4[] = {
    "lda sp", "sec", "sbc #4", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};

/* incsp2.s: incsp2 */
static const char *const INCSP2[] = {
    "inc sp", "beq {1}", "inc sp", "beq {2}", "jmp {E}",
    "{1}: inc sp", "{2}: inc sp+1", NULL};

/* incsp1.s */
static const char *const INCSP1[] = {"inc sp", "bne {1}", "inc sp+1", "{1}:", NULL};

/* ldaxsp.s: ldax0sp / ldaxysp */
static const char *const LDAXYSP[] = {"lda (sp),y", "tax", "dey", "lda (sp),y", NULL};
static const char *const LDAX0SP[] = {"ldy #1", "lda (sp),y", "tax", "dey", "lda (sp),y", NULL};

/* staxsp.s: stax0sp / staxysp */
static const char *const STAXYSP[] = {"sta (sp),y", "iny", "pha", "txa", "sta (sp),y", "pla", NULL};
static const char *const STAX0SP[] = {"ldy #0", "sta (sp),y", "iny", "pha", "txa", "sta (sp),y", "pla", NULL};

/* pushwsp.s: pushw0sp / pushwysp */
static const char *const PUSHWYSP[] = {
    "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
    "{1}: lda (sp),y", "tax", "dey", "lda (sp),y", "ldy #$00", "sta (sp),y", "iny", "txa", "sta (sp),y", NULL};
static const char *const PUSHW0SP[] = {
    "ldy #3",
    "lda sp", "sec", "sbc #2", "sta sp", "bcs {1}", "dec sp+1",
    "{1}: lda (sp),y", "tax", "dey", "lda (sp),y", "ldy #$00", "sta (sp),y", "iny", "txa", "sta (sp),y", NULL};

/* decsp1.s, decsp3.s, decsp5-8.s (decsp3/5-8 are decsp2 with a different constant) */
static const char *const DECSP1[] = {"ldy sp", "bne {1}", "dec sp+1", "{1}: dec sp", NULL};
static const char *const DECSP3[] = {
    "lda sp", "sec", "sbc #3", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};
static const char *const DECSP5[] = {
    "lda sp", "sec", "sbc #5", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};
static const char *const DECSP6[] = {
    "lda sp", "sec", "sbc #6", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};
static const char *const DECSP7[] = {
    "lda sp", "sec", "sbc #7", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};
static const char *const DECSP8[] = {
    "lda sp", "sec", "sbc #8", "sta sp", "bcc {1}", "jmp {E}", "{1}: dec sp+1", NULL};

/* addysp.s: addysp1 / addysp; incsp3-8.s are `ldy #n / jmp addysp` */
#define ADDYSP_BODY "pha", "clc", "tya", "adc sp", "sta sp", "bcc {1}", "inc sp+1", "{1}: pla"
static const char *const ADDYSP[] = {ADDYSP_BODY, NULL};
static const char *const ADDYSP1[] = {"iny", ADDYSP_BODY, NULL};
static const char *const INCSP3[] = {"ldy #3", ADDYSP_BODY, NULL};
static const char *const INCSP4[] = {"ldy #4", ADDYSP_BODY, NULL};
static const char *const INCSP5[] = {"ldy #5", ADDYSP_BODY, NULL};
static const char *const INCSP6[] = {"ldy #6", ADDYSP_BODY, NULL};
static const char *const INCSP7[] = {"ldy #7", ADDYSP_BODY, NULL};
static const char *const INCSP8[] = {"ldy #8", ADDYSP_BODY, NULL};

/* addeqsp.s: addeq0sp / addeqysp */
#define ADDEQYSP_BODY "clc", "adc (sp),y", "sta (sp),y", "pha", "iny", "txa", "adc (sp),y", "sta (sp),y", "tax", "pla"
static const char *const ADDEQYSP[] = {ADDEQYSP_BODY, NULL};
static const char *const ADDEQ0SP[] = {"ldy #0", ADDEQYSP_BODY, NULL};

/* subeqsp.s: subeq0sp / subeqysp */
#define SUBEQYSP_BODY "sec", "eor #$FF", "adc (sp),y", "sta (sp),y", "pha", "iny", "txa", "eor #$FF", \
                      "adc (sp),y", "sta (sp),y", "tax", "pla"
static const char *const SUBEQYSP[] = {SUBEQYSP_BODY, NULL};
static const char *const SUBEQ0SP[] = {"ldy #0", SUBEQYSP_BODY, NULL};

/* leaaxsp.s: leaa0sp / leaaxsp */
#define LEAAXSP_BODY "clc", "adc sp", "pha", "txa", "adc sp+1", "tax", "pla"
static const char *const LEAAXSP[] = {LEAAXSP_BODY, NULL};
static const char *const LEAA0SP[] = {"ldx #$00", LEAAXSP_BODY, NULL};

/* icmp.s: tosicmp0 / tosicmp - the caller branches on the flags it leaves */
#define TOSICMP_BODY "sta sreg", "stx sreg+1", "ldy #$00", "lda (sp),y", "tax", "inc sp", "bne {1}", "inc sp+1", \
                     "{1}: lda (sp),y", "inc sp", "bne {2}", "inc sp+1", \
                     "{2}: sec", "sbc sreg+1", "bne {4}", "cpx sreg", "beq {3}", "adc #$FF", "ora #$01", \
                     "{3}: jmp {E}", "{4}: bvc {3}", "eor #$FF", "ora #$01"
static const char *const TOSICMP[] = {TOSICMP_BODY, NULL};
static const char *const TOSICMP0[] = {"ldx #$00", TOSICMP_BODY, NULL};

/* staxspi.s: staxspidx, which ends `jmp incsp2` - incsp2.s's body follows in its place. Its
   65SC02 variant differs only before `ldy tmp1`, which reloads Y and resets the flags. */
static const char *const STAXSPIDX[] = {
    "sty tmp1", "pha", "ldy #1", "lda (sp),y", "sta ptr1+1", "dey", "lda (sp),y", "sta ptr1", "ldy tmp1",
    "iny", "txa", "sta (ptr1),y", "dey", "pla", "sta (ptr1),y",
    "inc sp", "beq {1}", "inc sp", "beq {2}", "jmp {E}", "{1}: inc sp", "{2}: inc sp+1", NULL};

/* incax2.s (`add #2` from macpack generic spelled out); incaxy.s: incax4 / incaxy; incax3/5-8.s
   are `ldy #n / jmp incaxy`. incax1 is absent: its 65SC02 variant (INA) leaves carry differently. */
static const char *const INCAX2[] = {"clc", "adc #2", "bcc {1}", "inx", "{1}:", NULL};
#define INCAXY_BODY "sty tmp1", "clc", "adc tmp1", "bcc {1}", "inx", "{1}:"
static const char *const INCAXY[] = {INCAXY_BODY, NULL};
static const char *const INCAX3[] = {"ldy #3", INCAXY_BODY, NULL};
static const char *const INCAX4[] = {"ldy #4", INCAXY_BODY, NULL};
static const char *const INCAX5[] = {"ldy #5", INCAXY_BODY, NULL};
static const char *const INCAX6[] = {"ldy #6", INCAXY_BODY, NULL};
static const char *const INCAX7[] = {"ldy #7", INCAXY_BODY, NULL};
static const char *const INCAX8[] = {"ldy #8", INCAXY_BODY, NULL};

/* aslax1.s: aslax1 / shlax1; aslax2.s: aslax2 / shlax2 */
static const char *const ASLAX1[] = {"stx tmp1", "asl a", "rol tmp1", "ldx tmp1", NULL};
static const char *const ASLAX2[] = {"stx tmp1", "asl a", "rol tmp1", "asl a", "rol tmp1", "ldx tmp1", NULL};

/* mulax3.s, mulax5.s, mulax9.s */
static const char *const MULAX3[] = {
    "sta ptr1", "stx ptr1+1", "asl a", "rol ptr1+1",
    "clc", "adc ptr1", "pha", "txa", "adc ptr1+1", "tax", "pla", NULL};
static const char *const MULAX5[] = {
    "sta ptr1", "stx ptr1+1", "asl a", "rol ptr1+1", "asl a", "rol ptr1+1",
    "clc", "adc ptr1", "pha", "txa", "adc ptr1+1", "tax", "pla", NULL};
static const char *const MULAX9[] = {
    "sta ptr1", "stx ptr1+1", "asl a", "rol ptr1+1", "asl a", "rol ptr1+1", "asl a", "rol ptr1+1",
    "clc", "adc ptr1", "pha", "txa", "adc ptr1+1", "tax", "pla", NULL};

/* ldaxi.s: ldaxi / ldaxidx */
#define LDAXIDX_BODY "sta ptr1", "stx ptr1+1", "lda (ptr1),y", "tax", "dey", "lda (ptr1),y"
static const char *const LDAXIDX[] = {LDAXIDX_BODY, NULL};
static const char *const LDAXI[] = {"ldy #1", LDAXIDX_BODY, NULL};

/* laddeq.s: laddeq1 / laddeqa / laddeq. The two variants both reach `pha` with Y=1 and the same
   carry, and TXA resets N/Z straight after. */
#define LADDEQ_BODY "sty ptr1+1", "clc", "ldy #$00", "adc (ptr1),y", "sta (ptr1),y", "iny", \
                    "pha", "txa", "adc (ptr1),y", "sta (ptr1),y", "tax", "iny", \
                    "lda sreg", "adc (ptr1),y", "sta (ptr1),y", "sta sreg", "iny", \
                    "lda sreg+1", "adc (ptr1),y", "sta (ptr1),y", "sta sreg+1", "pla"
static const char *const LADDEQ[] = {LADDEQ_BODY, NULL};
static const char *const LADDEQA[] = {"ldx #$00", "stx sreg", "stx sreg+1", LADDEQ_BODY, NULL};
static const char *const LADDEQ1[] = {"lda #$01", "ldx #$00", "stx sreg", "stx sreg+1", LADDEQ_BODY, NULL};

static const RuntimeHelper HELPERS[] = {
    {"pushax", PUSHAX, 12, "sp"},
    {"pusha0", PUSHA0, 12, "sp"},
    {"push0", PUSH0, 12, "sp"},
    {"pusha", PUSHA, 9, "sp"},
    {"decsp1", DECSP1, 12, "sp"},
    {"decsp2", DECSP2, 9, "sp"},
    {"decsp3", DECSP3, 9, "sp"},
    {"decsp4", DECSP4, 9, "sp"},
    {"decsp5", DECSP5, 9, "sp"},
    {"decsp6", DECSP6, 9, "sp"},
    {"decsp7", DECSP7, 9, "sp"},
    {"decsp8", DECSP8, 9, "sp"},
    {"incsp1", INCSP1, 12, "sp"},
    {"incsp2", INCSP2, 9, "sp"},
    {"addysp", ADDYSP, 12, "sp"},
    {"addysp1", ADDYSP1, 12, "sp"},
    {"incsp3", INCSP3, 15, "sp"},
    {"incsp4", INCSP4, 15, "sp"},
    {"incsp5", INCSP5, 15, "sp"},
    {"incsp6", INCSP6, 15, "sp"},
    {"incsp7", INCSP7, 15, "sp"},
    {"incsp8", INCSP8, 15, "sp"},
    {"ldaxysp", LDAXYSP, 12, "sp"},
    {"ldax0sp", LDAX0SP, 12, "sp"},
    {"staxysp", STAXYSP, 12, "sp"},
    {"stax0sp", STAX0SP, 12, "sp"},
    {"pushwysp", PUSHWYSP, 12, "sp"},
    {"pushw0sp", PUSHW0SP, 12, "sp"},
    {"addeqysp", ADDEQYSP, 12, "sp"},
    {"addeq0sp", ADDEQ0SP, 12, "sp"},
    {"subeqysp", SUBEQYSP, 12, "sp"},
    {"subeq0sp", SUBEQ0SP, 12, "sp"},
    {"leaaxsp", LEAAXSP, 12, "sp"},
    {"leaa0sp", LEAA0SP, 12, "sp"},
    {"tosicmp", TOSICMP, 9, "sp sreg"},
    {"tosicmp0", TOSICMP0, 9, "sp sreg"},
    {"staxspidx", STAXSPIDX, 12, "sp tmp1 ptr1"},
    {"incax2", INCAX2, 12, ""},
    {"incaxy", INCAXY, 12, "tmp1"},
    {"incax3", INCAX3, 15, "tmp1"},
    {"incax4", INCAX4, 12, "tmp1"},
    {"incax5", INCAX5, 15, "tmp1"},
    {"incax6", INCAX6, 15, "tmp1"},
    {"incax7", INCAX7, 15, "tmp1"},
    {"incax8", INCAX8, 15, "tmp1"},
    {"aslax1", ASLAX1, 12, "tmp1"},
    {"shlax1", ASLAX1, 12, "tmp1"},
    {"aslax2", ASLAX2, 12, "tmp1"},
    {"shlax2", ASLAX2, 12, "tmp1"},
    {"mulax3", MULAX3, 12, "ptr1"},
    {"mulax5", MULAX5, 12, "ptr1"},
    {"mulax9", MULAX9, 12, "ptr1"},
    {"ldaxidx", LDAXIDX, 12, "ptr1"},
    {"ldaxi", LDAXI, 12, "ptr1"},
    {"laddeq", LADDEQ, 12, "sreg ptr1"},
    {"laddeqa", LADDEQA, 12, "sreg ptr1"},
    {"laddeq1", LADDEQ1, 12, "sreg ptr1"},
};

static bool is_zp_symbol(const Program *prog, const char *name);

/* True if the file imports every zero-page symbol in the space-separated list names. */
static bool has_zp_symbols(const Program *prog, const char *names) {
    char name[32];
    while (*names) {
        while (*names == ' ') names++;
        size_t len = strcspn(names, " ");
        if (len == 0) break;
        if (len >= sizeof(name)) return false;
        memcpy(name, names, len);
        name[len] = '\0';
        if (!is_zp_symbol(prog, name)) return false;
        names += len;
    }
    return true;
}

static const RuntimeHelper *find_helper(const char *name) {
    for (size_t i = 0; i < sizeof(HELPERS) / sizeof(HELPERS[0]); i++) {
        if (strcmp(HELPERS[i].name, name) == 0) return &HELPERS[i];
    }
    return NULL;
}

/* True if some node's source line contains text (case-sensitive). */
static bool source_contains(const Program *prog, const char *text) {
    for (const AstNode *node = prog->root; node; node = node->next) {
        if (node->source && strstr(node->source, text)) return true;
    }
    return false;
}

static bool is_zp_symbol(const Program *prog, const char *name) {
    for (int i = 0; i < prog->zp_symbol_count; i++) {
        if (strcmp(prog->zp_symbols[i], name) == 0) return true;
    }
    return false;
}

/* JMP, a conditional branch, or a cc65 longbranch macro (jeq, jne...). */
static bool is_jump_or_branch(const AstNode *node) {
    static const char *const OPS[] = {
        "JMP", "BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA",
        "JCC", "JCS", "JEQ", "JNE", "JMI", "JPL", "JVC", "JVS", NULL};
    for (const char *const *op = OPS; *op; op++) {
        if (op_is(node, *op)) return true;
    }
    return false;
}

/*
 * Sets in_loop on every node inside a loop: from a label to the last JMP/branch back to it,
 * within the same function.
 */
static void mark_loops(Program *prog) {
    for (AstNode *start = prog->root; start; start = start->next) {
        if (start->is_dead || !start->label || start->label[0] == '.') continue;

        AstNode *last_back_edge = NULL;
        for (AstNode *node = start->next; node && !is_proc_boundary(node, ".endproc"); node = node->next) {
            if (!node->is_dead && is_jump_or_branch(node) && node->operand &&
                strcmp(node->operand, start->label) == 0) {
                last_back_edge = node;
            }
        }
        if (!last_back_edge) continue;
        for (AstNode *node = start; ; node = node->next) {
            node->in_loop = true;
            if (node == last_back_edge) break;
        }
    }
}

static AstNode *make_node(Program *prog, const char *line, int line_num) {
    AstNode *node = create_ast_node(NODE_ASM_LINE, line_num);
    parse_line_ast(node, line, line_num, &prog->config);
    node->is_inlined = true;
    return node;
}

/*
 * Expands one helper after call, which is then marked dead. Returns false if memory ran out.
 *
 * A label on the call (often a loop's own label - `L0005: jsr decsp5`) is moved onto a live
 * label-only line ahead of the body, never left on the dead call: the other passes look straight
 * through dead lines, so a label left there would be invisible to them. The dead-code pass would
 * then delete the start of the body as unreachable after a preceding JMP, and the constant pass
 * could carry a register value across what is really a branch target.
 */
static bool inline_call(Program *prog, AstNode *call, const RuntimeHelper *helper, int site) {
    bool uses_end = false;
    AstNode *after = call;
    char line[256];

    if (call->label) {
        snprintf(line, sizeof(line), "%s%s", call->label, prog->config.supports_colon_labels ? ":" : "");
        AstNode *label = make_node(prog, line, call->line_num);
        if (!label) return false;
        label->next = call->next;
        call->next = label;
        after = label;
        free(call->label);
        call->label = NULL;
    }

    for (const char *const *text = helper->lines; *text; text++) {
        // Rewrite "{1}", "{2}", "{E}" into this site's unique cheap-local labels, and indent
        // unlabelled statements so they parse as instructions.
        char *out = line;
        const char *in = *text;
        if (in[0] != '{') *out++ = '\t';
        while (*in && out < line + sizeof(line) - 24) {
            if (in[0] == '{' && in[2] == '}') {
                if (in[1] == 'E') uses_end = true;
                out += sprintf(out, "@opt6502_%d_%c", site, in[1]);
                in += 3;
            } else {
                *out++ = *in++;
            }
        }
        *out = '\0';

        AstNode *node = make_node(prog, line, call->line_num);
        if (!node) return false;
        node->next = after->next;
        after->next = node;
        after = node;
    }

    if (uses_end) {
        snprintf(line, sizeof(line), "@opt6502_%d_E:", site);
        AstNode *node = make_node(prog, line, call->line_num);
        if (!node) return false;
        node->next = after->next;
        after->next = node;
    }

    call->is_dead = true;
    return true;
}

/* Worst-case size in bytes of one live node, or -1 if it can't be bounded. */
static int max_size(const AstNode *node) {
    static const char *const LONG_BRANCHES[] = {"JCC", "JCS", "JEQ", "JNE", "JMI", "JPL", "JVC", "JVS", NULL};
    if (node->is_dead || is_transparent(node)) return 0;
    if (is_directive(node)) return -1;
    if (!node->opcode || node->opcode[0] == '\0') return 0;  // label-only line
    for (const char *const *op = LONG_BRANCHES; *op; op++) {
        if (op_is(node, *op)) return 5;
    }
    if (!node->operand || node->operand[0] == '\0' || strcasecmp(node->operand, "a") == 0) return 1;
    if (node->operand[0] == '#') return 2;
    return 3;
}

/*
 * Inlining grows code, which can push a short conditional branch past its -128..+127 byte reach.
 * Every short branch whose span contains inlined code is checked against a worst-case size
 * (3 bytes for any memory operand, 5 for a longbranch macro); if that could be out of range it
 * becomes the matching cc65 longbranch macro (beq -> jeq), which ca65 assembles as a short branch
 * when the target is known to be close and a branch-around-JMP otherwise - always correct. A BRA
 * becomes a JMP.
 */
static void fix_branch_ranges(Program *prog) {
    static const char *const SHORT[] = {"BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA", NULL};

    AstNode *proc_start = prog->root;
    for (AstNode *branch = prog->root; branch; branch = branch->next) {
        if (is_proc_boundary(branch, ".proc")) proc_start = branch;
        if (branch->is_dead || !branch->operand) continue;
        bool is_short = false;
        for (const char *const *op = SHORT; *op; op++) {
            if (op_is(branch, *op)) is_short = true;
        }
        if (!is_short) continue;

        bool found = false, spans_inlined = false, unbounded = false;
        int bytes = 0, limit = 127;

        // Forward target: bytes strictly between the branch and the label.
        for (AstNode *node = branch->next; node && !is_proc_boundary(node, ".endproc"); node = node->next) {
            if (node->label && strcmp(node->label, branch->operand) == 0) {
                found = true;
                break;
            }
            int size = max_size(node);
            if (size < 0) unbounded = true;
            else bytes += size;
            if (node->is_inlined && !node->is_dead) spans_inlined = true;
        }

        if (!found) {
            // Backward target in the same function: bytes from the label through the branch.
            AstNode *target = NULL;
            for (AstNode *node = proc_start; node && node != branch; node = node->next) {
                if (node->label && strcmp(node->label, branch->operand) == 0) target = node;
            }
            if (!target) continue;  // not a label in this function - leave it to ca65
            spans_inlined = unbounded = false;
            bytes = 0;
            limit = 128;
            for (AstNode *node = target; ; node = node->next) {
                int size = max_size(node);
                if (size < 0) unbounded = true;
                else bytes += size;
                if (node->is_inlined && !node->is_dead) spans_inlined = true;
                if (node == branch) break;
            }
        }

        if (!spans_inlined || (!unbounded && bytes <= limit)) continue;

        // BRA (65C02 - cc65 emits it for --cpu 65c02/65816) has no longbranch macro, but JMP is an
        // exact substitute: unconditional, touches no flags, and 3 cycles like a taken BRA.
        char *long_form = malloc(4);
        if (!long_form) continue;
        bool lower = islower((unsigned char)branch->opcode[0]);
        if (op_is(branch, "BRA")) {
            strcpy(long_form, lower ? "jmp" : "JMP");
        } else {
            long_form[0] = lower ? 'j' : 'J';
            long_form[1] = branch->opcode[1];
            long_form[2] = branch->opcode[2];
            long_form[3] = '\0';
        }
        free(branch->opcode);
        branch->opcode = long_form;
        prog->instructions_rewritten++;
    }
}

/**
 * @brief -speed: replace calls to cc65 runtime helpers inside loops with their bodies
 *
 * Runs once, before the other passes, so they can tidy up around the inlined code. Adds bytes
 * (reported as negative bytes saved) to save 9-12 cycles per call on every trip round the loop.
 *
 * @param prog Program to optimize
 */
void optimize_inline_runtime_ast(Program *prog) {
    if (prog->mode != OPT_SPEED || prog->config.type != ASM_CA65) return;
    bool has_longbranch = source_contains(prog, ".macpack\tlongbranch") || source_contains(prog, ".macpack longbranch");
    if (!source_contains(prog, "compiler,\"cc65 v 2.19") || !is_zp_symbol(prog, "sp") || !has_longbranch) return;

    mark_loops(prog);

    // Debugging aid: OPT6502_INLINE_LIMIT=n inlines only the first n call sites, so a failing
    // program can be bisected down to the one site that breaks it.
    const char *limit_text = getenv("OPT6502_INLINE_LIMIT");
    int limit = limit_text ? atoi(limit_text) : -1;

    int site = 0;
    for (AstNode *node = prog->root; node; node = node->next) {
        if (!node->in_loop || node->is_dead || node->no_optimize || node->is_inlined ||
            !op_is(node, "JSR") || !node->operand) continue;

        const RuntimeHelper *helper = find_helper(node->operand);
        if (!helper || !has_zp_symbols(prog, helper->zp)) continue;
        if (limit >= 0 && site >= limit) break;

        int call_bytes, call_cycles;
        estimate_cost(prog, node, &call_bytes, &call_cycles);
        if (!inline_call(prog, node, helper, ++site)) break;

        int body_bytes = 0;
        for (AstNode *added = node->next; added && added->is_inlined; added = added->next) {
            if (added->opcode) {
                int bytes, cycles;
                estimate_cost(prog, added, &bytes, &cycles);
                body_bytes += bytes;
            }
        }

        prog->optimizations++;
        prog->stats[OPT_KIND_INLINE]++;
        prog->instructions_removed++;
        prog->bytes_saved += call_bytes - body_bytes;  // negative: inlining trades size for speed
        prog->cycles_saved += helper->cycles_saved;
    }

    if (site > 0) fix_branch_ranges(prog);
}
