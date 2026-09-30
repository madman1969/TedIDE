/**
 * @file loadstore.c
 * @brief Load/store optimization pass implementation
 *
 * Tedide: upstream's body here was an exact copy of optimize_peephole_ast (same LDA/STA/LDA
 * pattern), so every match was counted twice. The pattern now lives only in peephole.c.
 */

#include "optimizer.h"

void optimize_load_store_ast(Program *prog) {
    (void)prog;
}
