namespace CodeSwitchX.Core;

/// <summary>
/// Replaces a file by writing next to it and moving over it, so a reader sees the old content or the new and never a torn
/// file. Windows refuses the move while another process holds the file without FileShare.Delete (the relay reading
/// endpoint.json, an editor on settings.json), so it is retried for about half a second; a read-only target is refused at
/// once. A failure never leaves the temporary file behind.
/// </summary>
public static class AtomicFile
{
    public const int Attempts = 10;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static void Replace(string file, string content, string tempSuffix)
    {
        var tmp = file + tempSuffix;
        try
        {
            // Tried as the move is: a temporary file a failed write left may still be held by a scanner (#392).
            Retry(() => File.WriteAllText(tmp, content), tmp);
            MoveOver(tmp, file);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Moves a file written next to <paramref name="file"/> over it, tried for about half a second while another process holds
    /// either one, as a scanner does a file just written (#392); a read-only target is refused at once. The caller deletes
    /// the source when this throws.
    /// </summary>
    public static void MoveOver(string source, string file) => Retry(() => File.Move(source, file, overwrite: true), file);

    private static void Retry(Action action, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts && !IsReadOnly(target))
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static bool IsReadOnly(string file)
    {
        try
        {
            return File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
