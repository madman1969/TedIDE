/**
 * @file deadcode.c
 * @brief Dead code elimination pass implementation
 *
 * Tedide: upstream also deleted directives after a JMP/RTS (a .dbg line, or .byte data) and
 * stopped at the first blank line; it now removes only machine instructions, looks through blank,
 * comment and .dbg lines, and stops at any label or other directive.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"

/**
 * @brief Remove unlabelled instructions following an unconditional JMP, RTS or RTI
 *
 * Nothing can reach them: every entry point into code carries a label. Removal stops at the
 * next label, or at any directive (.segment, .proc, .endproc...) since it changes context.
 *
 * @param prog Program to optimize
 */
void optimize_dead_code_ast(Program *prog) {
    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead || !(op_is(node, "JMP") || op_is(node, "RTS") || op_is(node, "RTI"))) continue;

        for (AstNode *current = node->next; current; current = current->next) {
            if (current->is_dead || is_transparent(current)) continue;
            if (current->label || is_directive(current) || !is_removable(current)) break;
            remove_instruction(prog, current, OPT_KIND_UNREACHABLE);
        }
    }
}
