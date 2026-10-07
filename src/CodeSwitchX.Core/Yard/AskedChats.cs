namespace CodeSwitchX.Core.Yard;

/// <summary>
/// The Raven chats whose brain is in a turn the user's own question started (#193). A message from another Claude session
/// starts a turn too, with all of the brain's tools; what such a message asks is no word of the user's, so the Yard's tools
/// that act refuse a Raven chat that is not in its user's question. A chat is known by what its brain sends as
/// <see cref="YardMcp.ChatHeader"/>. Thread-safe.
/// </summary>
public sealed class AskedChats
{
    private readonly Dictionary<string, int> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unverified = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The chat's brain is in its user's question's turn, until as many <see cref="End"/>s.</summary>
    public void Begin(string chat)
    {
        lock (_gate)
        {
            _asked[chat] = _asked.GetValueOrDefault(chat) + 1;
        }
    }

    public void End(string chat)
    {
        lock (_gate)
        {
            if (_asked.TryGetValue(chat, out var count))
            {
                if (count > 1)
                {
                    _asked[chat] = count - 1;
                }
                else
                {
                    _asked.Remove(chat);
                }
            }
        }
    }

    public bool IsAsked(string chat)
    {
        lock (_gate)
        {
            return _asked.ContainsKey(chat);
        }
    }

    /// <summary>
    /// The chat's brain answers a question it cannot tell from one the user cancelled, as its Claude Code sends no
    /// question ids back (#199): what acts is refused for that reason, not for a chat's message. Until it sees one again.
    /// </summary>
    public void Unverified(string chat, bool unverified)
    {
        lock (_gate)
        {
            if (unverified)
            {
                _unverified.Add(chat);
            }
            else
            {
                _unverified.Remove(chat);
            }
        }
    }

    public bool IsUnverified(string chat)
    {
        lock (_gate)
        {
            return _unverified.Contains(chat);
        }
    }

    /// <summary>True the first time it is called for <paramref name="what"/>: a warning said once for the whole app.</summary>
    public bool FirstTime(string what)
    {
        lock (_gate)
        {
            return _said.Add(what);
        }
    }
}
