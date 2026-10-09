using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Core.Sessions;

/// <summary>A stretch of work: from its first minute with a chat at work to its last, and how many minutes each chat worked in it.</summary>
public sealed record WorkSession(DateTimeOffset Start, DateTimeOffset End, IReadOnlyDictionary<string, int> MinutesByChat);

/// <summary>
/// The user's working sessions (#237), from the minutes the chats used tokens in (<see cref="UsageBucket"/>): a session
/// ends at a break of <see cref="Break"/> or more, when no chat worked at all.
/// </summary>
public static class WorkSessions
{
    /// <summary>A break this long or longer ends a session: a lunch does not, a night does.</summary>
    public static readonly TimeSpan Break = TimeSpan.FromHours(4);

    /// <summary>
    /// The last session before now: the one before the session going on, when chats worked within <see cref="Break"/> of
    /// now, else the latest. Null when there is none.
    /// </summary>
    /// <param name="counts">Whether a chat's minutes count: a chat of the user's workspaces, not Raven's own brains.</param>
    public static WorkSession? Last(IEnumerable<UsageBucket> buckets, Func<string, bool> counts, DateTimeOffset now)
    {
        var minutes = buckets.Where(b => counts(b.SessionId)).GroupBy(b => b.MinuteUtc).OrderBy(g => g.Key).ToList();
        if (minutes.Count == 0)
        {
            return null;
        }

        var sessions = new List<List<IGrouping<DateTimeOffset, UsageBucket>>>();
        foreach (var minute in minutes)
        {
            // The idle time between two minutes with work: from the end of the one to the start of the next.
            if (sessions.Count == 0 || minute.Key - (sessions[^1][^1].Key + OneMinute) >= Break)
            {
                sessions.Add([]);
            }

            sessions[^1].Add(minute);
        }

        var going = now - (sessions[^1][^1].Key + OneMinute) < Break;
        if (going && sessions.Count < 2)
        {
            return null;
        }

        var last = sessions[going ? ^2 : ^1];
        var byChat = last.SelectMany(m => m.Select(b => b.SessionId).Distinct()).GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        return new WorkSession(last[0].Key, last[^1].Key + OneMinute, byChat);
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
}
