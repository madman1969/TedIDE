namespace Tedide.Core.Navigation;

/// <summary>
/// A C type, as far as following <c>.</c> and <c>-&gt;</c> needs it: what it's built on, and how many
/// pointer or array levels sit on top. <see cref="Base"/> is a struct or union key (see
/// <see cref="SymbolDetail.Container"/>), a typedef name, or plain words such as "unsigned char".
/// </summary>
public sealed record CType(string Base, int Indirection)
{
    /// <summary>The type after one <c>*</c> or <c>[i]</c>.</summary>
    public CType Dereferenced => this with { Indirection = Math.Max(0, Indirection - 1) };

    /// <summary>Whether <see cref="Base"/> names a struct or union directly, rather than a typedef or
    /// a plain type.</summary>
    public bool IsAggregate => SymbolDetail.IsAggregateKey(Base);

    public override string ToString() => Base + new string('*', Indirection);
}

/// <summary>A function's or function-like macro's declaration, as shown while typing a call to it:
/// its text, and where each parameter sits in that text (none for <c>(void)</c>).</summary>
public sealed record CallSignature(string Text, IReadOnlyList<(int Start, int Length)> Parameters);

/// <summary>
/// What <see cref="CSymbolScanner.ScanDetailed"/> knows about a C symbol beyond where it is.
/// </summary>
/// <param name="Type">A variable's, parameter's or member's type; a function's return type; the type
/// a typedef stands for.</param>
/// <param name="Container">For a member, the struct or union it belongs to: "struct name" or
/// "union name" for a tagged one, a file position for one without a tag.</param>
/// <param name="Signature">For a function, prototype or function-like macro.</param>
public sealed record SymbolDetail(CType? Type = null, string? Container = null, CallSignature? Signature = null)
{
    /// <summary>Whether <paramref name="key"/> names a struct or union directly: "struct x", "union
    /// x", or an untagged one's position key.</summary>
    public static bool IsAggregateKey(string key) =>
        key.StartsWith("struct ", StringComparison.Ordinal) || key.StartsWith("union ", StringComparison.Ordinal) || key.Contains('@');

    /// <summary>The key for an untagged struct or union: where its body opens.</summary>
    public static string AnonymousKey(string path, int line, int column) => $"{path}@{line}:{column}";
}

/// <summary>A C file's symbols with <see cref="SymbolDetail"/>s for those that have them.</summary>
public sealed record CScan(List<SymbolDefinition> Definitions, Dictionary<SymbolDefinition, SymbolDetail> Details);
