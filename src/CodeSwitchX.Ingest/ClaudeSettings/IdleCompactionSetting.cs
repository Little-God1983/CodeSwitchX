using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.ClaudeSettings;

/// <param name="Enabled">Whether Claude Code compacts an idle chat before its cache runs out.</param>
/// <param name="MinTokens">The context size, in tokens, from which it does.</param>
public sealed record IdleCompaction(bool Enabled, int MinTokens);

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
    /// Writes what differs from <see cref="Read"/>, and nothing else: a value the file holds in a form of its own stays as it
    /// is until it is changed. Whether the file was written. Throws <see cref="ClaudeSettingsException"/>.
    /// </summary>
    public bool Write(IdleCompaction value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value.MinTokens, LowestMinTokens);

        var settings = _file.Load(Touch);
        var before = ClaudeSettingsFile.TextOf(settings);
        var now = Of(settings);

        if (value.MinTokens != now.MinTokens)
        {
            SetMinTokens(settings, value.MinTokens);
        }

        if (value.Enabled != now.Enabled)
        {
            if (value.Enabled)
            {
                settings.Remove(Key);
            }
            else
            {
                settings[Key] = false;
            }
        }

        // Nothing changed: no backup, and no file created where there was none.
        if (ClaudeSettingsFile.TextOf(settings) == before)
        {
            return false;
        }

        _file.Save(settings, before);
        _logger.LogInformation("Idle compaction in {File}: {Enabled}, from {MinTokens} tokens", _paths.SettingsFile, value.Enabled, value.MinTokens);
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

    /// <summary>Claude Code's reading: off only for the boolean false; the size an integer, raised to the lowest.</summary>
    private static IdleCompaction Of(JsonObject settings)
    {
        var enabled = settings[Key] is not JsonValue flag || flag.GetValueKind() != JsonValueKind.False;
        return new IdleCompaction(enabled, MinTokensOf(settings));
    }

    private static int MinTokensOf(JsonObject settings)
    {
        if (settings[Env] is not JsonObject env || env[EnvKey] is not JsonValue entry)
        {
            return DefaultMinTokens;
        }

        long tokens;
        switch (entry.GetValueKind())
        {
            case JsonValueKind.String:
                if (!long.TryParse(entry.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out tokens))
                {
                    return DefaultMinTokens;
                }

                break;
            case JsonValueKind.Number:
                if (!entry.TryGetValue(out tokens))
                {
                    return DefaultMinTokens;
                }

                break;
            default:
                return DefaultMinTokens;
        }

        return (int)Math.Clamp(tokens, LowestMinTokens, int.MaxValue);
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
