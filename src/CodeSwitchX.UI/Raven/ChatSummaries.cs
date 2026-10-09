using System.Text;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>Sums a Claude Code chat up from its conversation (#234).</summary>
public interface IChatSummaries
{
    /// <summary>The chat's summary: what Raven says of it, and the whole of it, to be written.</summary>
    /// <exception cref="YardActionException">It could not be summed up; the message says why.</exception>
    Task<ChatSummary> SummarizeAsync(YardChat chat, CancellationToken ct);
}

/// <param name="Short">Two or three sentences, said.</param>
/// <param name="Full">What it was asked, what it did, where it stands and what it waits for, a line each, written.</param>
public sealed record ChatSummary(string Short, string Full);

/// <summary>
/// The chat's conversation on disk, in short (<see cref="TranscriptDigest"/>), summed up by the conversation summarizer
/// (<see cref="BrainRole.ChatSummarizer"/>, a small fast model with no tools, which forgets each chat after it), in a fixed
/// shape: a short part to say, then Asked, Done, Now and Waiting. One chat at a time: the summarizer takes one question
/// at a time.
/// </summary>
/// <param name="conversationOf">The file of the chat's conversation, by its id; null when it has none.</param>
public sealed class ChatSummaries(IConductorBrain summarizer, Func<string, string?> conversationOf, TimeProvider time, ILogger<ChatSummaries> logger)
    : IChatSummaries
{
    /// <summary>The conversation summarizer's key among the app's brains.</summary>
    public const string BrainKey = "raven-chat-summaries";

    /// <summary>How long a summary may take: well under the brain's patience with a tool (<see cref="ClaudeCliBrain.Silence"/>).</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<ChatSummary> SummarizeAsync(YardChat chat, CancellationToken ct)
    {
        var digest = TranscriptDigest.Read(conversationOf(chat.Id))
            ?? throw new YardActionException($"CodeSwitchX cannot read the conversation of the {chat.Title} chat, so it cannot sum it up.");
        var question = $"The chat \"{chat.Title}\" in {chat.Workspace}, {Where(chat)}.\n\nIts conversation, in short:\n{digest}";

        // The limit counts the wait behind another summary too: the brain's patience with the tool does.
        using var limit = new CancellationTokenSource(Limit, time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        var held = false;
        try
        {
            await _one.WaitAsync(both.Token).ConfigureAwait(false);
            held = true;
            var reply = new StringBuilder();
            await foreach (var e in summarizer.AskAsync(question, both.Token).ConfigureAwait(false))
            {
                switch (e)
                {
                    case BrainText { Delta: var piece }:
                        reply.Append(piece);
                        break;
                    case BrainFailed { Reason: var why }:
                        logger.LogWarning("Summing up chat {Id} failed: {Why}", chat.Id, why);
                        throw new YardActionException($"Summing up the {chat.Title} chat failed: {why}");
                }
            }

            logger.LogInformation("Summed up chat {Id} from {Chars} characters of its conversation", chat.Id, digest.Length);
            return Parse(reply.ToString())
                ?? throw new YardActionException($"Summing up the {chat.Title} chat gave no summary.");
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new YardActionException($"Summing up the {chat.Title} chat took longer than {Limit.TotalSeconds:0} seconds and was given up.");
        }
        finally
        {
            if (held)
            {
                _one.Release();
            }
        }
    }

    /// <summary>What the summarizer is told of where the chat is now, as the Yard shows it.</summary>
    private static string Where(YardChat chat) => chat.NeedsYou ? "waiting on the user right now"
        : chat.State switch
        {
            Core.Sessions.SessionState.Working => "working right now",
            Core.Sessions.SessionState.Ended or Core.Sessions.SessionState.Errored => "closed",
            _ => "idle, its turn over",
        };

    /// <summary>
    /// The short part (the "Short:" line) and the rest; a reply that keeps no shape is said and written whole. Null for none.
    /// </summary>
    internal static ChatSummary? Parse(string reply)
    {
        // Bullets, numbers, bold and code fences a model may add are no part of it.
        var lines = reply.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("```", StringComparison.Ordinal))
            .Select(l => Marks.Replace(l, "").Replace("**", "").Trim())
            .Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var at = lines.FindIndex(l => l.StartsWith("Short:", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return new ChatSummary(string.Join(' ', lines), string.Join('\n', lines));
        }

        // "Short:" alone on its line has its sentences on the next.
        var said = lines[at]["Short:".Length..].Trim();
        var taken = 1;
        if (said.Length == 0 && at + 1 < lines.Count && !IsLabel(lines[at + 1]))
        {
            said = lines[at + 1];
            taken = 2;
        }

        var rest = lines.Where((_, i) => i < at || i >= at + taken).ToList();
        return said.Length == 0 ? null : new ChatSummary(said, rest.Count > 0 ? string.Join('\n', rest) : said);
    }

    /// <summary>A bullet or a number before a line: "- ", "* ", "• ", "1. ", "2) ".</summary>
    private static readonly System.Text.RegularExpressions.Regex Marks = new(@"^(?:[*\-•]\s*|\d+[.)]\s*)+");

    private static bool IsLabel(string line) =>
        new[] { "Asked:", "Done:", "Now:", "Waiting:" }.Any(l => line.StartsWith(l, StringComparison.OrdinalIgnoreCase));
}
