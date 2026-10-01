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
}
