/**
 * @file nodeinfo.c
 * @brief Shared instruction classification for the optimization passes (Tedide addition)
 *
 * Conservative by design: any mnemonic not listed here (a 65816-only instruction, or a ca65
 * macro such as cc65's longbranch "jeq") is treated as ending straight-line code and as reading
 * everything, so no pass ever optimizes across something it doesn't understand.
 */

#include "nodeinfo.h"
#include <ctype.h>
#include <stdlib.h>

static bool in_list(const AstNode *node, const char *const *list) {
    if (!node || !node->opcode) return false;
    for (; *list; list++) {
        if (strcasecmp(node->opcode, *list) == 0) return true;
    }
    return false;
}

/* ASL/LSR/ROL/ROR/INC/DEC with no operand (or "a") work on the accumulator. */
static bool is_accumulator_mode(const AstNode *node) {
    static const char *const rmw[] = {"ASL", "LSR", "ROL", "ROR", "INC", "DEC", NULL};
    if (!in_list(node, rmw)) return false;
    return !node->operand || node->operand[0] == '\0' || strcasecmp(node->operand, "a") == 0;
}

static const char *const BRANCHES_AND_JUMPS[] = {
    "BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA",
    "JMP", "JSR", "RTS", "RTI", "BRK", NULL};

static const char *const OVERWRITES_A[] = {"LDA", "PLA", "TXA", "TYA", NULL};

static const char *const READS_A[] = {
    "ADC", "SBC", "AND", "ORA", "EOR", "CMP", "BIT", "STA", "PHA", "TAX", "TAY",
    "JSR", "JMP", "RTS", "RTI", "BRK", NULL};

static const char *const READS_NZ[] = {
    "BEQ", "BNE", "BMI", "BPL", "PHP", "JSR", "JMP", "RTS", "RTI", "BRK", NULL};

static const char *const WRITES_NZ[] = {
    "LDA", "LDX", "LDY", "TAX", "TAY", "TXA", "TYA", "TSX", "PLA",
    "INX", "INY", "DEX", "DEY", "INC", "DEC", "ASL", "LSR", "ROL", "ROR",
    "AND", "ORA", "EOR", "ADC", "SBC", "CMP", "CPX", "CPY", "BIT", NULL};

static const char *const PRESERVES_A_AND_NZ[] = {
    "STA", "STX", "STY", "STZ", "CLC", "SEC", "CLI", "SEI", "CLD", "SED", "CLV",
    "NOP", "PHA", "PHX", "PHY", "TXS", "PHP", NULL};

static bool is_known(const AstNode *node) {
    return in_list(node, BRANCHES_AND_JUMPS) || in_list(node, WRITES_NZ) ||
           in_list(node, PRESERVES_A_AND_NZ) || in_list(node, READS_A);
}

bool op_is(const AstNode *node, const char *mnemonic) {
    return node && node->opcode && strcasecmp(node->opcode, mnemonic) == 0;
}

bool is_directive(const AstNode *node) {
    return node && node->opcode && node->opcode[0] == '.';
}

bool is_transparent(const AstNode *node) {
    if (!node || node->label) return false;
    return !node->opcode || node->opcode[0] == '\0' || strcasecmp(node->opcode, ".dbg") == 0;
}

AstNode *next_significant(AstNode *node) {
    for (node = node ? node->next : NULL; node; node = node->next) {
        if (!node->is_dead && !is_transparent(node)) return node;
    }
    return NULL;
}

bool is_removable(const AstNode *node) {
    return node && !node->is_dead && !node->no_optimize && !node->label &&
           node->opcode && node->opcode[0] != '\0' && !is_directive(node);
}

bool ends_basic_block(const AstNode *node) {
    if (!node) return true;
    if (node->label || node->no_optimize) return true;
    if (is_directive(node)) return strcasecmp(node->opcode, ".dbg") != 0;
    if (!node->opcode || node->opcode[0] == '\0') return false;
    return in_list(node, BRANCHES_AND_JUMPS) || !is_known(node);
}

bool reads_a(const AstNode *node) {
    return in_list(node, READS_A) || is_accumulator_mode(node) || !is_known(node);
}

bool overwrites_a(const AstNode *node) {
    return in_list(node, OVERWRITES_A);
}

bool reads_nz(const AstNode *node) {
    return in_list(node, READS_NZ) || !is_known(node);
}

bool writes_nz(const AstNode *node) {
    return in_list(node, WRITES_NZ);
}

bool preserves_a_and_nz(const AstNode *node) {
    return in_list(node, PRESERVES_A_AND_NZ);
}

bool operand_is_indirect_or_y_indexed(const char *operand) {
    if (!operand) return false;
    if (strchr(operand, '(') || strchr(operand, '[')) return true;
    size_t len = strlen(operand);
    while (len > 0 && isspace((unsigned char)operand[len - 1])) len--;
    return len >= 2 && operand[len - 2] == ',' && tolower((unsigned char)operand[len - 1]) == 'y';
}

