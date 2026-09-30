namespace Tedide.Core;

/// <summary>
/// What opt6502 favours when a project has <see cref="TedideProject.UseOpt6502"/> on - passed to
/// it as -size or -speed (see Tedide.Build's Cc65Toolchain.BuildOpt6502Arguments).
/// </summary>
public enum Opt6502Mode
{
    /// <summary>-size: only changes that remove code. The default.</summary>
    Size,

    /// <summary>-speed: also replaces calls to cc65's runtime stack helpers (pushax, ldaxysp,
    /// incsp2...) inside loops with the helpers' own code - bigger, but saves 9-12 cycles per call
    /// on every pass round the loop.</summary>
    Speed,
}
