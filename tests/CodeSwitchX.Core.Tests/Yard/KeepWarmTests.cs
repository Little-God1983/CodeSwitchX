using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Yard;

public class KeepWarmTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly Dictionary<string, SessionSnapshot> _chats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Sender _sender = new();
    private readonly Settings _settings = new();

    private KeepWarm New()
    {
        var keep = new KeepWarm(id => _chats.GetValueOrDefault(id), id => _names.GetValueOrDefault(id), _sender, _settings, _time,
            NullLogger<KeepWarm>.Instance);
        return keep;
    }

    /// <summary>A chat open in a tab, idle, whose last request was <paramref name="ago"/>.</summary>
    private void Chat(string id, TimeSpan ago, SessionState state = SessionState.Idle, bool inTab = true)
    {
        var now = _time.GetUtcNow();
        _chats[id] = new SessionSnapshot { SessionId = id, State = state, StartedAt = Start, LastEventAt = now, StateSince = now, LastRequestAt = now - ago };
        if (inTab)
        {
            _names[id] = "ws-" + id;
        }
        else
        {
            _names.Remove(id);
        }
    }

    private static TimeSpan Minutes(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void A_chat_is_kept_warm_until_the_time_chosen()
    {
        Chat("a", Minutes(10));
        var keep = New();

        keep.Start("a", TimeSpan.FromHours(4)).ShouldBeTrue();

        keep.Until("a").ShouldBe(Start.AddHours(4));
    }

    [Fact]
    public void A_chat_nobody_marked_is_not_kept_warm()
    {
        New().Until("a").ShouldBeNull();
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_cache_has_more_than_five_minutes_left()
    {
        Chat("a", Minutes(54));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBeEmpty();
        keep.Until("a").ShouldNotBeNull();
    }

    [Fact]
    public async Task The_ping_goes_to_the_chats_tab_once_its_last_request_is_55_minutes_old()
    {
        Chat("a", Minutes(55));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBe(["ws-a"]);
    }

    /// <summary>For the on-screen check: nobody waits 55 minutes to see a ping arrive.</summary>
    [Fact]
    public async Task The_age_a_chat_is_pinged_at_can_be_set()
    {
        Chat("a", Minutes(1));
        var keep = new KeepWarm(id => _chats.GetValueOrDefault(id), id => _names.GetValueOrDefault(id), _sender, _settings, _time,
            NullLogger<KeepWarm>.Instance) { PingAfter = Minutes(1) };
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBe(["ws-a"]);
    }

    [Fact]
    public async Task The_pings_of_several_chats_go_out_together()
    {
        Chat("a", Minutes(55));
        Chat("b", Minutes(55));
        Chat("c", Minutes(55));
        _sender.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));
        keep.Start("b", TimeSpan.FromHours(2));
        keep.Start("c", TimeSpan.FromHours(2));

        var check = keep.CheckAsync(CancellationToken.None);
        for (var i = 0; i < 100 && _sender.Sent.Count < 3; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        _sender.Sent.ShouldBe(["ws-a", "ws-b", "ws-c"], "a slow send must not hold the next chat's ping past its hour");
        check.IsCompleted.ShouldBeFalse();
        _sender.Hold.SetResult();
        await check;
    }

    [Fact]
    public async Task A_ping_is_not_sent_again_while_its_turn_may_still_be_on_its_way()
    {
        Chat("a", Minutes(55));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));
        await keep.CheckAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(20));
        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_ping_that_brought_no_request_is_sent_again_after_two_minutes()
    {
        Chat("a", Minutes(55));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));
        await keep.CheckAsync(CancellationToken.None);

        _time.Advance(KeepWarm.PingGap);
        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task After_a_ping_that_worked_the_next_one_waits_for_the_new_hour()
    {
        Chat("a", Minutes(55));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(4));
        await keep.CheckAsync(CancellationToken.None);

        Chat("a", TimeSpan.Zero); // the ping's turn made a request
        _time.Advance(Minutes(54));
        await keep.CheckAsync(CancellationToken.None);
        _sender.Sent.Count.ShouldBe(1);

        _time.Advance(Minutes(1));
        Chat("a", Minutes(55));
        await keep.CheckAsync(CancellationToken.None);
        _sender.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Keep_warm_ends_when_its_time_is_up()
    {
        Chat("a", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        _time.Advance(TimeSpan.FromHours(2));
        Chat("a", Minutes(57));
        await keep.CheckAsync(CancellationToken.None);

        keep.Until("a").ShouldBeNull();
        _sender.Sent.ShouldBeEmpty("its time is up: the chat goes cold as it would have");
    }

    [Fact]
    public async Task A_chat_that_went_cold_is_not_warmed_again()
    {
        Chat("a", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(8));

        Chat("a", Minutes(61)); // the app was closed over its hour
        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBeEmpty("a ping now would pay for the whole conversation again");
        keep.Until("a").ShouldBeNull();
    }

    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Waiting)]
    public async Task A_chat_in_a_turn_is_not_pinged(SessionState state)
    {
        Chat("a", Minutes(56), state);
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBeEmpty("a message would only queue behind its turn");
        keep.Until("a").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_chat_that_only_waits_for_a_background_job_is_pinged()
    {
        Chat("a", Minutes(56), SessionState.Working);
        _chats["a"] = _chats["a"] with { TurnOverAt = _time.GetUtcNow() - Minutes(56), OpenJobs = ["job1"] };
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        _sender.Sent.ShouldBe(["ws-a"]);
    }

    [Fact]
    public async Task A_chat_whose_tab_is_closed_cannot_be_pinged_and_ends_when_it_is_cold()
    {
        Chat("a", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(8));

        Chat("a", Minutes(56), inTab: false);
        await keep.CheckAsync(CancellationToken.None);
        _sender.Sent.ShouldBeEmpty();
        keep.Until("a").ShouldNotBeNull("opened again within its hour, it is kept warm on");

        Chat("a", Minutes(60), inTab: false);
        await keep.CheckAsync(CancellationToken.None);
        keep.Until("a").ShouldBeNull();
    }

    [Fact]
    public void Stopping_takes_the_mark_off()
    {
        Chat("a", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        keep.Stop("a");

        keep.Until("a").ShouldBeNull();
    }

    [Fact]
    public void A_cold_chat_cannot_be_kept_warm()
    {
        Chat("a", Minutes(61));
        var keep = New();

        keep.WhyNot("a").ShouldBe("Its cache is cold already: keeping it warm would pay for the whole conversation again.");
        keep.Start("a", TimeSpan.FromHours(2)).ShouldBeFalse();
        keep.Until("a").ShouldBeNull();
    }

    [Fact]
    public void A_chat_that_is_not_open_in_a_tab_cannot_be_kept_warm()
    {
        Chat("a", Minutes(10), inTab: false);

        New().WhyNot("a").ShouldBe("Only a chat open in a VS Code tab can be kept warm.");
    }

    [Fact]
    public void A_chat_that_made_no_request_yet_cannot_be_kept_warm()
    {
        _names["a"] = "ws-a";

        New().WhyNot("a").ShouldBe("It has no warm cache yet.");
    }

    [Fact]
    public void A_warm_chat_in_a_tab_can_be_kept_warm()
    {
        Chat("a", Minutes(59));

        New().WhyNot("a").ShouldBeNull();
    }

    [Fact]
    public async Task The_marks_come_back_after_a_restart()
    {
        Chat("a", Minutes(10));
        Chat("b", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));
        keep.Start("b", TimeSpan.FromHours(8));
        await keep.Saved;

        _time.Advance(TimeSpan.FromHours(3));
        var again = New();
        await again.LoadAsync(CancellationToken.None);

        again.Until("a").ShouldBeNull("its time ran out while the app was closed");
        again.Until("b").ShouldBe(Start.AddHours(8));
    }

    [Fact]
    public async Task A_stopped_mark_does_not_come_back()
    {
        Chat("a", Minutes(10));
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));
        keep.Stop("a");
        await keep.Saved;

        var again = New();
        await again.LoadAsync(CancellationToken.None);

        again.Until("a").ShouldBeNull();
    }

    [Fact]
    public async Task A_sender_that_fails_ends_nothing()
    {
        Chat("a", Minutes(55));
        _sender.Works = false;
        var keep = New();
        keep.Start("a", TimeSpan.FromHours(2));

        await keep.CheckAsync(CancellationToken.None);

        keep.Until("a").ShouldNotBeNull("it is tried again while the cache lasts");
    }

    private sealed class Sender : IKeepAliveSender
    {
        public List<string> Sent { get; } = [];
        public bool Works { get; set; } = true;

        /// <summary>When set, every send waits for it.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<bool> SendAsync(string sendName, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(sendName);
            }

            if (Hold is { } hold)
            {
                await hold.Task;
            }

            return Works;
        }
    }

    private sealed class Settings : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PricingRule>>([]);

        public Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default) => Task.CompletedTask;
    }
}
