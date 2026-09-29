using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class TokenUsageTests
{
    [Fact]
    public void A_one_hour_cache_write_is_its_own_count_and_still_a_context_and_total_token()
    {
        var usage = new TokenUsage(Input: 100, Output: 20, CacheWrite: 300, CacheRead: 3000, CacheWrite1h: 200);

        usage.ContextTokens.ShouldBe(3600, "the write is in the context whichever cache it went to");
        usage.Total.ShouldBe(3620);
    }

    [Fact]
    public void The_sum_of_two_usages_adds_the_one_hour_cache_writes()
    {
        var sum = new TokenUsage(1, 2, 3, 4, CacheWrite1h: 5) + new TokenUsage(10, 20, 30, 40, CacheWrite1h: 50);

        sum.ShouldBe(new TokenUsage(11, 22, 33, 44, CacheWrite1h: 55));
    }
}
