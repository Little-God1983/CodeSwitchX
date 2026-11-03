namespace CodeSwitchX.Core.GitHub;

/// <summary>An exception as the GitHub code logs it: its type and its stack, never its message, which could carry a title, a label or a repository name.</summary>
public static class FailureText
{
    public static string Of(Exception ex) => ex.StackTrace is { Length: > 0 } stack ? ex.GetType().Name + Environment.NewLine + stack : ex.GetType().Name;
}
