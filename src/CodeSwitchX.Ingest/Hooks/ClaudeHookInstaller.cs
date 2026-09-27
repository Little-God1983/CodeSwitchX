using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Merges csx-hook entries into the user-level Claude Code settings.json. Our entries are the ones whose
/// command contains <see cref="Marker"/>; nothing else in the file is ever touched.
/// </summary>
public sealed class ClaudeHookInstaller
{
    public const string Marker = "csx-hook";
    public const int TimeoutSeconds = 5;

    public static readonly string[] Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "StopFailure", "SubagentStop", "SessionEnd",
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ClaudeCodePaths _paths;
    private readonly ILogger<ClaudeHookInstaller> _logger;

    public ClaudeHookInstaller(ClaudeCodePaths paths, ILogger<ClaudeHookInstaller> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public HookInstallStatus GetStatus(string relayExecutable)
    {
        JsonObject settings;
        try
        {
            settings = Load();
        }
        catch (HookInstallException)
        {
            return new HookInstallStatus(HookInstallState.NotInstalled, [], Events, _paths.SettingsFile);
        }

        var installed = new List<string>();
        var outdated = false;
        foreach (var eventName in Events)
        {
            var ours = OurHooks(settings, eventName).ToList();
            if (ours.Count == 0)
            {
                continue;
            }

            installed.Add(eventName);
            if (!ours.All(h => IsCurrent(h, relayExecutable, eventName)))
            {
                outdated = true;
            }
        }

        var missing = Events.Except(installed).ToList();
        var state = installed.Count == 0 ? HookInstallState.NotInstalled
            : missing.Count > 0 ? HookInstallState.Partial
            : outdated ? HookInstallState.Outdated
            : HookInstallState.Installed;
        return new HookInstallStatus(state, installed, missing, _paths.SettingsFile);
    }

    public HookInstallResult Install(string relayExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relayExecutable);
        if (!relayExecutable.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            // Our entries are found by the marker alone: without it each install would add another set and none could be removed.
            throw new HookInstallException($"The relay path must contain \"{Marker}\" (the relay is {Marker}.exe): {relayExecutable}");
        }

        var settings = Load();
        var before = settings.ToJsonString(WriteOptions);

        var hooks = settings["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = [];
            settings["hooks"] = hooks;
        }

        foreach (var eventName in Events)
        {
            var groups = hooks[eventName] as JsonArray;
            if (groups is null)
            {
                groups = [];
                hooks[eventName] = groups;
            }

            var ours = OurHooks(settings, eventName).ToList();
            if (ours.Count == 0)
            {
                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = relayExecutable,
                        ["args"] = new JsonArray(eventName),
                        ["timeout"] = TimeoutSeconds,
                    }),
                });
            }
            else
            {
                foreach (var hook in ours)
                {
                    hook["command"] = relayExecutable;
                    hook["args"] = new JsonArray(eventName);
                }
            }
        }

        return Save(settings, before);
    }

    public HookInstallResult Uninstall()
    {
        var settings = Load();
        var before = settings.ToJsonString(WriteOptions);

        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var eventName in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray groups)
                {
                    continue;
                }

                foreach (var group in groups.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is JsonArray entries)
                    {
                        foreach (var entry in entries.OfType<JsonObject>().Where(IsOurs).ToList())
                        {
                            entries.Remove(entry);
                        }

                        if (entries.Count == 0)
                        {
                            groups.Remove(group);
                        }
                    }
                }

                if (groups.Count == 0)
                {
                    hooks.Remove(eventName);
                }
            }

            if (hooks.Count == 0)
            {
                settings.Remove("hooks");
            }
        }

        return Save(settings, before);
    }

    private static IEnumerable<JsonObject> OurHooks(JsonObject settings, string eventName)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[eventName] is not JsonArray groups)
        {
            yield break;
        }

        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>().Where(IsOurs))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Exec form: Claude Code spawns <c>command</c> with <c>args</c> directly. A shell-form command ("path" Event) only runs
    /// in bash; without Git Bash Claude Code hands it to PowerShell, which cannot run a quoted path without its call operator.
    /// </summary>
    private static bool IsCurrent(JsonObject hook, string relayExecutable, string eventName) =>
        hook["command"] is JsonValue command
        && command.TryGetValue<string>(out var path)
        && string.Equals(path, relayExecutable, StringComparison.OrdinalIgnoreCase)
        && hook["args"] is JsonArray { Count: 1 } args
        && args[0] is JsonValue arg
        && arg.TryGetValue<string>(out var argument)
        && argument == eventName;

    private static bool IsOurs(JsonObject hook) =>
        hook["command"] is JsonValue value
        && value.TryGetValue<string>(out var command)
        && command.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private JsonObject Load()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(_paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HookInstallException($"Cannot read {_paths.SettingsFile}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new HookInstallException($"{_paths.SettingsFile} is empty; refusing to overwrite it. Fix or delete the file and retry.");
        }

        try
        {
            // A repeated key is refused while parsing; JsonObject would otherwise throw an ArgumentException on first access.
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false };
            return JsonNode.Parse(text, documentOptions: options) as JsonObject
                ?? throw new HookInstallException($"{_paths.SettingsFile} does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new HookInstallException($"{_paths.SettingsFile} is not valid JSON: {ex.Message}", ex);
        }
    }

    private HookInstallResult Save(JsonObject settings, string before)
    {
        var after = settings.ToJsonString(WriteOptions);
        if (after == before && File.Exists(_paths.SettingsFile))
        {
            return new HookInstallResult(false, null);
        }

        string? backup = null;
        var tmp = _paths.SettingsFile + ".csx-tmp";
        try
        {
            Directory.CreateDirectory(_paths.ClaudeDirectory);
            if (File.Exists(_paths.SettingsFile))
            {
                backup = $"{_paths.SettingsFile}.csx-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
                File.Copy(_paths.SettingsFile, backup, overwrite: true);
            }

            File.WriteAllText(tmp, after + Environment.NewLine);
            File.Move(tmp, _paths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only, locked by an editor or by Claude Code writing it: settings.json itself is unchanged.
            TryDelete(tmp);
            throw new HookInstallException($"Cannot write {_paths.SettingsFile}: {ex.Message}", ex);
        }

        _logger.LogInformation("Updated {File} (backup: {Backup})", _paths.SettingsFile, backup ?? "none");
        return new HookInstallResult(true, backup);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
