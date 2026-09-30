/**
 * @file peephole.c
 * @brief Peephole optimization pass implementation
 *
 * Tedide: rewritten to be safe on cc65 output - see the comment on optimize_peephole_ast.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"

/**
 * @brief Remove the second load of LDA x / STA y / LDA x
 *
 * STA changes neither A nor the flags, so the reload is redundant. Only applied when:
 * - the STA and the second LDA have no label (nothing can jump in between with a different A)
 * - x is immediate, or a plain symbol: never indirect (the STA could be rewriting the pointer),
 *   and never a literal address (it may be an I/O register, where every read matters)
 * Blank, comment and .dbg lines between the three are looked through.
 *
 * @param prog Program to optimize
 */
void optimize_peephole_ast(Program *prog) {
    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead || node->no_optimize || !op_is(node, "LDA") || !node->operand) continue;

        const char *x = node->operand;
        if (x[0] != '#' && (operand_is_indirect_or_y_indexed(x) || x[0] == '$' || x[0] == '%' ||
                            (x[0] >= '0' && x[0] <= '9'))) continue;

        AstNode *store = next_significant(node);
        if (!store || store->label || store->no_optimize || !op_is(store, "STA")) continue;

        AstNode *reload = next_significant(store);
        if (!is_removable(reload) || !op_is(reload, "LDA") || !reload->operand ||
            strcmp(reload->operand, x) != 0) continue;

        remove_instruction(prog, reload, OPT_KIND_RELOAD);
    }
}
