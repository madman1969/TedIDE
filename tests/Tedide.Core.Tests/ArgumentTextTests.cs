namespace Tedide.Core.Tests;

public class ArgumentTextTests
{
    [Fact]
    public void Split_SeparatesOnAnyRunOfWhitespace()
    {
        Assert.Equal(["-Cl", "-I", "include"], ArgumentText.Split("  -Cl \t-I   include "));
    }

    [Fact]
    public void Split_KeepsAQuotedPathWithSpacesAsOneArgument_WithoutTheQuotes()
    {
        Assert.Equal(["include", @"C:\My Libs\include"], ArgumentText.Split(@"include ""C:\My Libs\include"""));
    }

    [Fact]
    public void Split_JoinsQuotedAndUnquotedTextThatTouch_IntoOneArgument()
    {
        // The same as a shell: --asm-define="A B" is one argument, --asm-define=A B.
        Assert.Equal(["--asm-define=A B"], ArgumentText.Split(@"--asm-define=""A B"""));
    }

    [Fact]
    public void Split_KeepsAnExplicitEmptyQuotedArgument()
    {
        Assert.Equal(["a", "", "b"], ArgumentText.Split(@"a """" b"));
    }

    [Fact]
    public void Split_ReturnsNothing_ForBlankText()
    {
        Assert.Empty(ArgumentText.Split("   "));
    }

    [Fact]
    public void Join_QuotesOnlyArgumentsThatNeedIt_AndRoundTripsThroughSplit()
    {
        List<string> arguments = ["-Cl", @"C:\My Libs\include", "", "NAME=VALUE"];

        var text = ArgumentText.Join(arguments);

        Assert.Equal(@"-Cl ""C:\My Libs\include"" """" NAME=VALUE", text);
        Assert.Equal(arguments, ArgumentText.Split(text));
    }
}
