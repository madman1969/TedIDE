namespace Tedide.Core.Navigation;

/// <summary>
/// Works out which lines of a C file sit in an inactive <c>#if</c>/<c>#ifdef</c>/<c>#elif</c>/
/// <c>#else</c> branch, given the macros known to be defined (cc65's predefined target macros plus
/// the project's own -D defines). Only <c>defined(X)</c>, <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>,
/// parentheses and the literals 0/1 are evaluated; any other condition counts as "maybe", which keeps
/// every branch of it active. So this can hide a definition that cc65 wouldn't see, but never hides
/// one that it would.
/// </summary>
public static class PreprocessorConditions
{
    private sealed record Frame(bool ParentActive, bool Taken, bool Active);

    public static List<SourceScope> InactiveRanges(IReadOnlyList<SourceToken> tokens, IReadOnlySet<string> defined)
    {
        var ranges = new List<SourceScope>();
        var stack = new Stack<Frame>();
        int? inactiveStart = null;
        bool Active() => stack.Count == 0 || stack.Peek().Active;

        foreach (var directive in tokens.Where(t => t.Directive != 0).GroupBy(t => t.Directive).Select(g => g.ToList()))
        {
            if (directive.Count < 2 || !directive[0].Is("#"))
                continue;

            var wasActive = Active();
            var condition = directive.Skip(2).ToList();
            switch (directive[1].Text)
            {
                case "if":
                    Push(stack, wasActive, Evaluate(condition, defined));
                    break;
                case "ifdef" or "ifndef" when condition.Count > 0:
                    var isDefined = defined.Contains(condition[0].Text);
                    Push(stack, wasActive, directive[1].Is("ifdef") ? isDefined : !isDefined);
                    break;
                case "elif" when stack.Count > 0:
                {
                    var frame = stack.Pop();
                    var result = frame.Taken ? false : Evaluate(condition, defined);
                    stack.Push(new Frame(frame.ParentActive, frame.Taken || result == true, frame.ParentActive && !frame.Taken && result != false));
                    break;
                }
                case "else" when stack.Count > 0:
                {
                    var frame = stack.Pop();
                    stack.Push(new Frame(frame.ParentActive, true, frame.ParentActive && !frame.Taken));
                    break;
                }
                case "endif" when stack.Count > 0:
                    stack.Pop();
                    break;
            }

            var isActive = Active();
            if (wasActive && !isActive)
                inactiveStart = directive[^1].Line + 1;
            else if (!wasActive && isActive && inactiveStart is { } start)
            {
                if (directive[0].Line > start)
                    ranges.Add(new SourceScope(start, directive[0].Line - 1));
                inactiveStart = null;
            }
        }

        if (inactiveStart is { } open && tokens.Count > 0 && tokens[^1].Line >= open)
            ranges.Add(new SourceScope(open, tokens[^1].Line));
        return ranges;
    }

    private static void Push(Stack<Frame> stack, bool parentActive, bool? condition) =>
        stack.Push(new Frame(parentActive, condition == true, parentActive && condition != false));

    /// <summary>Three-valued: true, false, or null for "can't tell".</summary>
    public static bool? Evaluate(IReadOnlyList<SourceToken> condition, IReadOnlySet<string> defined)
    {
        var position = 0;
        var result = Or(condition, ref position, defined, out var failed);
        return failed || position != condition.Count ? null : result;
    }

    private static bool? Or(IReadOnlyList<SourceToken> t, ref int i, IReadOnlySet<string> defined, out bool failed)
    {
        var left = And(t, ref i, defined, out failed);
        while (!failed && i + 1 < t.Count && t[i].Is("|") && t[i + 1].Is("|"))
        {
            i += 2;
            var right = And(t, ref i, defined, out failed);
            left = left == true || right == true ? true : left == false && right == false ? false : null;
        }
        return left;
    }

    private static bool? And(IReadOnlyList<SourceToken> t, ref int i, IReadOnlySet<string> defined, out bool failed)
    {
        var left = Unary(t, ref i, defined, out failed);
        while (!failed && i + 1 < t.Count && t[i].Is("&") && t[i + 1].Is("&"))
        {
            i += 2;
            var right = Unary(t, ref i, defined, out failed);
            left = left == false || right == false ? false : left == true && right == true ? true : null;
        }
        return left;
    }

    private static bool? Unary(IReadOnlyList<SourceToken> t, ref int i, IReadOnlySet<string> defined, out bool failed)
    {
        failed = false;
        if (i >= t.Count)
        {
            failed = true;
            return null;
        }

        var token = t[i];
        if (token.Is("!") && !(i + 1 < t.Count && t[i + 1].Is("=")))
        {
            i++;
            var operand = Unary(t, ref i, defined, out failed);
            return operand is { } value ? !value : null;
        }
        if (token.Is("("))
        {
            i++;
            var inner = Or(t, ref i, defined, out failed);
            if (failed || i >= t.Count || !t[i].Is(")"))
            {
                failed = true;
                return null;
            }
            i++;
            return inner;
        }
        if (token.Is("defined"))
        {
            i++;
            var parenthesised = i < t.Count && t[i].Is("(");
            if (parenthesised)
                i++;
            if (i >= t.Count || t[i].Kind != TokenKind.Identifier)
            {
                failed = true;
                return null;
            }
            var name = t[i++].Text;
            if (parenthesised)
            {
                if (i >= t.Count || !t[i].Is(")"))
                {
                    failed = true;
                    return null;
                }
                i++;
            }
            return defined.Contains(name);
        }
        if (token.Kind == TokenKind.Number)
        {
            i++;
            return token.Text is "0" ? false : token.Text is "1" ? true : null;
        }
        if (token.Kind == TokenKind.Identifier)
        {
            // A macro's value, which we don't track.
            i++;
            return null;
        }

        failed = true;
        return null;
    }
}
