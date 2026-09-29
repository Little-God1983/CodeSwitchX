namespace CodeSwitchX.Core.Persistence;

/// <summary>
/// Where a chat's title came from, in the order Claude Code's own session list uses: a name given with <c>/rename</c>,
/// then the generated title, then the first prompt. A title replaces one from a lower source; only another
/// <c>/rename</c> replaces a <c>/rename</c> name.
/// </summary>
public enum TitleSource
{
    None = 0,
    Prompt = 1,
    Generated = 2,
    Custom = 3,
}
