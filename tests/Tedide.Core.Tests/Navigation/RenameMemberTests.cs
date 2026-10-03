using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

/// <summary>
/// Renaming a struct/union member. Members are matched by name alone - which struct "s->x" belongs
/// to isn't worked out - so a name more than one struct declares has to be refused rather than
/// renamed everywhere (it renamed cursor.x along with sprite.x, silently).
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

    private static CodeNavigator Navigator()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MainC] = MainCText };
        return new CodeNavigator([MainC], path => files.TryGetValue(path, out var text) ? text : null, [], [], []);
    }

    private static (int Line, int Column) At(string needle, int offset)
    {
        var index = MainCText.IndexOf(needle, StringComparison.Ordinal) + offset;
        var before = MainCText[..index];
        return (before.Count(c => c == '\n') + 1, index - (before.LastIndexOf('\n') + 1) + 1);
    }

    private static RenamePlan Rename(string needle, int offset, string newName)
    {
        var (line, column) = At(needle, offset);
        return Navigator().PlanRename(MainC, line, column, newName);
    }

    [Fact]
    public void WhyNotRenamable_RefusesBeforeANameIsAsked()
    {
        var (line, column) = At("c->x", 3);
        Assert.Contains("member of 2 structs", Navigator().WhyNotRenamable(MainC, line, column));
        (line, column) = At("s->frame", 3);
        Assert.Null(Navigator().WhyNotRenamable(MainC, line, column));
    }

    [Fact]
    public void AMemberSeveralStructsDeclare_IsRefused()
    {
        var plan = Rename("s->x++", 3, "px");

        Assert.Empty(plan.Edits);
        Assert.Contains("'x' is a member of 2 structs or unions (main.c:1, main.c:2)", plan.Error);
        // From its declaration in the struct body, too.
        Assert.Contains("2 structs", Rename("unsigned char x, y, frame", 14, "px").Error);
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
