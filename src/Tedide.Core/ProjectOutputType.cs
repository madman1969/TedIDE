namespace Tedide.Core;

/// <summary>What building a project produces - see <see cref="TedideProject.OutputType"/>.</summary>
public enum ProjectOutputType
{
    /// <summary>A program for the target, linked by ld65.</summary>
    Application,

    /// <summary>A cc65 library archive (.lib) made by ar65, for other projects to link.</summary>
    Library,
}
