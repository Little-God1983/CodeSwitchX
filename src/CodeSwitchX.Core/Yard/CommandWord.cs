using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.Yard;

/// <summary>
/// Whether an Open mic turn is meant for Raven (#217): it starts with "Raven", after a lead word or not ("hey Raven", "okay
/// Raven"), as Whisper spells it (Raven, Ravin, Rayven, the German Raben). Only the start counts, so speech around the room
/// that names a raven ("I saw a raven today") is not taken, and neither is a longer word ("Ravens are clever").
/// </summary>
public static partial class CommandWord
{
    [GeneratedRegex("""^[\s"'„“”]*(?:(?:hey|hi|hallo|okay|ok|so|well|please|bitte)[\s,.!:;]+)*(?:raven|ravin|rayven|raeven|raiven|raben)(?!\p{L})[\s,.!?:;\-–—]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Start();

    /// <summary>True when <paramref name="said"/> starts with the word; <paramref name="rest"/> is what follows it, begun with a
    /// capital, or empty when the word was all of it.</summary>
    public static bool TryStrip(string? said, out string rest)
    {
        rest = "";
        if (said is null || Start().Match(said) is not { Success: true } match)
        {
            return false;
        }

        var after = said[match.Length..].Trim();
        rest = after.Length == 0 ? "" : char.ToUpperInvariant(after[0]) + after[1..];
        return true;
    }
}
