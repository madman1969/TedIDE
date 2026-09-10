namespace Tedide.App.Tests;

public class AppShellTests
{
    [Theory]
    [InlineData("screen.h", "SCREEN_H")]
    [InlineData("main.h", "MAIN_H")]
    [InlineData("Screen.h", "SCREEN_H")]
    [InlineData("my-header.h", "MY_HEADER_H")]
    [InlineData("my header.h", "MY_HEADER_H")]
    public void BuildIncludeGuardMacro_UppercasesTheBaseNameAndAppendsH(string fileName, string expectedMacro)
    {
        var macro = AppShell.BuildIncludeGuardMacro(fileName);

        Assert.Equal(expectedMacro, macro);
    }

    [Fact]
    public void BuildHeaderGuardContent_WrapsTheMacroInAnIfndefDefineEndifGuard()
    {
        // Matches the exact convention every bundled sample's own headers already follow (see e.g.
        // samples/bounce/include/main.h) and Workspace.NewProject's own generated main.h uses.
        var content = AppShell.BuildHeaderGuardContent("screen.h");

        Assert.Equal("#ifndef SCREEN_H\n#define SCREEN_H\n\n#endif\n", content);
    }
}
