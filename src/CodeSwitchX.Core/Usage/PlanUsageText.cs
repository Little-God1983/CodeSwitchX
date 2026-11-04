using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.Usage;

/// <summary>
/// What <c>claude -p "/usage"</c> prints, made into figures (#370). The text is meant for people, so anything that does not
/// read exactly as expected gives no figure instead of a wrong one:
/// <code>
/// Current session: 54% used · resets Nov 3, 10:39pm (Europe/Berlin)
/// Current week (all models): 38% used · resets Nov 9, 6am (Europe/Berlin)
/// </code>
/// The minutes are left out on the hour. The text has no year, and its zone is an IANA name in brackets. Its time can be a
/// minute before the real reset, so it says about when, not exactly when.
/// </summary>
public static partial class PlanUsageText
{
    private static readonly string[] ResetFormats = ["yyyy MMM d, h:mmtt", "yyyy MMM d, htt"];

    public static PlanLimits Parse(string text, DateTimeOffset now)
    {
        PlanLimit? fiveHour = null;
        PlanLimit? week = null;
        foreach (var line in text.Split('\n'))
        {
            if (Limit().Match(line.Trim()) is not { Success: true } match)
            {
                continue;
            }

            var limit = LimitOf(match, now);
            if (match.Groups["name"].ValueSpan.SequenceEqual("session"))
            {
                fiveHour ??= limit;
            }
            else
            {
                week ??= limit;
            }
        }

        return new PlanLimits(fiveHour, week);
    }

    private static PlanLimit? LimitOf(Match match, DateTimeOffset now)
    {
        if (!int.TryParse(match.Groups["percent"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        if (!match.Groups["when"].Success)
        {
            return new PlanLimit(percent, null);
        }

        return ResetOf(match.Groups["when"].Value, match.Groups["zone"].Value, now) is { } resetsAt ? new PlanLimit(percent, resetsAt) : null;
    }

    /// <summary>The text has no year: of the years it can be in, the one that puts it nearest to now, so a reset across New Year lands in the next year and one just passed stays in the old one.</summary>
    private static DateTimeOffset? ResetOf(string when, string zoneName, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException)
        {
            return null;
        }

        DateTimeOffset? nearest = null;
        for (var year = now.Year - 1; year <= now.Year + 1; year++)
        {
            if (DateTime.TryParseExact($"{year} {when}", ResetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                // By the offset, not a conversion: a time the clock skips that night would throw.
                var candidate = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));
                if (nearest is null || (candidate - now).Duration() < (nearest.Value - now).Duration())
                {
                    nearest = candidate;
                }
            }
        }

        return nearest?.ToUniversalTime();
    }

    [GeneratedRegex(@"^Current (?<name>session|week \(all models\)):\s*(?<percent>\d{1,4})%\s*used(?:\s+\S\s+resets\s+(?<when>.+?)\s*\((?<zone>[^()]+)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Limit();
}
