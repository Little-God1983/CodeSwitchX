using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class TokenFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(999_950, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(3_400_000_000, "3.4B")]
    [InlineData(1_500_000_000_000, "1500B")]
    [InlineData(long.MaxValue, "9223372037B")]
    public void Compact_uses_K_M_B_suffixes(long tokens, string expected)
    {
        TokenFormat.Compact(tokens).ShouldBe(expected);
    }
}
