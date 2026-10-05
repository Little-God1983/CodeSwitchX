using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Conductor;

/// <summary>
/// What a chat Raven starts runs with, unless it is said for that one: the model and the effort, and the alias table that
/// turns a spoken model name into an id. Settings loads and edits it; the brain's tools read it from any thread.
/// </summary>
public sealed class ChatSettings
{
    private volatile IReadOnlyList<ModelAlias> _aliases = ChatModels.DefaultAliases;
    private volatile ChatDefaults _defaults = new(null, null);

    public IReadOnlyList<ModelAlias> Aliases
    {
        get => _aliases;
        set => _aliases = value is { Count: > 0 } ? value : ChatModels.DefaultAliases;
    }

    /// <summary>The model as an alias name or id, and the effort level; null for Claude Code's own default.</summary>
    public ChatDefaults Defaults
    {
        get => _defaults;
        set => _defaults = new ChatDefaults(Blank(value.Model), Blank(value.Effort));
    }

    /// <summary>The full id of the default model, through the alias table; null for Claude Code's default, or a name it no longer knows.</summary>
    public string? DefaultModelId => Defaults.Model is { } model ? ChatModels.ResolveModel(model, Aliases) : null;

    /// <summary>A model or effort as given, trimmed; none for an empty one, which tool callers and empty boxes send for "none".</summary>
    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The model new chats start with, from what the user said; set_defaults and set_setting both store it so (#141). Null
    /// for "default" (Claude Code's own); the alias name when the user said just that ("Opus"), so a new id for it in the
    /// table applies; else the id. A version or id said ("Opus 5.5") names that one model, which a later change of the
    /// table must not swap. Throws <see cref="YardActionException"/> for a name Raven does not know.
    /// </summary>
    public string? DefaultModelOf(string said) =>
        ChatModels.IsDefault(said) ? null : ChatModels.AliasNamed(said, Aliases)?.Name ?? ModelIdOf(said);

    /// <summary>The model id what the user said means; throws <see cref="YardActionException"/> for a name Raven does not know.</summary>
    public string ModelIdOf(string said) => ChatModels.ResolveModel(said, Aliases)
        ?? throw new YardActionException($"'{said}' is no model Raven knows: say {string.Join(", ", Aliases.Select(a => a.Name))}, the name "
            + "with its version, or a full model id. Nothing was changed.");

    /// <summary>The effort new chats start with, from what the user said: null for "default" (Claude Code's own).</summary>
    public static string? DefaultEffortOf(string said) => ChatModels.IsDefault(said) ? null : EffortOf(said);

    /// <summary>The effort level what the user said means ("extra high" is xhigh); throws <see cref="YardActionException"/> for none.</summary>
    public static string EffortOf(string said) => ChatModels.ResolveEffort(said)
        ?? throw new YardActionException($"'{said}' is no effort level: say {string.Join(", ", ChatModels.EffortLevels)}. Nothing was changed.");
}
