/**
 * @file constant.c
 * @brief Constant propagation pass implementation
 *
 * Tedide: upstream kept tracking A's value across JSR (which clobbers it) and across
 * instructions that change the N/Z flags the removed load would have set, and compared only the
 * first 63 characters of operands.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"

/**
 * @brief Remove a reload of an immediate value a register already holds
 *
 * After LDA/LDX/LDY #v, a later load of the same register with the same #v is redundant as long
 * as everything in between is straight-line code that changes neither A/X/Y nor the N/Z flags
 * (stores, flag set/clear, pushes, NOP - see preserves_a_and_nz). Any label, branch, jump, call,
 * return or directive other than .dbg ends the search.
 *
 * @param prog Program to optimize
 */
void optimize_constant_propagation_ast(Program *prog) {
    static const char *const LOADS[] = {"LDA", "LDX", "LDY", NULL};

    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead || node->no_optimize || !node->operand || node->operand[0] != '#') continue;

        const char *load = NULL;
        for (const char *const *l = LOADS; *l; l++) {
            if (op_is(node, *l)) load = *l;
        }
        if (!load) continue;

        for (AstNode *current = next_significant(node); current; current = next_significant(current)) {
            if (op_is(current, load) && is_removable(current) && current->operand &&
                strcmp(current->operand, node->operand) == 0) {
                remove_instruction(prog, current, OPT_KIND_CONSTANT);
                continue;
            }
            if (ends_basic_block(current) || !preserves_a_and_nz(current)) break;
        }
    }
}
