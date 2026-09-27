using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

/// <summary>Types for the Locals table come from the C source - cc65's debug info records none.</summary>
public class CDeclarationsTests
{
    // Shaped like samples/CBMInfo's video.c.
    private const string Source = """
        #include "video.h"

        static unsigned detect_video_system(void)
        {
            unsigned max_raster = 0;
            unsigned int i;

            for (i = 0; i < 1000; ++i)
            {
                unsigned char lo = VIC.rasterline;
                unsigned current = lo;
                if (current > max_raster)
                {
                    max_raster = current;
                }
            }
            return max_raster;
        }

        void video_get_text_size(unsigned char* cols, unsigned char *rows)
        {
            unsigned char c, r;
            signed char delta = -1;
            long total = 0L;
            SystemInfo* info;
            screensize(&c, &r);
        }
        """;

    private static IReadOnlyDictionary<string, CVariableType> Types(string function, int uptoLine, params string[] names) =>
        CDeclarations.FindTypes(Source, function, names, uptoLine);

    [Fact]
    public void FindsLocals_IncludingInitializedOnesAndPlainUnsigned()
    {
        var types = Types("detect_video_system", 17, "max_raster", "i", "lo", "current");

        Assert.Equal("unsigned", types["max_raster"].Text);
        Assert.Equal(2, types["max_raster"].Size);
        Assert.False(types["max_raster"].IsSigned);
        Assert.Equal("unsigned int", types["i"].Text);
        Assert.Equal(new CVariableType("unsigned char", 1, IsPointer: false, IsSigned: false, IsChar: true), types["lo"]);
    }

    [Fact]
    public void FindsParameters_WithThePointerOnEitherSide()
    {
        var types = Types("video_get_text_size", 26, "cols", "rows");

        Assert.Equal(new CVariableType("unsigned char*", 2, IsPointer: true, IsSigned: false, IsChar: false), types["cols"]);
        Assert.Equal(types["cols"], types["rows"]);
    }

    [Fact]
    public void FindsEveryNameInACommaList_AndSignedLongAndTypedefPointers()
    {
        var types = Types("video_get_text_size", 27, "c", "r", "delta", "total", "info");

        Assert.Equal(1, types["c"].Size);
        Assert.Equal(types["c"], types["r"]);
        Assert.True(types["delta"].IsSigned);
        Assert.Equal(4, types["total"].Size);
        Assert.True(types["total"].IsSigned);
        Assert.Equal(new CVariableType("SystemInfo*", 2, IsPointer: true, IsSigned: false, IsChar: false), types["info"]);
    }

    [Fact]
    public void IgnoresStatementsThatOnlyLookLikeDeclarations()
    {
        // "return max_raster;" and "max_raster = current;" must not redefine max_raster's type.
        var types = Types("detect_video_system", 18, "max_raster");

        Assert.Equal("unsigned", types["max_raster"].Text);
    }

    [Fact]
    public void FindsALocalWhoseDeclarationIsStillAhead_ButNotPastTheFunctionsEnd()
    {
        // Stopped on "unsigned max_raster = 0;" (line 5): i is declared on line 6, lo in the loop.
        var types = Types("detect_video_system", 5, "i", "lo", "c");

        Assert.Equal("unsigned int", types["i"].Text);
        Assert.Equal("unsigned char", types["lo"].Text);
        Assert.False(types.ContainsKey("c")); // video_get_text_size's, after this function's "}"
    }

    [Fact]
    public void ReturnsNothing_ForAnUnknownFunction()
    {
        Assert.Empty(Types("no_such_function", 27, "c"));
    }
}
