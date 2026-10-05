using CodeSwitchX.Core.Yard;
using ModelContextProtocol;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>What the app's MCP tools share: how a refusal or a busy window reaches the brain.</summary>
internal static class ToolActs
{
    /// <summary>
    /// Runs the action; a <see cref="YardActionException"/> comes back as a tool error in its own words, and a window too busy
    /// to answer in words the brain can tell the user, not a raw error.
    /// </summary>
    public static async Task<T> Act<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (YardActionException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (TimeoutException)
        {
            // The app's window was busy past the wait: the brain gets something to tell the user, not a raw error.
            throw new McpException("CodeSwitchX's window did not respond in time, so that was not done. Say it again in a moment.");
        }
    }
}
