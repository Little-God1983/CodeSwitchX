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

    /// <summary>How many <c>settings.json.csx-backup-*</c> files stay next to settings.json; older ones go when a save makes a new one.</summary>
    public const int BackupsKept = 5;
    private const string TempSuffix = ".csx-tmp";
    private const string BackupSuffix = ".csx-backup-";

    /// <summary>
    /// The spec's eight events, StopFailure (a turn that ends on an API error) and PermissionRequest, which fires the moment a
    /// permission prompt opens; the permission_prompt Notification follows 6 s later, and not at all when it is answered sooner.
    /// </summary>
    public static readonly string[] Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "StopFailure", "SubagentStop", "SessionEnd",
    ];

    /// <summary>
    /// The relay's argument for the second PreToolUse entry, which only a chat's question runs (<see cref="AskMatcher"/>): it
    /// hands the question to CodeSwitchX and waits, up to <see cref="AskTimeoutSeconds"/>, for the user to answer it there.
    /// </summary>
    public const string AskArgument = "Ask";

    public const string AskEvent = "PreToolUse";

    public const string AskMatcher = "AskUserQuestion";

    /// <summary>Above CodeSwitchX's own ten minutes, which let the question go to VS Code first.</summary>
    public const int AskTimeoutSeconds = 660;

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
        catch (HookInstallException ex)
        {
            return new HookInstallStatus(HookInstallState.Unreadable, [], Events, _paths.SettingsFile, ex.Message);
        }

        var installed = new List<string>();
        var outdated = false;
        foreach (var eventName in Events)
        {
            var ours = OurHooks(settings, eventName).Where(h => !IsAsk(h)).ToList();
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

        // Without its entry for questions, an install from before them asks every question in VS Code: one more to update.
        var asks = OurAsks(settings).ToList();
        if (asks.Count != 1 || !IsCurrentAsk(asks[0].Group, asks[0].Hook, relayExecutable))
        {
            outdated = true;
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
        // Our entries are found by the marker alone: without it each install would add another set and none could be removed.
        if (string.IsNullOrWhiteSpace(relayExecutable) || !relayExecutable.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            throw new HookInstallException($"The relay path must lead to {Marker}.exe: \"{relayExecutable}\"");
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

            var ours = OurHooks(settings, eventName).Where(h => !IsAsk(h)).ToList();
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

        InstallAsk(hooks, settings, relayExecutable);
        return Save(settings, before);
    }

    /// <summary>
    /// The entry for questions, in a group of its own with the matcher: exactly one, current. Ours elsewhere (a second one,
    /// one moved into a group without the matcher) go, so no question is asked twice or every tool waits.
    /// </summary>
    private static void InstallAsk(JsonObject hooks, JsonObject settings, string relayExecutable)
    {
        var asks = OurAsks(settings).ToList();
        var keep = asks.Where(a => Matches(a.Group)).Select(a => a.Hook).FirstOrDefault();
        foreach (var (group, hook) in asks.Where(a => !ReferenceEquals(a.Hook, keep)))
        {
            var entries = (JsonArray)group["hooks"]!;
            entries.Remove(hook);
            if (entries.Count == 0)
            {
                (hooks[AskEvent] as JsonArray)?.Remove(group);
            }
        }

        if (keep is not null)
        {
            keep["type"] = "command";
            keep["command"] = relayExecutable;
            keep["args"] = new JsonArray(AskArgument);
            keep["timeout"] = AskTimeoutSeconds;
            return;
        }

        if (hooks[AskEvent] is not JsonArray groups)
        {
            groups = [];
            hooks[AskEvent] = groups;
        }

        groups.Add(new JsonObject
        {
            ["matcher"] = AskMatcher,
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = relayExecutable,
                ["args"] = new JsonArray(AskArgument),
                ["timeout"] = AskTimeoutSeconds,
            }),
        });
    }

    private static bool Matches(JsonObject group) =>
        group["matcher"] is JsonValue matcher && matcher.TryGetValue<string>(out var text) && text == AskMatcher;

    private static bool IsCurrentAsk(JsonObject group, JsonObject hook, string relayExecutable) =>
        Matches(group)
        && hook["command"] is JsonValue command && command.TryGetValue<string>(out var path) && string.Equals(path, relayExecutable, StringComparison.OrdinalIgnoreCase)
        && hook["timeout"] is JsonValue timeout && timeout.TryGetValue<int>(out var seconds) && seconds == AskTimeoutSeconds;

    /// <summary>Our entry for questions: the relay run with <see cref="AskArgument"/>.</summary>
    private static bool IsAsk(JsonObject hook) =>
        hook["args"] is JsonArray { Count: 1 } args && args[0] is JsonValue arg && arg.TryGetValue<string>(out var argument) && argument == AskArgument;

    private static IEnumerable<(JsonObject Group, JsonObject Hook)> OurAsks(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[AskEvent] is not JsonArray groups)
        {
            yield break;
        }

        foreach (var group in groups.OfType<JsonObject>())
        {
            foreach (var entry in (group["hooks"] as JsonArray ?? []).OfType<JsonObject>().Where(h => IsOurs(h) && IsAsk(h)))
            {
                yield return (group, entry);
            }
        }
    }

    public HookInstallResult Uninstall()
    {
        var settings = Load();
        var before = settings.ToJsonString(WriteOptions);

        if (settings["hooks"] is not JsonObject hooks || RemoveOurs(hooks) == 0)
        {
            // Nothing of ours: the file stays as it is, so a missing one is not created and a foreign empty "hooks" stays.
            return new HookInstallResult(false, null);
        }

        if (hooks.Count == 0)
        {
            settings.Remove("hooks");
        }

        return Save(settings, before);
    }

    /// <summary>Removes our entries, and the groups and events only they filled; a group or event that was empty before is not ours to drop.</summary>
    private static int RemoveOurs(JsonObject hooks)
    {
        var removed = 0;
        foreach (var eventName in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            var emptied = false;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray entries)
                {
                    continue;
                }

                var ours = entries.OfType<JsonObject>().Where(IsOurs).ToList();
                foreach (var entry in ours)
                {
                    entries.Remove(entry);
                }

                removed += ours.Count;
                if (ours.Count > 0 && entries.Count == 0)
                {
                    groups.Remove(group);
                    emptied = true;
                }
            }

            if (emptied && groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        return removed;
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
    /// The exec form needs Claude Code 2.1.139 or later (StopFailure exists since 2.1.78).
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

        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new HookInstallException($"{_paths.SettingsFile} does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new HookInstallException($"{_paths.SettingsFile} is not valid JSON: {ex.Message}", ex);
        }

        try
        {
            ReadHookEntries(settings);
            _ = settings.ToJsonString(WriteOptions);
        }
        catch (ArgumentException ex)
        {
            // Claude Code keeps the last of a repeated key. Only the parts the installer reads must not repeat one; the rest
            // is never read and is written back as it was.
            throw new HookInstallException($"{_paths.SettingsFile} repeats a key where CodeSwitchX edits it: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // A lone surrogate escape (half an emoji) anywhere in the file, in a key or a value: the key throws when its object
            // is first read, the value when the file is written back; either failed the status and with it the start.
            throw new HookInstallException($"{_paths.SettingsFile} holds an escape sequence that cannot be read: {ex.Message}", ex);
        }

        return settings;
    }

    /// <summary>
    /// Reads the root and every hook entry, which is all the installer touches. JsonObject reads its keys on first use and
    /// throws on a repeated one, so this makes that happen here rather than halfway through an install.
    /// </summary>
    private static void ReadHookEntries(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (var (_, groups) in hooks)
        {
            foreach (var group in (groups as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var entry in (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    _ = entry.Count;
                }
            }
        }
    }

    private HookInstallResult Save(JsonObject settings, string before)
    {
        var after = settings.ToJsonString(WriteOptions);
        if (after == before && File.Exists(_paths.SettingsFile))
        {
            return new HookInstallResult(false, null);
        }

        // A linked settings.json (dotfiles setups) is written where it points, so the link stays and its repository sees the change.
        var target = LinkTarget(_paths.SettingsFile);
        string? backup = null;
        try
        {
            Directory.CreateDirectory(_paths.ClaudeDirectory);
            if (File.Exists(_paths.SettingsFile))
            {
                backup = $"{_paths.SettingsFile}{BackupSuffix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
                File.Copy(_paths.SettingsFile, backup, overwrite: true);
            }

            AtomicFile.Replace(target, after + Environment.NewLine, TempSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only, locked by an editor or by Claude Code writing it: settings.json itself is unchanged, so the backup of
            // it is not kept either (the temporary file is already gone).
            if (backup is not null)
            {
                TryDelete(backup);
            }

            throw new HookInstallException($"Cannot write {_paths.SettingsFile}: {ex.Message}", ex);
        }

        PruneBackups();
        _logger.LogInformation("Updated {File} (backup: {Backup})", _paths.SettingsFile, backup ?? "none");
        return new HookInstallResult(true, backup);
    }

    /// <summary>The file a symbolic link finally points to, or the path itself.</summary>
    private static string LinkTarget(string file)
    {
        try
        {
            return new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file;
        }
    }

    /// <summary>The newest <see cref="BackupsKept"/> backups stay; their names carry the time, so they sort by age.</summary>
    private void PruneBackups()
    {
        string[] backups;
        try
        {
            backups = Directory.GetFiles(_paths.ClaudeDirectory, Path.GetFileName(_paths.SettingsFile) + BackupSuffix + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var old in backups.OrderByDescending(f => f, StringComparer.Ordinal).Skip(BackupsKept))
        {
            TryDelete(old);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal); // a backup copies the read-only flag of settings.json
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
