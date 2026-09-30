/**
 * @file cpu65c02.c
 * @brief 65C02-specific optimizations
 *
 * Tedide: rewritten. Upstream converted every STA after LDA #0 to STZ - including (zp),y and
 * abs,y stores, which STZ can't encode, so ca65 rejected the result - and deleted the LDA #0
 * even when a following JSR/RTS handed A on, or a branch tested the Z flag it set.
 */

#include "optimizer.h"
#include "../analysis/nodeinfo.h"
#include <stdio.h>
#include <stdlib.h>
#include <ctype.h>

static bool is_zero_immediate(const char *operand) {
    return operand && (strcmp(operand, "#0") == 0 || strcmp(operand, "#$0") == 0 ||
                       strcmp(operand, "#$00") == 0);
}

/**
 * @brief LDA #0 / STA a / STA b ... -> STZ a / STZ b ..., removing the LDA
 *
 * Applied only when the zero in A is provably never read afterwards: scanning forward through
 * straight-line code, every STA must use an addressing mode STZ has (zp, zp,x, abs, abs,x), and
 * A must then be overwritten (LDA/PLA/TXA/TYA) before anything reads it - with the N/Z flags
 * the LDA set also overwritten before anything tests them. Any label, branch, jump, call or
 * return first means A or the flags may still be needed, so nothing changes.
 *
 * Also raises the file's `.setcpu "6502"` to "65C02" (see write_output_ast) so ca65 accepts STZ.
 *
 * @param prog Program to optimize (must have allow_65c02=true, is_45gs02=false)
 */
void optimize_65c02_instructions_ast(Program *prog) {
    if (!prog->allow_65c02 || prog->is_45gs02) return;  // Don't apply to 45GS02!

    for (AstNode *node = prog->root; node; node = node->next) {
        if (!is_removable(node) || !op_is(node, "LDA") || !is_zero_immediate(node->operand)) continue;

        AstNode *stores[64];
        int store_count = 0;
        bool nz_dead = false;
        bool a_dead = false;

        for (AstNode *current = next_significant(node); current; current = next_significant(current)) {
            if (ends_basic_block(current)) break;
            if (op_is(current, "STA")) {
                if (!is_removable(current) || operand_is_indirect_or_y_indexed(current->operand) ||
                    store_count == (int)(sizeof(stores) / sizeof(stores[0]))) break;
                stores[store_count++] = current;
                continue;
            }
            if (reads_a(current) || (!nz_dead && reads_nz(current))) break;
            if (overwrites_a(current) && writes_nz(current)) {
                a_dead = true;
                break;
            }
            if (writes_nz(current)) {
                nz_dead = true;
                continue;
            }
            if (!preserves_a_and_nz(current)) break;
        }

        if (!a_dead || store_count == 0) continue;

        for (int i = 0; i < store_count; i++) {
            AstNode *store = stores[i];
            char *stz = realloc(store->opcode, 4);
            if (!stz) continue;
            strcpy(stz, islower((unsigned char)store->source_opcode[0]) ? "stz" : "STZ");
            store->opcode = stz;
            prog->instructions_rewritten++;
        }
        remove_instruction(prog, node, OPT_KIND_STZ);
        prog->uses_65c02_instructions = true;

        if (prog->trace_level > 1) {
            printf("DEBUG 65c02: Line %d: LDA #0 removed, %d STA converted to STZ\n", node->line_num, store_count);
        }
    }
}
