using System.Text.Json;
using CodeSwitchX.Core;

namespace CodeSwitchX.Conductor;

/// <summary>A chat's brain conversation, as Claude Code keeps it: resumed by its id while it is younger than the quiet reset.</summary>
/// <param name="Model">The model it was held with: one set since starts a new conversation.</param>
/// <param name="LastTurnAt">When its last turn ended.</param>
public sealed record BrainSession(string Id, string Model, DateTimeOffset LastTurnAt);

/// <summary>Where each chat's brain conversation is kept, so a resume survives an app restart. Called off the UI thread; never throws.</summary>
public interface IBrainSessionStore
{
    BrainSession? Load(string chat);

    /// <summary>Keeps the chat's conversation; null forgets it.</summary>
    void Save(string chat, BrainSession? session);
}

/// <summary>The conversations in a small JSON file in Raven's folder: a chat key to its session.</summary>
public sealed class BrainSessionFile(string file) : IBrainSessionStore
{
    private readonly Lock _gate = new();
    private Dictionary<string, BrainSession>? _sessions;

    public BrainSession? Load(string chat)
    {
        lock (_gate)
        {
            return All().GetValueOrDefault(chat);
        }
    }

    public void Save(string chat, BrainSession? session)
    {
        lock (_gate)
        {
            var all = All();
            if (session is null)
            {
                all.Remove(chat);
            }
            else
            {
                all[chat] = session;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.Replace(file, JsonSerializer.Serialize(all), ".tmp");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory: only a resume after a restart is lost.
            }
        }
    }

    private Dictionary<string, BrainSession> All()
    {
        if (_sessions is null)
        {
            try
            {
                _sessions = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, BrainSession>>(File.ReadAllText(file)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _sessions = null; // unreadable: every chat starts a new conversation
            }

            _sessions ??= [];
        }

        return _sessions;
    }
}
