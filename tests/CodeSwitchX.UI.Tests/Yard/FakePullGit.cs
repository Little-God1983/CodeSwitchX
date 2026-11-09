using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>
/// Git as <see cref="GitPuller"/> sees it (#407), scripted: a clean checkout on <see cref="Current"/> whose pull brings 3 new commits.
/// Each run waits for <see cref="Gate"/>, so a test can hold a pull while it looks at the panel.
/// </summary>
internal sealed class FakePullGit
{
    /// <summary>Released at once unless a test takes it back with <see cref="Hold"/>.</summary>
    public TaskCompletionSource Gate { get; private set; } = Released();

    /// <summary>What <c>git status --porcelain</c> prints: nothing is a clean checkout.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The checkout's branch.</summary>
    public string Current { get; set; } = "feature/x";

    /// <summary>Commits of the checkout's branch that GitHub has not got.</summary>
    public int Unpushed { get; set; }

    /// <summary>The command lines run, in order.</summary>
    public List<string> Ran { get; } = [];

    public void Hold() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => Gate.TrySetResult();

    public async Task<ProcessRunResult> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        lock (Ran)
        {
            Ran.Add(string.Join(' ', arguments));
        }

        await Gate.Task;
        return arguments[0] switch
        {
            "status" => Ok(Status),
            "rev-parse" => Ok("abc123\n"),
            // The branch left after a merge against GitHub's copy of it, the new commits since the pull began, local commits on main.
            "rev-list" when arguments[2].StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && arguments[2].EndsWith("..HEAD", StringComparison.Ordinal) => Ok($"{Unpushed}\n"),
            "rev-list" => Ok(arguments[2].EndsWith("..HEAD", StringComparison.Ordinal) ? "3\n" : "0\n"),
            "branch" => Ok(Current + "\n"),
            _ => Ok(string.Empty),
        };
    }

    private static ProcessRunResult Ok(string output) => new(output, false, 1, 0, null, string.Empty);

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }
}
