/**
 * @file jumps.c
 * @brief Jump optimization pass implementation
 *
 * Tedide: upstream removed a JMP whenever the following line was *any* label, deleting e.g. a
 * loop's back-jump; it now requires the label to be the JMP's own target. Jump threading (the
 * "branch chaining" upstream's README described but never implemented) was added too.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"
#include <ctype.h>
#include <stdlib.h>

static char *copy_string(const char *s) {
    size_t len = strlen(s);
    char *copy = malloc(len + 1);
    if (copy) memcpy(copy, s, len + 1);
    return copy;
}

/* A JMP to a plain label - not JMP (indirect), not JMP *+3 or other expressions. */
static bool is_direct_jmp(const AstNode *node) {
    if (!op_is(node, "JMP") || !node->operand || node->operand[0] == '\0') return false;
    for (const char *p = node->operand; *p; p++) {
        if (!isalnum((unsigned char)*p) && *p != '_' && *p != '@' && *p != '.') return false;
    }
    return true;
}

/**
 * @brief Thread one JMP through the jumps and returns it lands on
 *
 * - `JMP L1` where L1's first instruction is `JMP L2` (or 65C02 `BRA L2`) becomes `JMP L2`,
 *   following chains of up to 16 hops. A chain that cycles is left alone.
 * - `JMP L` where L's first instruction is `RTS` becomes `RTS`: same effect, 2 bytes and 3 cycles
 *   less.
 *
 * Only JMP itself is rewritten: its size never changes (or shrinks, for RTS), so no short branch
 * elsewhere can be pushed out of range. Conditional branches and cc65's jeq/jne macros are left
 * alone for exactly that reason. The labels jumped past stay, since other code may use them.
 * Nothing is threaded into a #NOOPT region, which may be timing-critical.
 */
static void thread_jump(Program *prog, AstNode *jmp) {
    enum { MAX_HOPS = 16 };
    const char *visited[MAX_HOPS + 1];
    const char *target = jmp->operand;
    int hops = 0;
    visited[0] = target;

    for (; hops < MAX_HOPS; hops++) {
        AstNode *label = find_label(prog, jmp, target);
        if (!label) break;  // imported symbol (e.g. a runtime helper) - nothing to look through
        AstNode *landing = instruction_at(label);
        if (landing == jmp) return;  // the chain cycles back through this JMP - leave it alone
        if (!landing || landing->no_optimize) break;

        if (op_is(landing, "RTS") && !landing->operand) {
            char *rts = copy_string(islower((unsigned char)jmp->opcode[0]) ? "rts" : "RTS");
            if (!rts) return;
            free(jmp->opcode);
            jmp->opcode = rts;
            free(jmp->operand);
            jmp->operand = NULL;
            record_rewrite(prog, OPT_KIND_THREAD, 2, 3);
            return;
        }

        // A BRA in the file is unconditional whatever -cpu says: cc65 only emits it for a CPU
        // that has it.
        bool onward = is_direct_jmp(landing) || (op_is(landing, "BRA") && landing->operand);
        if (!onward) break;
        // A chain that loops back on itself is an intentional infinite loop (e.g. for (;;);) -
        // leave the JMP alone rather than rewrite it round the cycle on every pass.
        for (int i = 0; i <= hops; i++) {
            if (strcmp(visited[i], landing->operand) == 0) return;
        }
        target = landing->operand;
        visited[hops + 1] = target;
    }

    if (target == jmp->operand) return;
    char *retarget = copy_string(target);
    if (!retarget) return;
    free(jmp->operand);
    jmp->operand = retarget;
    record_rewrite(prog, OPT_KIND_THREAD, 0, 3 * hops);
}

/** Removes jmp if it's a JMP to the label on the very next line (looking through blank, comment
 *  and .dbg lines); returns whether it did. */
static bool remove_jump_to_next(Program *prog, AstNode *jmp) {
    if (!is_removable(jmp) || !is_direct_jmp(jmp)) return false;
    AstNode *next = next_significant(jmp);
    if (!next || !next->label || strcmp(next->label, jmp->operand) != 0) return false;
    remove_instruction(prog, jmp, OPT_KIND_JUMP);
    return true;
}

/**
 * @brief Remove JMPs to the very next line, and thread the rest through the jumps/returns they
 * land on (see thread_jump)
 *
 * @param prog Program to optimize
 */
void optimize_jumps_ast(Program *prog) {
    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead || node->no_optimize || !is_direct_jmp(node)) continue;

        // Removing a jump to the next line beats threading it (which could at best turn it into
        // an RTS), so that's checked first - and again after threading, which may have retargeted
        // it to the next line.
        if (!remove_jump_to_next(prog, node)) {
            thread_jump(prog, node);
            remove_jump_to_next(prog, node);
        }
    }
}
