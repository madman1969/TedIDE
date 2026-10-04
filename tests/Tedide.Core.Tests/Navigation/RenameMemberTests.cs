using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

/// <summary>
/// Renaming a struct/union member. Each use is traced to its struct through the type of what's
/// left of its "." or "->", so renaming sprite.x leaves cursor.x alone. Where a use can't be traced
/// and several structs declare the name, the rename is refused rather than risk the wrong one -
/// before that, it renamed cursor.x along with sprite.x, silently.
/// </summary>
public class RenameMemberTests
{
    private static readonly string MainC = Path.Combine(Path.GetTempPath(), "tedide-member-tests", "src", "main.c");

    private const string MainCText = """
        struct sprite { unsigned char x, y, frame; };
        struct cursor { unsigned char x, y; };

        unsigned char x;

        void move(struct sprite *s, struct cursor *c)
        {
            s->x++;
            c->x = s->y;
            s->frame = 0;
            x = 1;
        }

        """;

    private static CodeNavigator Navigator(string text = MainCText)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MainC] = text };
        return new CodeNavigator([MainC], path => files.TryGetValue(path, out var found) ? found : null, [], [], []);
    }

    private static (int Line, int Column) At(string needle, int offset, string text = MainCText)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        var before = text[..index];
        return (before.Count(c => c == '\n') + 1, index - (before.LastIndexOf('\n') + 1) + 1);
    }

    private static RenamePlan Rename(string needle, int offset, string newName, string text = MainCText)
    {
        var (line, column) = At(needle, offset, text);
        return Navigator(text).PlanRename(MainC, line, column, newName);
    }

    [Fact]
    public void AMemberSeveralStructsDeclare_RenamesOnlyItsOwnStruct()
    {
        var plan = Rename("s->x++", 3, "px");

        Assert.Null(plan.Error);
        var renamed = RenamePlan.Apply(MainCText, plan.Edits);
        Assert.Contains("struct sprite { unsigned char px, y, frame; };", renamed);
        Assert.Contains("struct cursor { unsigned char x, y; };", renamed);
        Assert.Contains("s->px++;", renamed);
        Assert.Contains("c->x = s->y;", renamed);
        Assert.Contains("unsigned char x;", renamed);
    }

    [Fact]
    public void FromItsDeclaration_Too()
    {
        var plan = Rename("struct cursor { unsigned char x", 30, "col");

        Assert.Null(plan.Error);
        var renamed = RenamePlan.Apply(MainCText, plan.Edits);
        Assert.Contains("struct cursor { unsigned char col, y; };", renamed);
        Assert.Contains("c->col = s->y;", renamed);
        Assert.Contains("s->x++;", renamed);
    }

    [Fact]
    public void AUseThatCantBeTraced_IsRefused_WhenSeveralStructsDeclareTheName()
    {
        // "thing" isn't declared anywhere, so which x it has can't be worked out.
        var text = MainCText.Replace("x = 1;", "x = 1;\n    thing->x = 2;");

        var plan = Rename("s->x++", 3, "px", text);

        Assert.Empty(plan.Edits);
        Assert.Contains("'x' is a member of 2 structs or unions (main.c:1, main.c:2)", plan.Error);
        Assert.Contains("can't tell which one main.c:12 means", plan.Error);
        var (line, column) = At("thing->x", 7, text);
        Assert.Contains("can't tell which one this is", Navigator(text).WhyNotRenamable(MainC, line, column));
    }

    [Fact]
    public void AMemberOnlyOneStructDeclares_StillRenames()
    {
        var plan = Rename("s->frame", 3, "image");

        Assert.Null(plan.Error);
        var renamed = RenamePlan.Apply(MainCText, plan.Edits);
        Assert.Contains("unsigned char x, y, image;", renamed);
        Assert.Contains("s->image = 0;", renamed);
    }

    [Fact]
    public void GoToDefinition_GoesToTheRightStructsMember()
    {
        var (line, column) = At("c->x", 3);

        var definition = Assert.Single(Navigator().GoToDefinition(MainC, line, column).Definitions);

        Assert.Equal(2, definition.Line);
    }

    [Fact]
    public void FindReferences_LeavesOutOtherStructsMembers()
    {
        var (line, column) = At("c->x", 3);

        var references = Navigator().FindReferences(MainC, line, column).References;

        Assert.Equal([2, 9], references.Select(r => r.Line));
    }

    [Fact]
    public void AVariableNamedLikeAMember_StillRenames_LeavingTheMembersAlone()
    {
        var plan = Rename("x = 1;", 0, "count");

        Assert.Null(plan.Error);
        var renamed = RenamePlan.Apply(MainCText, plan.Edits);
        Assert.Contains("unsigned char count;", renamed);
        Assert.Contains("count = 1;", renamed);
        Assert.Contains("s->x++;", renamed);
    }
}
