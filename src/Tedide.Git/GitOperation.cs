namespace Tedide.Git;

/// <summary>A multi-step git operation that can stop part-way - on conflicts - and then be
/// continued or aborted.</summary>
public enum GitOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

public static class GitOperationExtensions
{
    /// <summary>"merge", "rebase", "cherry-pick", "revert" - for messages.</summary>
    public static string Describe(this GitOperation operation) => operation switch
    {
        GitOperation.Merge => "merge",
        GitOperation.Rebase => "rebase",
        GitOperation.CherryPick => "cherry-pick",
        GitOperation.Revert => "revert",
        _ => "",
    };
}
