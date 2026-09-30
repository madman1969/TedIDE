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
 * target uses, or a 65C02 one. popax is deliberately absent: its variants leave Y different.
 *
 * Checked by tests/sim65: a stack-heavy program run in cc65's own simulator gives identical
 * output with and without this pass, for sim6502 and sim65c02 at every cc65 -O level.
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
 * cycles_saved is for the path that doesn't take any mid-body exit: the JSR+RTS pair (12), less
 * 3 where that path now runs a JMP {E} instead.
 */
typedef struct {
    const char *name;
    const char *const *lines;
    int cycles_saved;
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

static const RuntimeHelper HELPERS[] = {
    {"pushax", PUSHAX, 12},
    {"pusha0", PUSHA0, 12},
    {"push0", PUSH0, 12},
    {"pusha", PUSHA, 9},
    {"decsp2", DECSP2, 9},
    {"decsp4", DECSP4, 9},
    {"incsp2", INCSP2, 9},
    {"incsp1", INCSP1, 12},
    {"ldaxysp", LDAXYSP, 12},
    {"ldax0sp", LDAX0SP, 12},
    {"staxysp", STAXYSP, 12},
    {"stax0sp", STAX0SP, 12},
    {"pushwysp", PUSHWYSP, 12},
    {"pushw0sp", PUSHW0SP, 12},
};

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
 * Expands one helper after call (which is then marked dead - its label, if any, still prints
 * first; see write_output_ast). Returns false if memory ran out.
 */
static bool inline_call(Program *prog, AstNode *call, const RuntimeHelper *helper, int site) {
    bool uses_end = false;
    AstNode *after = call;
    char line[256];

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

    int site = 0;
    for (AstNode *node = prog->root; node; node = node->next) {
        if (!node->in_loop || node->is_dead || node->no_optimize || node->is_inlined ||
            !op_is(node, "JSR") || !node->operand) continue;

        const RuntimeHelper *helper = find_helper(node->operand);
        if (!helper) continue;

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
