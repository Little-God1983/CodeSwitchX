using System.Text.RegularExpressions;

namespace CodeSwitchX.Voice.Dictation;

/// <summary>Applies the user's "heard => meant" list to a transcript in a single left-to-right
/// pass, longest trimmed phrase first, so "comfy your eye" is rewritten before a shorter "eye"
/// rule can chew a piece out of it. Because the whole list is matched in one pass over the
/// original text, a shorter rule also can't chew into text a longer rule already produced (e.g. a
/// standalone "art" correction reaching into an "art" that only exists because "fine art" was just
/// rewritten to "Fine Art"). Word-boundary anchored so "art" never touches "part"; whitespace
/// inside a phrase matches any run of whitespace because Whisper's spacing is not the user's.</summary>
public static class TranscriptCorrector
{
    public static string Apply(string text, IReadOnlyList<Correction> corrections)
    {
        if (corrections.Count == 0 || text.Length == 0)
        {
            return text;
        }

        var ordered = corrections
            .Select(c => (Heard: c.Heard.Trim(), c.Meant))
            .Where(c => c.Heard.Length > 0)
            .OrderByDescending(c => c.Heard.Length)
            .ToList();
        if (ordered.Count == 0)
        {
            return text;
        }

        var alternatives = ordered.Select((c, i) =>
        {
            var pattern = Regex.Replace(Regex.Escape(c.Heard), @"(\\ )+", @"\s+");
            return $"(?<c{i}>{pattern})";
        });
        // \b only works next to word characters; a phrase starting or ending with "+" or ")"
        // has no boundary to anchor, so use lookarounds that accept start/end or whitespace.
        var combined = $@"(?<![\w])(?:{string.Join("|", alternatives)})(?![\w])";
        var regex = new Regex(combined, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return regex.Replace(text, m =>
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (m.Groups[$"c{i}"].Success)
                {
                    return ordered[i].Meant;
                }
            }

            return m.Value;
        });
    }
}
