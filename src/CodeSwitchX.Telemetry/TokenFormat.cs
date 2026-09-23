using System.Globalization;

namespace CodeSwitchX.Telemetry;

public static class TokenFormat
{
    public static string Compact(long tokens)
    {
        var abs = Math.Abs(tokens);
        return abs switch
        {
            < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => Scale(tokens, 1_000, "K"),
            < 1_000_000_000 => Scale(tokens, 1_000_000, "M"),
            _ => Scale(tokens, 1_000_000_000, "B"),
        };
    }

    private static string Scale(long tokens, long divisor, string suffix)
    {
        var value = Math.Round((double)tokens / divisor, 1);
        if (value >= 1000)
        {
            return Compact((long)(value * divisor));
        }

        var text = value >= 100 || value == Math.Floor(value)
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
        return text + suffix;
    }
}
