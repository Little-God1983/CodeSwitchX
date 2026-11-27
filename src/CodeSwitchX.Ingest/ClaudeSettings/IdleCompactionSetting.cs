using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.ClaudeSettings;

/// <param name="Enabled">Whether Claude Code compacts an idle chat before its cache runs out.</param>
/// <param name="MinTokens">
/// The context size, in tokens, from which it does; null when the file holds a size this app cannot be sure how Claude Code
/// reads (review of #450).
/// </param>
public sealed record IdleCompaction(bool Enabled, int? MinTokens);

/// <summary>
/// Claude Code's own compaction of an idle chat, as its user-level settings.json holds it: <c>idleCompaction</c> and the size
/// in <c>env</c>. CodeSwitchX stores neither and compacts nothing itself; it reads the file as Claude Code does and writes only
/// the value that changed (#450).
/// </summary>
public sealed class IdleCompactionSetting
{
    public const int DefaultMinTokens = 200_000;

    /// <summary>Claude Code raises a lower size to this.</summary>
    public const int LowestMinTokens = 100_000;

    internal const string Key = "idleCompaction";
    internal const string Env = "env";
    internal const string EnvKey = "CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS";

    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeSettingsFile _file;
    private readonly ILogger<IdleCompactionSetting> _logger;

    public IdleCompactionSetting(ClaudeCodePaths paths, ILogger<IdleCompactionSetting> logger)
    {
        _paths = paths;
        _file = new ClaudeSettingsFile(paths, logger);
        _logger = logger;
    }

    /// <summary>What Claude Code will do, as the file says. Throws <see cref="ClaudeSettingsException"/>.</summary>
    public IdleCompaction Read() => Of(_file.Load(Touch));

    /// <summary>
    /// Writes the given values where they differ from <see cref="Read"/>; null leaves that one alone, so a value changed in the
    /// file meanwhile is not overwritten by the other control (review of #450). A value the file holds in a form of its own
    /// stays as it is until it is changed. Whether the file was written. Throws <see cref="ClaudeSettingsException"/>.
    /// </summary>
    public bool Write(bool? enabled, int? minTokens)
    {
        if (minTokens is { } tokens)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(tokens, LowestMinTokens, nameof(minTokens));
        }

        // One writer of settings.json at a time: the hook installer writes it too (review of #450).
        return _file.Locked(() => WriteAlone(enabled, minTokens));
    }

    private bool WriteAlone(bool? enabled, int? minTokens)
    {
        var settings = _file.Load(Touch);
        var before = ClaudeSettingsFile.TextOf(settings);
        var now = Of(settings);
        List<string> written = [];

        // A size the file holds in a form this app cannot be sure of reads as null, which differs from any size given.
        if (minTokens is { } tokens && tokens != now.MinTokens)
        {
            SetMinTokens(settings, tokens);
            written.Add(string.Create(CultureInfo.InvariantCulture, $"from {tokens} tokens"));
        }

        if (enabled is { } on && on != now.Enabled)
        {
            if (on)
            {
                settings.Remove(Key);
            }
            else
            {
                settings[Key] = false;
            }

            written.Add(on ? "on" : "off");
        }

        // Nothing changed: no backup, and no file created where there was none.
        if (ClaudeSettingsFile.TextOf(settings) == before)
        {
            return false;
        }

        _file.Save(settings, before);
        _logger.LogInformation("Idle compaction in {File}: {Written}", _paths.SettingsFile, string.Join(", ", written));
        return true;
    }

    /// <summary>
    /// 200,000 is Claude Code's default, so it is the entry removed (and <c>env</c> with it, if that emptied it). An <c>env</c>
    /// that is no object is refused rather than replaced: it is the user's, and Claude Code reads other variables from it.
    /// </summary>
    private void SetMinTokens(JsonObject settings, int minTokens)
    {
        settings.TryGetPropertyValue(Env, out var node);
        var env = node as JsonObject;
        if (minTokens == DefaultMinTokens)
        {
            if (env is not null)
            {
                env.Remove(EnvKey);
                if (env.Count == 0)
                {
                    settings.Remove(Env);
                }
            }

            return;
        }

        if (env is null)
        {
            if (settings.ContainsKey(Env))
            {
                throw new ClaudeSettingsException($"{_paths.SettingsFile}: \"{Env}\" is not an object, so the size cannot be saved there.");
            }

            env = [];
            settings[Env] = env;
        }

        env[EnvKey] = minTokens.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Claude Code's reading: off only for the boolean false; the size a plain integer, raised to the lowest.</summary>
    private static IdleCompaction Of(JsonObject settings)
    {
        var enabled = settings[Key] is not JsonValue flag || flag.GetValueKind() != JsonValueKind.False;
        return new IdleCompaction(enabled, MinTokensOf(settings));
    }

    /// <summary>
    /// The size as Claude Code 2.1.332 reads a plain integer (review of #450): the entry trimmed, an optional sign and digits;
    /// below 1 it counts as unset, anything else is raised to <see cref="LowestMinTokens"/>. Any other form (grouped digits,
    /// an exponent, not a string) is null: Claude Code accepts some of them, and this does not guess how.
    /// </summary>
    private static int? MinTokensOf(JsonObject settings)
    {
        if (settings[Env] is not JsonObject env || !env.TryGetPropertyValue(EnvKey, out var entry))
        {
            return DefaultMinTokens;
        }

        if (entry is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetValue<string>().Trim();
        var negative = text.StartsWith('-');
        var digits = negative || text.StartsWith('+') ? text[1..] : text;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        digits = digits.TrimStart('0');
        if (digits.Length == 0 || negative)
        {
            return DefaultMinTokens; // zero or below: unset
        }

        // More digits than int.MaxValue has is above it, whatever they are; a long holds any ten.
        return digits.Length > 10
            ? int.MaxValue
            : (int)Math.Clamp(long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture), LowestMinTokens, int.MaxValue);
    }

    /// <summary>The root and <c>env</c>, all this reads or edits, so a repeated key there is refused when the file is loaded.</summary>
    private static void Touch(JsonObject settings)
    {
        _ = settings.Count;
        if (settings[Env] is JsonObject env)
        {
            _ = env.Count;
        }
    }
}
