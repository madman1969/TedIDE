/**
 * @file output.c
 * @brief Optimized assembly output generation implementation
 *
 * Implements writing of optimized AST back to assembly source format.
 * Handles proper formatting for different assembler syntaxes and
 * includes optional optimization trace information.
 */

#include "output.h"
#include <stdio.h>
#include <stdbool.h>

static bool same_text(const char *a, const char *b) {
    if (!a || !b) return a == b;
    return strcmp(a, b) == 0;
}

/** Rebuilds a line a pass changed: label, opcode, operand and comment, tab-separated. */
static void write_reconstructed(FILE *fp, const AstNode *node, const Program *prog) {
    if (node->label) {
        fprintf(fp, "%s", node->label);
        if (prog->config.supports_colon_labels) fprintf(fp, ":");
    }
    fprintf(fp, "\t%s", node->opcode ? node->opcode : "");
    if (node->operand && node->operand[0] != '\0') fprintf(fp, "     %s", node->operand);
    if (node->comment && node->comment[0] != '\0') fprintf(fp, "\t%s", node->comment);
    fprintf(fp, "\n");
}

/**
 * @brief Write optimized program to assembly file
 *
 * Every line no pass changed is written back exactly as it was read, so formatting, comments,
 * blank lines and directives (ca65 `.segment`, `.proc`, `.dbg`, ...) survive untouched; only a
 * line whose opcode or operand a pass rewrote is reconstructed. A dead line is omitted - or, at
 * trace level > 0, replaced with a comment naming it. A dead line that still carries a label keeps
 * the label on a line of its own (passes never kill labelled lines; this is a safety net).
 *
 * The one deliberate change to an unmodified line: when a 65C02 instruction was introduced into
 * ca65 source, its `.setcpu "6502"` is raised to "65C02" so ca65 accepts it.
 *
 * Unlike upstream, no header comment block is added, so the file's line numbers match cc65's own
 * output line for line apart from removed lines.
 *
 * @param prog Program containing optimized AST and configuration
 * @param filename Output file path
 * @return true on success, false if the file couldn't be written
 */
bool write_output_ast(Program *prog, const char *filename) {
    FILE *fp = fopen(filename, "w");
    if (!fp) {
        fprintf(stderr, "Error: Cannot write to %s\n", filename);
        return false;
    }

    const char *cmt = prog->config.comment_char;

    for (AstNode *node = prog->root; node; node = node->next) {
        if (node->is_dead) {
            if (node->label) {
                fprintf(fp, "%s%s\n", node->label, prog->config.supports_colon_labels ? ":" : "");
            }
            if (prog->trace_level > 0) {
                fprintf(fp, "%s OPT: Removed - %s\n", cmt, node->source ? node->source : "");
            }
            continue;
        }

        if (prog->uses_65c02_instructions && prog->config.type == ASM_CA65 &&
            node->opcode && strcasecmp(node->opcode, ".setcpu") == 0 &&
            node->operand && strcasecmp(node->operand, "\"6502\"") == 0) {
            fprintf(fp, "\t.setcpu\t\t\"65C02\"\n");
            continue;
        }

        if (node->source && same_text(node->opcode, node->source_opcode) &&
            same_text(node->operand, node->source_operand)) {
            fprintf(fp, "%s\n", node->source);
        } else {
            write_reconstructed(fp, node, prog);
        }
    }

    bool ok = !ferror(fp);
    if (fclose(fp) != 0) ok = false;
    if (!ok) fprintf(stderr, "Error: Failed writing %s\n", filename);
    return ok;
}
