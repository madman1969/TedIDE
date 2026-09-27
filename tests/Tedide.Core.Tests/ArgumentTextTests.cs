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
    public void Split_ReadsADoubledQuoteInsideQuotes_AsOneLiteralQuote()
    {
        // How a define gets a C string value - previously impossible to express.
        Assert.Equal(["-DMSG=\"hi there\""], ArgumentText.Split("\"-DMSG=\"\"hi there\"\"\""));
    }

    [Fact]
    public void Split_KeepsBackslashesLiteral_EvenBeforeAClosingQuote()
    {
        // Unlike the Windows \" convention, which would read this trailing \" as an escaped quote.
        Assert.Equal([@"C:\My Libs\", "next"], ArgumentText.Split(@"""C:\My Libs\"" next"));
    }

    [Theory]
    [InlineData("-DMSG=\"hi\"")]
    [InlineData("say \"hi\"")]
    [InlineData("\"")]
    [InlineData(@"C:\My Libs\")]
    public void Join_RoundTripsArgumentsContainingQuotes(string argument)
    {
        Assert.Equal([argument, "x"], ArgumentText.Split(ArgumentText.Join([argument, "x"])));
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
