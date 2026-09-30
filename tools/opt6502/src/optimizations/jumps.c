/**
 * @file jumps.c
 * @brief Jump optimization pass implementation
 *
 * Tedide: upstream removed a JMP whenever the following line was *any* label, deleting e.g. a
 * loop's back-jump; it now requires the label to be the JMP's own target.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"

/**
 * @brief Remove a JMP to the label on the very next line (looking through blank, comment and
 * .dbg lines)
 *
 * @param prog Program to optimize
 */
void optimize_jumps_ast(Program *prog) {
    for (AstNode *node = prog->root; node; node = node->next) {
        if (!is_removable(node) || !op_is(node, "JMP") || !node->operand) continue;

        AstNode *next = next_significant(node);
        if (next && next->label && strcmp(next->label, node->operand) == 0) {
            remove_instruction(prog, node, OPT_KIND_JUMP);
        }
    }
}
