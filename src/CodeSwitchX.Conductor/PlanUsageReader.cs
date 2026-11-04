using CodeSwitchX.Core.Usage;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Conductor;

/// <summary>
/// Reads the plan's real limits with <c>claude -p "/usage"</c> (#370), through the shared package. Claude Code answers that
/// itself, with no model turn and no tokens, so reading the limits does not use them up. The user's settings stay out, and so
/// do their hooks: those would make the reading a chat on the Yard, and take longer. Nothing is saved.
/// </summary>
/// <param name="claude">The package's <see cref="ClaudeCliChatClient"/>, made with <see cref="Options"/>.</param>
public sealed class PlanUsageReader(IChatClient claude, TimeProvider time, ILogger<PlanUsageReader> logger) : IPlanUsageReader
{
    /// <summary>How long a reading may take; it took 5 to 9 seconds.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private bool _formatLogged;
    private bool _failing;

    /// <summary>
    /// The model is never asked by this command. Should a later Claude Code hand it to one, the cheapest answers, and
    /// <see cref="PlanReadingKind.SpentATurn"/> ends the readings.
    /// </summary>
    public static ClaudeCliOptions Options() => new()
    {
        Isolation = ClaudeIsolation.Custom,
        DefaultSystemPrompt = null,
        DefaultModel = "haiku",
        KeepApiKeyEnvironment = true,
        PersistSessions = false,
        WallClockTimeout = Limit,
    };

    public async Task<PlanReading> ReadAsync(CancellationToken ct)
    {
        var options = new ChatOptions().WithClaudeCli(c => c.ExtraArguments = ["--setting-sources", "project,local"]);
        ChatResponse response;
        try
        {
            response = await claude.GetResponseAsync([new ChatMessage(ChatRole.User, "/usage")], options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_failing)
            {
                _failing = true;
                logger.LogWarning(ex, "The plan's limits could not be read; what was last read stays on the bar");
            }

            return new PlanReading(PlanReadingKind.Failed);
        }

        _failing = false;
        if (response.AdditionalProperties?.ContainsKey(ClaudeCliKeys.LocalCommand) != true)
        {
            logger.LogWarning("/usage reached the model instead of running on its own, which spends the limits it reads; the plan's limits are not read again until the app restarts");
            return new PlanReading(PlanReadingKind.SpentATurn);
        }

        var limits = PlanUsageText.Parse(response.Text, time.GetUtcNow());
        if ((limits.FiveHour is null || limits.Week is null) && !_formatLogged)
        {
            _formatLogged = true;
            // Only the lines it looks for: the rest of the text quotes the user's own usage.
            var lines = response.Text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Current ", StringComparison.Ordinal));
            logger.LogWarning(
                "/usage gave {Found} of the two limits; none on an API key, otherwise its text changed and the bar cannot show them until this app reads it. Its lines: {Lines}",
                (limits.FiveHour is null ? 0 : 1) + (limits.Week is null ? 0 : 1), string.Join(" | ", lines));
        }

        return limits.HasAny ? new PlanReading(PlanReadingKind.Read, limits) : new PlanReading(PlanReadingKind.NoFigures);
    }
}
