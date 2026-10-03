namespace Tedide.Core.Navigation;

public enum SymbolKind
{
    // Definitions - where the symbol actually is.
    Function,
    Variable,
    Macro,
    Typedef,
    Tag,
    EnumConstant,
    Member,
    Label,
    Constant,
    Parameter,
    LocalVariable,
    File,

    // Declarations - promises that it exists somewhere else.
    Prototype,
    ExternVariable,
    Import,
}

/// <summary>A block of lines within one file that a symbol is visible in - a C function or block
/// for a local variable, the stretch between two ordinary labels for a ca65 cheap local.</summary>
public readonly record struct SourceScope(int StartLine, int EndLine)
{
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>
/// Where a symbol is defined or declared. <see cref="Scope"/> is null for a symbol visible across
/// the whole program (or at least the whole file); otherwise the symbol is only visible within those
/// lines of <see cref="FilePath"/>, and shadows any wider symbol of the same name there.
/// </summary>
public sealed record SymbolDefinition(string Name, SymbolKind Kind, string FilePath, int Line, int Column, SourceScope? Scope = null)
{
    public bool IsDeclaration => Kind is SymbolKind.Prototype or SymbolKind.ExternVariable or SymbolKind.Import;

    public bool IsLocal => Scope is not null;

    /// <summary>A short human-readable description for a picker list, e.g. "function" or "#define".</summary>
    public string KindText => Kind switch
    {
        SymbolKind.Function => "function",
        SymbolKind.Variable => "variable",
        SymbolKind.Macro => "macro",
        SymbolKind.Typedef => "typedef",
        SymbolKind.Tag => "struct/union/enum",
        SymbolKind.EnumConstant => "enum constant",
        SymbolKind.Member => "member",
        SymbolKind.Label => "label",
        SymbolKind.Constant => "constant",
        SymbolKind.Parameter => "parameter",
        SymbolKind.LocalVariable => "local variable",
        SymbolKind.File => "file",
        SymbolKind.Prototype => "prototype",
        SymbolKind.ExternVariable => "extern declaration",
        SymbolKind.Import => ".import",
        _ => Kind.ToString(),
    };
}