/* True if the operand's address (before any ",x"/",y") is a zero-page one - see estimate_cost. */
static bool is_zero_page(const Program *prog, const char *operand) {
    const char *p = operand;
    if (*p == '$') {
        int digits = 0;
        for (p++; isxdigit((unsigned char)*p); p++) digits++;
        return digits > 0 && digits <= 2;
    }
    if (isdigit((unsigned char)*p)) return atoi(p) < 256;
    const char *start = p;
    while (isalnum((unsigned char)*p) || *p == '_' || *p == '@' || *p == '.') p++;
    size_t len = (size_t)(p - start);
    if (len == 0) return false;
    for (int i = 0; i < prog->zp_symbol_count; i++) {
        if (strlen(prog->zp_symbols[i]) == len && strncmp(prog->zp_symbols[i], start, len) == 0) return true;
    }
    return false;
}

void estimate_cost(const Program *prog, const AstNode *node, int *bytes, int *cycles) {
    static const char *const RMW[] = {"ASL", "LSR", "ROL", "ROR", "INC", "DEC", NULL};
    static const char *const BRANCHES[] = {"BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA", NULL};
    const char *operand = node->operand;

    if (!operand || operand[0] == '\0' || strcasecmp(operand, "a") == 0) {
        *bytes = 1;
        if (op_is(node, "PHA") || op_is(node, "PHP") || op_is(node, "PHX") || op_is(node, "PHY")) *cycles = 3;
        else if (op_is(node, "PLA") || op_is(node, "PLP") || op_is(node, "PLX") || op_is(node, "PLY")) *cycles = 4;
        else if (op_is(node, "RTS") || op_is(node, "RTI")) *cycles = 6;
        else if (op_is(node, "BRK")) *cycles = 7;
        else *cycles = 2;
        return;
    }
    if (operand[0] == '#' || in_list(node, BRANCHES)) {
        *bytes = 2;
        *cycles = 2;
        return;
    }
    if (op_is(node, "JSR")) { *bytes = 3; *cycles = 6; return; }
    if (op_is(node, "JMP")) { *bytes = 3; *cycles = operand[0] == '(' ? 5 : 3; return; }
    if (operand[0] == '(') {
        *bytes = 2;
        *cycles = strstr(operand, ",x)") || strstr(operand, ",X)") ? 6 : 5;
        return;
    }

    bool indexed = strchr(operand, ',') != NULL;
    bool zp = is_zero_page(prog, operand);
    *bytes = zp ? 2 : 3;
    if (in_list(node, RMW)) *cycles = zp ? (indexed ? 6 : 5) : (indexed ? 7 : 6);
    else if (op_is(node, "STA") && indexed && !zp) *cycles = 5;
    else *cycles = zp ? (indexed ? 4 : 3) : 4;
}

void remove_instruction(Program *prog, AstNode *node, OptKind kind) {
    int bytes, cycles;
    estimate_cost(prog, node, &bytes, &cycles);
    node->is_dead = true;
    prog->optimizations++;
    prog->stats[kind]++;
    prog->instructions_removed++;
    prog->bytes_saved += bytes;
    prog->cycles_saved += cycles;
}

void record_rewrite(Program *prog, OptKind kind, int bytes_saved, int cycles_saved) {
    prog->optimizations++;
    prog->stats[kind]++;
    prog->instructions_rewritten++;
    prog->bytes_saved += bytes_saved;
    prog->cycles_saved += cycles_saved;
}

bool is_proc_boundary(const AstNode *node, const char *directive) {
    return node->label ? strcasecmp(node->label, directive) == 0 : op_is(node, directive);
}

AstNode *find_label(Program *prog, const AstNode *from, const char *label) {
    AstNode *proc_start = prog->root;
    for (AstNode *node = prog->root; node && node != from; node = node->next) {
        if (is_proc_boundary(node, ".proc")) proc_start = node;
        if (is_proc_boundary(node, ".endproc")) proc_start = node->next;
    }
    for (AstNode *node = proc_start; node; node = node->next) {
        if (node != proc_start && is_proc_boundary(node, ".proc")) break;
        if (node->label && strcmp(node->label, label) == 0) return node;
        if (is_proc_boundary(node, ".endproc")) break;
    }
    return NULL;
}

AstNode *instruction_at(AstNode *label_node) {
    for (AstNode *node = label_node; node; node = node->next) {
        if (node->is_dead || is_transparent(node)) continue;
        if (is_directive(node)) return NULL;
        if (node->opcode && node->opcode[0] != '\0') return node;
        // A label-only line: execution falls through to whatever follows.
    }
    return NULL;
}
