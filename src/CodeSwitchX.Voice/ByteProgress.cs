namespace CodeSwitchX.Voice;

/// <summary>How much of a download has arrived.</summary>
public readonly record struct ByteProgress(long Done, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);

    /// <summary>"142 of 330 MB".</summary>
    public override string ToString() => $"{Done / 1_000_000:N0} of {Total / 1_000_000:N0} MB";
}
