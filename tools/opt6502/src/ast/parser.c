/**
 * @file parser.c
 * @brief Assembly language parser implementation
 *
 * Implements parsing of assembly source lines into AST nodes.
 * Handles various assembler syntaxes, label formats, and comment styles.
 */

#include "parser.h"
#include <string.h>
#include <ctype.h>
#include <stdlib.h>

/** Heap copy of [start, end). */
static char *dup_range(const char *start, const char *end) {
    size_t len = (size_t)(end - start);
    char *copy = malloc(len + 1);
    if (copy) {
        memcpy(copy, start, len);
        copy[len] = '\0';
    }
    return copy;
}

static char *dup_string(const char *s) {
    return s ? dup_range(s, s + strlen(s)) : NULL;
}

/**
 * The first comment start at or after p that isn't inside a '...' or "..." literal, or the
 * string's terminating NUL if there is none - so a ';' in `.byte "a;b"` or `lda #';'` stays part
 * of the operand.
 */
static const char *find_comment(const char *p, AsmConfig *config) {
    char quote = 0;
    for (; *p; p++) {
        if (quote) {
            if (*p == quote) quote = 0;
        } else if (*p == '"' || *p == '\'') {
            quote = *p;
        } else if (is_comment_start(p, config)) {
            break;
        }
    }
    return p;
}

/**
 * @brief Parse an assembly line into an AST node
 *
 * Parses a line of assembly code according to assembler syntax rules:
 * 1. Labels: start at column 0. For colon-label assemblers (ca65 and most others) a label must
 *    end with ':' - any other column-0 token (a ca65 directive such as `.segment` or `.proc`, or
 *    a `sym = value` assignment) is an ordinary statement. Assemblers without colon labels
 *    (Merlin) treat any column-0 token as a label, as before.
 * 2. Opcodes: follow the label, or the leading whitespace
 * 3. Operands: everything after the opcode up to an unquoted comment
 * 4. Comments: start with the assembler-specific character(s), outside quotes
 *
 * Blank and comment-only lines produce a node with no label or opcode. The original line is
 * kept in node->source so output can write it back verbatim (see write_output_ast).
 *
 * @param node Node to populate with parsed components
 * @param line Source line to parse
 * @param line_num Line number (unused, kept for future diagnostics)
 * @param config Assembler syntax configuration
 */
void parse_line_ast(AstNode *node, const char *line, int line_num, AsmConfig *config) {
    (void)line_num;  // Suppress unused parameter warning
    const char *p = line;
    node->source = dup_string(line);

    if (*p && !isspace((unsigned char)*p) && !is_comment_start(p, config)) {
        const char *end = p;
        while (*end && !isspace((unsigned char)*end) && *end != ':' && !is_comment_start(end, config)) {
            end++;
        }
        bool has_colon = *end == ':';
        if (end > p && (has_colon || !config->supports_colon_labels)) {
            // ca65's "name:=" is an assignment, not a label.
            if (!(has_colon && end[1] == '=')) {
                // Keep the second colon of a ca65 global label ("name::") as part of its text.
                if (has_colon && end[1] == ':') end++;
                node->label = dup_range(p, end);
                node->is_local_label = is_local_label(node->label, config);
                node->type = NODE_LABEL;
                p = has_colon ? end + 1 : end;
            }
        }
    }

    while (*p && isspace((unsigned char)*p)) p++;

    const char *comment = find_comment(p, config);

    // Opcode: the first token before any comment
    const char *start = p;
    while (p < comment && !isspace((unsigned char)*p)) p++;
    if (p > start) node->opcode = dup_range(start, p);

    while (p < comment && isspace((unsigned char)*p)) p++;

    // Operand: the rest up to the comment, trailing whitespace trimmed
    const char *end = comment;
    while (end > p && isspace((unsigned char)end[-1])) end--;
    if (end > p) node->operand = dup_range(p, end);

    if (*comment) node->comment = dup_string(comment);

    node->source_opcode = dup_string(node->opcode);
    node->source_operand = dup_string(node->operand);
}

/**
 * @brief Build complete AST from program lines
 *
 * Placeholder for additional AST post-processing. Currently, the AST
 * is built incrementally during line-by-line parsing via add_line_ast().
 * This function is reserved for future enhancements like AST validation
 * or structural analysis.
 *
 * @param prog Program containing parsed AST
 */
void build_ast(Program *prog) {
    (void)prog;  // Suppress unused parameter warning
    // AST is built during parsing in add_line_ast
    // This function can be used for additional AST processing if needed
}
