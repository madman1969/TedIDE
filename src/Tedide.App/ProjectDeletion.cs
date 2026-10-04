using System.Runtime.InteropServices;
using Tedide.Core;

namespace Tedide.App;

/// <summary>
/// Solution Explorer > Delete Project: what may be deleted, and sending a project's folder to the
/// Recycle Bin. The whole folder goes, so a project whose folder also holds the solution file or
/// another of the solution's projects can't be deleted - that would take them with it.
/// </summary>
internal static class ProjectDeletion
{
    /// <summary>Why <paramref name="project"/> can't be deleted, or null if it can.</summary>
    internal static string? WhyNot(Workspace workspace, TedideProject project)
    {
        if (workspace.Solution is not { FilePath: { } solutionFile })
            return "Only a project in a solution can be deleted - open its .tsln first.";
        if (ProjectCommands.IsSameOrInsideDirectory(solutionFile, project.Directory))
            return $"{project.Name}'s folder also holds the solution file ({Path.GetFileName(solutionFile)}), "
                + "which deleting the folder would delete too. Use Remove from Solution instead.";
        if (workspace.Projects.FirstOrDefault(p => p != project && ProjectCommands.IsSameOrInsideDirectory(p.Directory, project.Directory)) is { } nested)
            return $"{project.Name}'s folder also holds the {nested.Name} project, which deleting the folder would delete too.";
        return null;
    }

    /// <summary>
    /// Sends <paramref name="directory"/> to the Recycle Bin, quietly - no Windows confirmation or
    /// progress dialogs over the terminal. Throws <see cref="IOException"/> if it couldn't be (a
    /// file in use by VICE, say), having changed as little as Windows managed to leave alone.
    /// </summary>
    internal static void RecycleDirectory(string directory)
    {
        var operation = new ShFileOperation
        {
            Func = FoDelete,
            // Double-null-terminated, as SHFileOperation's list of paths must be.
            From = Path.GetFullPath(directory) + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted || Directory.Exists(directory))
            throw new IOException($"Couldn't move '{directory}' to the Recycle Bin (error {result}). Is a file in it still open in another program?");
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
