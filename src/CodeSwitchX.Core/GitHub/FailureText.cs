namespace CodeSwitchX.Core.GitHub;

/// <summary>An exception as the GitHub code logs it: its type and its stack, and those of each exception inside it, never a message, which could carry a title, a label or a repository name.</summary>
public static class FailureText
{
    public static string Of(Exception ex)
    {
        var text = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            text.Append(e == ex ? "" : Environment.NewLine + " ---> ").Append(e.GetType().Name);
            if (e.StackTrace is { Length: > 0 } stack)
            {
                text.Append(Environment.NewLine).Append(stack);
            }
        }

        return text.ToString();
    }
}
