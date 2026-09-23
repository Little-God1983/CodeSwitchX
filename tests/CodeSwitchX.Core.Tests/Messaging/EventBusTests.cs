using CodeSwitchX.Core.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Core.Tests.Messaging;

public class EventBusTests
{
    private sealed record Ping(int N);
    private sealed record Pong(int N);

    [Fact]
    public void Subscribers_receive_only_their_message_type()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var pings = new List<int>();
        var pongs = new List<int>();
        bus.Subscribe<Ping>(p => pings.Add(p.N));
        bus.Subscribe<Pong>(p => pongs.Add(p.N));

        bus.Publish(new Ping(1));
        bus.Publish(new Pong(2));

        pings.ShouldBe([1]);
        pongs.ShouldBe([2]);
    }

    [Fact]
    public void Disposing_the_subscription_stops_delivery()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = 0;
        var subscription = bus.Subscribe<Ping>(_ => received++);

        bus.Publish(new Ping(1));
        subscription.Dispose();
        bus.Publish(new Ping(2));

        received.ShouldBe(1);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_others()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = 0;
        bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<Ping>(_ => received++);

        Should.NotThrow(() => bus.Publish(new Ping(1)));

        received.ShouldBe(1);
    }

    [Fact]
    public void Publishing_from_inside_a_handler_is_allowed()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var pongs = new List<int>();
        bus.Subscribe<Ping>(p => bus.Publish(new Pong(p.N * 10)));
        bus.Subscribe<Pong>(p => pongs.Add(p.N));

        bus.Publish(new Ping(4));

        pongs.ShouldBe([40]);
    }
}
