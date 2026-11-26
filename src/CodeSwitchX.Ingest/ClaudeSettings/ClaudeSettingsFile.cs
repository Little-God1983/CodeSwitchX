using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.ClaudeSettings;

/// <param name="Changed">Whether the file was written.</param>
/// <param name="BackupFile">The copy of the file as it was; null when there was none before, or nothing was written.</param>
internal sealed record ClaudeSettingsSaved(bool Changed, string? BackupFile);

/// <summary>
/// Loads and saves Claude Code's user-level settings.json with care: a backup of the file as it was, an atomic replace, a
/// linked file written where it points, and nothing written when nothing changed (#450). Not in DI: each user creates its
/// own, so its log lines carry that user's category.
/// </summary>
internal sealed class ClaudeSettingsFile(ClaudeCodePaths paths, ILogger logger)
{
    /// <summary>How many <c>settings.json.csx-backup-*</c> files stay next to settings.json; older ones go when a save makes a new one.</summary>
    internal const int BackupsKept = 5;
    private const string TempSuffix = ".csx-tmp";
    private const string BackupSuffix = ".csx-backup-";

    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The file as text of the form it is written in: what <see cref="Save"/> compares against.</summary>
    internal static string TextOf(JsonObject settings) => settings.ToJsonString(WriteOptions);

    /// <summary>
    /// The file, or an empty object when there is none. <paramref name="read"/> touches the parts the caller is going to read
    /// or edit, so a repeated key or a bad escape there is refused here rather than halfway through a change.
    /// </summary>
    internal JsonObject Load(Action<JsonObject>? read = null)
    {
        if (!File.Exists(paths.SettingsFile))
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ClaudeSettingsException($"Cannot read {paths.SettingsFile}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ClaudeSettingsException($"{paths.SettingsFile} is empty; refusing to overwrite it. Fix or delete the file and retry.");
        }

        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new ClaudeSettingsException($"{paths.SettingsFile} does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ClaudeSettingsException($"{paths.SettingsFile} is not valid JSON: {ex.Message}", ex);
        }

        try
        {
            read?.Invoke(settings);
            _ = settings.ToJsonString(WriteOptions);
        }
        catch (ArgumentException ex)
        {
            // Claude Code keeps the last of a repeated key. Only the parts the caller reads must not repeat one; the rest
            // is never read and is written back as it was.
            throw new ClaudeSettingsException($"{paths.SettingsFile} repeats a key where CodeSwitchX edits it: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // A lone surrogate escape (half an emoji) anywhere in the file, in a key or a value: the key throws when its object
            // is first read, the value when the file is written back; either failed the status and with it the start.
            throw new ClaudeSettingsException($"{paths.SettingsFile} holds an escape sequence that cannot be read: {ex.Message}", ex);
        }

        return settings;
    }

    /// <summary>
    /// Writes <paramref name="settings"/> unless its text equals <paramref name="before"/> and the file exists: a backup of the
    /// file as it was, then an atomic replace.
    /// </summary>
    internal ClaudeSettingsSaved Save(JsonObject settings, string before)
    {
        var after = settings.ToJsonString(WriteOptions);
        if (after == before && File.Exists(paths.SettingsFile))
        {
            return new ClaudeSettingsSaved(false, null);
        }

        // A linked settings.json (dotfiles setups) is written where it points, so the link stays and its repository sees the change.
        var target = LinkTarget(paths.SettingsFile);
        string? backup = null;
        try
        {
            Directory.CreateDirectory(paths.ClaudeDirectory);
            if (File.Exists(paths.SettingsFile))
            {
                backup = $"{paths.SettingsFile}{BackupSuffix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
                File.Copy(paths.SettingsFile, backup, overwrite: true);
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

            throw new ClaudeSettingsException($"Cannot write {paths.SettingsFile}: {ex.Message}", ex);
        }

        PruneBackups();
        logger.LogInformation("Updated {File} (backup: {Backup})", paths.SettingsFile, backup ?? "none");
        return new ClaudeSettingsSaved(true, backup);
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
            backups = Directory.GetFiles(paths.ClaudeDirectory, Path.GetFileName(paths.SettingsFile) + BackupSuffix + "*");
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
