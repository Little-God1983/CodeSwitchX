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
/// <param name="stored">The chats saved, with their workspace and conversation: the app keeps in mind only those of the last day it started with.</param>
/// <param name="sessions">The chats the app knows now (<c>SessionEngine.Snapshots</c>), fresher than those saved.</param>
/// <param name="git">Runs git in a folder with the arguments given; its output, or null when it failed.</param>
public sealed class SessionRecaps(IConductorBrain recapper, IUsageStore usage, ISessionStore stored, Func<IReadOnlyCollection<SessionSnapshot>> sessions, IYardDirectory yard,
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

    /// <summary>How long after its last chat's work a commit still counts to the session: one made by hand, say.</summary>
    internal static readonly TimeSpan CommitSlack = TimeSpan.FromMinutes(15);

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

    /// <summary>A chat that may have worked in the session: its workspace, title and conversation.</summary>
    private sealed record ChatFacts(Guid? WorkspaceId, string? Title, string? TranscriptPath);

    /// <summary>What the recapper is given: when the session was, what each chat did in it, and the commits made then.</summary>
    internal async Task<string> QuestionAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The saved chats of the whole look-back, the app's own fresher where it has them: after a restart it keeps in mind
        // only the chats of the day before, and a Friday's must count on a Monday.
        var chats = new Dictionary<string, ChatFacts>(StringComparer.Ordinal);
        foreach (var record in await stored.GetActiveSinceAsync(now - LookBack, ct).ConfigureAwait(false))
        {
            if (record.WorkspaceId is not null)
            {
                chats[record.Id] = new ChatFacts(record.WorkspaceId, record.Title, record.TranscriptPath);
            }
        }

        foreach (var snapshot in sessions().Where(s => s.WorkspaceId is not null))
        {
            chats[snapshot.SessionId] = new ChatFacts(snapshot.WorkspaceId, snapshot.Title, snapshot.TranscriptPath);
        }

        var buckets = await usage.GetBucketsAsync(now - LookBack, now, ct).ConfigureAwait(false);
        var session = WorkSessions.Last(buckets, chats.ContainsKey, now)
            ?? throw new YardActionException($"No working session before this one shows in the last {LookBack.TotalDays:0} days.");
        var workspaces = await yard.WorkspacesAsync(ct).ConfigureAwait(false);
        string NameOf(Guid? id) => workspaces.FirstOrDefault(w => w.Id == id)?.Name ?? "a workspace no longer on the Yard";

        var text = new StringBuilder();
        text.Append("It was ").Append(When(session.Start, now)).Append(", from ").Append(Local(session.Start).ToString("HH:mm", CultureInfo.InvariantCulture))
            .Append(" to ").Append(Local(session.End).ToString("HH:mm", CultureInfo.InvariantCulture)).Append(".\n\n");

        // The chats that worked the longest; one whose conversation cannot be read makes room for the next.
        var told = 0;
        foreach (var (id, minutes) in session.MinutesByChat.OrderByDescending(c => c.Value))
        {
            var chat = chats[id];
            if (told == MaxChats
                || TranscriptDigest.ReadBetween(chat.TranscriptPath, session.Start - TimeSpan.FromMinutes(1), session.End, ChatChars, ct) is not { } steps)
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

    /// <summary>
    /// The user's own commits in each workspace's folders during the session, on its local branches (no one else's fetched
    /// ones, no stash), merges aside; a folder two workspaces share counts for the first; workspaces with none are left out.
    /// </summary>
    private async Task<List<(string Workspace, IReadOnlyList<string> Subjects)>> CommitsAsync(IReadOnlyList<YardWorkspace> workspaces, WorkSession session,
        CancellationToken ct)
    {
        var range = $"--since=\"{Git(session.Start)}\" --until=\"{Git(session.End + CommitSlack)}\"";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var each = workspaces.Select(w => (w.Name, Folders: (w.Folders.Count > 0 ? w.Folders.Select(f => f.Path) : [w.RootPath]).Where(seen.Add).ToList()))
            .ToList();
        var found = await Task.WhenAll(each.Select(async w =>
        {
            var subjects = new List<string>();
            foreach (var folder in w.Folders)
            {
                var author = (await git(folder, "config user.email", ct).ConfigureAwait(false))?.Trim() is { Length: > 0 } email
                    ? $" --author=\"{email.Replace("\"", "")}\"" : "";
                var log = await git(folder, $"log --branches --no-merges{author} {range} --pretty=format:%s -n {MaxCommits}", ct).ConfigureAwait(false);
                subjects.AddRange((log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            return (Workspace: w.Name, Subjects: (IReadOnlyList<string>)subjects.Distinct().Take(MaxCommits).ToList());
        })).ConfigureAwait(false);
        return [.. found.Where(f => f.Subjects.Count > 0)];
    }

    private static string Git(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss +0000", CultureInfo.InvariantCulture);

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);

    /// <summary>"yesterday afternoon", "last night", "on Friday evening", "earlier today, in the morning", "on 2 October".</summary>
    internal string When(DateTimeOffset start, DateTimeOffset now)
    {
        var at = Local(start);
        var part = at.Hour switch
        {
            < 5 => "night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 22 => "evening",
            _ => "night",
        };

        // The small hours belong to the night before: 01:00 on Thursday is Wednesday night.
        var day = at.Hour < 5 ? at.Date.AddDays(-1) : at.Date;
        var named = day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        return (Local(now).Date - day).Days switch
        {
            <= 0 => $"earlier today, in the {part}",
            1 when part == "night" => $"last night ({named})",
            1 => $"yesterday {part} ({named})",
            < 7 => $"on {day.ToString("dddd", CultureInfo.InvariantCulture)} {part} ({day.ToString("d MMMM", CultureInfo.InvariantCulture)})",
            _ => $"on {named}, in the {part}",
        };
    }
}
