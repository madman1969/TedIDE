/**
 * @file nodeinfo.h
 * @brief Shared instruction classification for the optimization passes (Tedide addition)
 *
 * Every pass needs the same answers - is this line an instruction, may it be deleted, does it
 * read or overwrite A or the N/Z flags, does it end straight-line code - and getting any of them
 * wrong in just one pass miscompiles cc65 output. They live here once instead.
 */

#ifndef NODEINFO_H
#define NODEINFO_H

#include "../types.h"

/** True if node is the instruction mnemonic (case-insensitive - cc65 emits lowercase). */
bool op_is(const AstNode *node, const char *mnemonic);

/** True if the line is an assembler directive (.segment, .proc, .byte, .dbg, ...). */
bool is_directive(const AstNode *node);

/**
 * True for a line the passes can look straight past without it affecting any analysis: a blank
 * or comment-only line, or a ".dbg" debug-info directive. Never true for a labelled line.
 */
bool is_transparent(const AstNode *node);

/** The next live node after node that isn't transparent, or NULL at end of program. */
AstNode *next_significant(AstNode *node);

/**
 * True if node may be marked dead: a live, unlabelled, optimizable machine instruction. Labels
 * are never removed (something may reference them), and neither are directives.
 */
bool is_removable(const AstNode *node);

/** True if node ends straight-line code: a label, any branch/jump/call/return, or a directive. */
bool ends_basic_block(const AstNode *node);

/** True if the instruction reads the accumulator (including JSR/RTS/RTI, which hand it on). */
bool reads_a(const AstNode *node);

/** True if the instruction replaces the accumulator without reading it first. */
bool overwrites_a(const AstNode *node);

/** True if the instruction reads the N or Z flag (conditional branches, PHP, and JSR/RTS/RTI). */
bool reads_nz(const AstNode *node);

/** True if the instruction sets both N and Z without reading them. */
bool writes_nz(const AstNode *node);

/** True if the instruction changes neither A nor the N/Z flags (stores, flag ops, NOP, PHA...). */
bool preserves_a_and_nz(const AstNode *node);

/** True if the operand is indirect or indexed by Y - modes STZ doesn't have. */
bool operand_is_indirect_or_y_indexed(const char *operand);


/**
 * Estimated size in bytes and cycles of one execution of node's instruction, from its addressing
 * mode (cycles ignore page-crossing and branch-taken penalties). An operand is taken to be a
 * zero-page address when it's a literal below $100 or its leading symbol was declared by
 * .importzp/.exportzp/.globalzp; anything else is assumed absolute.
 */
void estimate_cost(const Program *prog, const AstNode *node, int *bytes, int *cycles);

/** Marks node dead as an optimization of the given kind, updating every counter and estimate. */
void remove_instruction(Program *prog, AstNode *node, OptKind kind);

/** Counts an in-place rewrite of kind that saved the given bytes and cycles. */
void record_rewrite(Program *prog, OptKind kind, int bytes_saved, int cycles_saved);

#endif // NODEINFO_H
