using System.Globalization;

namespace CodeSwitchX.Telemetry;

public static class TokenFormat
{
    private static readonly string[] Units = ["", "K", "M", "B"];

    /// <summary>
    /// "999", "12.3K", "1M", "1500B": one decimal below 100, whole numbers from there, halves rounded away from zero
    /// (1,050 is "1.1K"). A value that rounds up to 1000 moves to the next unit (999,500 is "1M", not "1000K"); B is the
    /// last unit. In decimal: 1.05 is 1.0499... as a double, and <see cref="long.MinValue"/> has no positive counterpart.
    /// </summary>
    public static string Compact(long tokens)
    {
        var value = (decimal)tokens;
        var unit = 0;
        while (unit < Units.Length - 1 && Math.Abs(Rounded(value)) >= 1000)
        {
            value /= 1000;
            unit++;
        }

        return Rounded(value).ToString("0.#", CultureInfo.InvariantCulture) + Units[unit];
    }

    private static decimal Rounded(decimal value) => Math.Round(value, Math.Abs(value) >= 100 ? 0 : 1, MidpointRounding.AwayFromZero);
}
