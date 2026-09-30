/**
 * @file regusage.c
 * @brief Register usage optimization pass implementation
 *
 * Tedide: upstream removed *both* halves of TAX / TXA, losing the value TAX put in X.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"

/**
 * @brief Remove the TXA of TAX / TXA (and TYA of TAY / TYA)
 *
 * After TAX, A and X already hold the same value and N/Z reflect it, so transferring it back
 * changes nothing. The TAX itself stays - X is still needed.
 *
 * @param prog Program to optimize
 */
void optimize_register_usage_ast(Program *prog) {
    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead || node->no_optimize) continue;

        const char *back = op_is(node, "TAX") ? "TXA" : op_is(node, "TAY") ? "TYA" : NULL;
        if (!back) continue;

        AstNode *next = next_significant(node);
        if (is_removable(next) && op_is(next, back)) {
            remove_instruction(prog, next, OPT_KIND_TRANSFER);
        }
    }
}
