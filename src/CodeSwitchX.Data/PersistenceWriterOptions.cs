namespace CodeSwitchX.Data;

public sealed class PersistenceWriterOptions
{
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int MaxBatch { get; set; } = 500;

    /// <summary>Opt-in: store raw hook payload JSON (contains prompts and file paths).</summary>
    public bool StorePayloads { get; set; }
}
