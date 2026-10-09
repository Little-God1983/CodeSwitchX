using System.Globalization;
using System.Text;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>Sums the user's last working session up (#237).</summary>
public interface ISessionRecaps
{
    /// <summary>The summary, short enough to be said: when it was, what each chat got done, what is still open.</summary>
    /// <exception cref="YardActionException">There is none to sum up, or it could not be; the message says why.</exception>
    Task<string> RecapAsync(CancellationToken ct);
}

/// <summary>
/// The last working session (<see cref="WorkSessions"/>, from the minutes the chats of the user's workspaces used tokens
/// in), what each chat did in it (<see cref="TranscriptDigest.ReadBetween"/>) and the commits made in each workspace then,
/// summed up by the recapper (<see cref="BrainRole.Recapper"/>, a small fast model with no tools) in one short summary.
/// One at a time.
/// </summary>
/// <param name="sessions">The chats the app knows, with their workspace and conversation (<c>SessionEngine.Snapshots</c>).</param>
/// <param name="git">Runs git in a folder with the arguments given; its output, or null when it failed.</param>
public sealed class SessionRecaps(IConductorBrain recapper, IUsageStore usage, Func<IReadOnlyCollection<SessionSnapshot>> sessions, IYardDirectory yard,
    Func<string, string, CancellationToken, Task<string?>> git, TimeProvider time, ILogger<SessionRecaps> logger) : ISessionRecaps
{
    /// <summary>The recapper's key among the app's brains.</summary>
    public const string BrainKey = "raven-session-recaps";

    /// <summary>How far back a session is looked for.</summary>
    internal static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    /// <summary>How long a recap may take: it carries on past the brain's patience with a tool, written when done.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>The most chats told: those that worked the longest.</summary>
    internal const int MaxChats = 12;

    /// <summary>The most of each chat's steps the recapper is given.</summary>
    internal const int ChatChars = 3_000;

    /// <summary>The most commits told per workspace.</summary>
    internal const int MaxCommits = 15;

    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<string> RecapAsync(CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(Limit, time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        var held = false;
        try
        {
            await _one.WaitAsync(both.Token).ConfigureAwait(false);
            held = true;
            var question = await QuestionAsync(both.Token).ConfigureAwait(false);
            var reply = new StringBuilder();
            await foreach (var e in recapper.AskAsync(question, both.Token).ConfigureAwait(false))
            {
                switch (e)
                {
                    case BrainText { Delta: var piece }:
                        reply.Append(piece);
                        break;
                    case BrainFailed { Reason: var why }:
                        logger.LogWarning("Summing up the last working session failed: {Why}", why);
                        throw new YardActionException($"Summing up your last working session failed: {why}");
                }
            }

            var said = string.Join(' ', reply.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return said.Length > 0 ? said : throw new YardActionException("Summing up your last working session gave no summary.");
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new YardActionException($"Summing up your last working session took longer than {Limit.TotalMinutes:0} minutes and was given up.");
        }
        finally
        {
            if (held)
            {
                _one.Release();
            }
        }
    }

    /// <summary>What the recapper is given: when the session was, what each chat did in it, and the commits made then.</summary>
    internal async Task<string> QuestionAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var chats = sessions().Where(s => s.WorkspaceId is not null).ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        var buckets = await usage.GetBucketsAsync(now - LookBack, now, ct).ConfigureAwait(false);
        var session = WorkSessions.Last(buckets, chats.ContainsKey, now)
            ?? throw new YardActionException($"No working session before this one shows in the last {LookBack.TotalDays:0} days.");
        var workspaces = await yard.WorkspacesAsync(ct).ConfigureAwait(false);
        string NameOf(Guid? id) => workspaces.FirstOrDefault(w => w.Id == id)?.Name ?? "a workspace no longer on the Yard";

        var text = new StringBuilder();
        text.Append("It was ").Append(When(session.Start, now)).Append(", from ").Append(Local(session.Start).ToString("HH:mm", CultureInfo.InvariantCulture))
            .Append(" to ").Append(Local(session.End).ToString("HH:mm", CultureInfo.InvariantCulture)).Append(".\n\n");

        var told = 0;
        foreach (var (id, minutes) in session.MinutesByChat.OrderByDescending(c => c.Value).Take(MaxChats))
        {
            var chat = chats[id];
            if (TranscriptDigest.ReadBetween(chat.TranscriptPath, session.Start - TimeSpan.FromMinutes(1), session.End, ChatChars) is not { } steps)
            {
                continue;
            }

            text.Append($"Chat \"{chat.Title ?? "untitled"}\" in {NameOf(chat.WorkspaceId)}, {minutes} minutes of work:\n{steps}\n\n");
            told++;
        }

        var commits = await CommitsAsync(workspaces, session, ct).ConfigureAwait(false);
        if (told == 0 && commits.Count == 0)
        {
            throw new YardActionException("Your last working session left nothing that can be summed up: its chats' conversations cannot be read.");
        }

        text.Append(commits.Count == 0 ? "No commits were made then.\n"
            : "Commits made then:\n" + string.Join('\n', commits.Select(c => $"{c.Workspace}: {string.Join("; ", c.Subjects)}")) + "\n");
        logger.LogInformation("Summing up the working session from {Start} to {End}: {Chats} chats, {Commits} workspaces with commits",
            session.Start, session.End, told, commits.Count);
        return text.ToString().TrimEnd();
    }

    /// <summary>The commits each workspace's folders got in the session, on any branch, merges aside; workspaces with none are left out.</summary>
    private async Task<List<(string Workspace, IReadOnlyList<string> Subjects)>> CommitsAsync(IReadOnlyList<YardWorkspace> workspaces, WorkSession session,
        CancellationToken ct)
    {
        var range = $"--since=\"{Git(session.Start)}\" --until=\"{Git(session.End)}\"";
        var found = await Task.WhenAll(workspaces.Select(async w =>
        {
            var folders = w.Folders.Count > 0 ? w.Folders.Select(f => f.Path) : [w.RootPath];
            var subjects = new List<string>();
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var log = await git(folder, $"log --all --no-merges {range} --pretty=format:%s -n {MaxCommits}", ct).ConfigureAwait(false);
                subjects.AddRange((log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            return (Workspace: w.Name, Subjects: (IReadOnlyList<string>)subjects.Distinct().Take(MaxCommits).ToList());
        })).ConfigureAwait(false);
        return [.. found.Where(f => f.Subjects.Count > 0)];
    }

    private static string Git(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss +0000", CultureInfo.InvariantCulture);

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);

    /// <summary>"yesterday afternoon", "on Friday evening", "earlier today, in the morning", "on 2 October".</summary>
    internal string When(DateTimeOffset start, DateTimeOffset now)
    {
        var at = Local(start);
        var days = (Local(now).Date - at.Date).Days;
        var part = at.Hour switch
        {
            < 5 => "night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 22 => "evening",
            _ => "night",
        };
        return days switch
        {
            0 => $"earlier today, in the {part}",
            1 when part == "night" => $"last night ({at.ToString("dddd d MMMM", CultureInfo.InvariantCulture)})",
            1 => $"yesterday {part} ({at.ToString("dddd d MMMM", CultureInfo.InvariantCulture)})",
            < 7 => $"on {at.ToString("dddd", CultureInfo.InvariantCulture)} {part} ({at.ToString("d MMMM", CultureInfo.InvariantCulture)})",
            _ => $"on {at.ToString("dddd d MMMM", CultureInfo.InvariantCulture)}, in the {part}",
        };
    }
}
