using Xunit.Sdk;

namespace XUnitEventHubsTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class EventHubsTests(Containers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly EventHubsFeature hubs = containers.Fixture.Feature<EventHubsFeature>();

    [Fact]
    public async Task typed_message_round_trips_through_sender_and_recorder()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-1", 12.5m, ["a"]);
        await hubs.Sender.PublishAsync(Containers.Orders, order, order.OrderId, correlationId: "corr-1");

        var received = await hubs.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(order.Amount, received.Amount);
        var raw = hubs.Recorder.Messages(Containers.Orders, typeof(OrderPlaced).FullName).Single(m => m.PartitionKey == order.OrderId);
        Assert.Equal("corr-1", raw.CorrelationId);
        Assert.DoesNotContain(EventHubsSender.TypeProperty, raw.Headers.Keys);
    }

    [Fact]
    public async Task unmatched_messages_stay_available_to_later_waits()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await hubs.Sender.PublishAsync(Containers.Orders, new OrderPlaced(first, "c", 1, []));
        await hubs.Sender.PublishAsync(Containers.Orders, new OrderPlaced(second, "c", 2, []));

        await hubs.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == second, Wait);
        await hubs.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == first, Wait);
    }

    [Theory]
    [InlineData(Containers.Orders)]
    [InlineData(Containers.TwoPartitions)]
    public async Task recorder_starts_at_the_tail(string hub)
    {
        var before = Guid.NewGuid().ToString();
        var after = Guid.NewGuid().ToString();
        await hubs.Sender.PublishAsync(hub, new OrderPlaced(before, "c", 1, []));
        // The fixture recorder seeing it proves it was enqueued before the late recorder pins its start.
        await hubs.Recorder.WaitForAsync<OrderPlaced>(hub, o => o.OrderId == before, Wait);

        await using var late = await EventHubsRecorder.StartAsync(containers.EventHubsConnection, [hub], ct: TestContext.Current.CancellationToken);
        await hubs.Sender.PublishAsync(hub, new OrderPlaced(after, "c", 1, []));

        await late.WaitForAsync<OrderPlaced>(hub, o => o.OrderId == after, Wait);
        Assert.DoesNotContain(late.Messages(hub), m => m.Text.Contains(before, StringComparison.Ordinal));
    }

    [Fact]
    public async Task recorder_reads_every_partition()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid().ToString()).ToList();
        foreach (var id in ids)
            await hubs.Sender.PublishAsync(Containers.TwoPartitions, new OrderPlaced(id, "c", 1, []), partitionKey: id);

        await Eventually.Assert(() =>
        {
            var seen = hubs.Recorder.Messages(Containers.TwoPartitions).Where(m => ids.Contains(m.PartitionKey)).ToList();
            Assert.Equal(ids.Order(), seen.Select(m => m.PartitionKey).Order());
            Assert.Equal(2, seen.Select(m => m.PartitionId).Distinct().Count());
            return Task.CompletedTask;
        }, Wait);
    }

    [Fact]
    public async Task scenario_publish_then_published_with_key_and_capture()
    {
        string? captured = null;
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(EventHubs.Publish(Containers.TwoPartitions,
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "amount": 12.5, "items": ["x", "y"] }""",
                type: typeof(OrderPlaced).FullName, key: "{{customerId}}"))
            .Then(EventHubs.Published<OrderPlaced>(Containers.TwoPartitions, """{ "orderId": "{{orderId}}", "items": ["x", "y"] }""")
                .WithKey("{{customerId}}")
                .Satisfies<OrderPlaced>(o => Assert.Equal(12.5m, o.Amount))
                .Capture("amount", "amount"))
            .Then(ctx =>
            {
                captured = ctx.Variables.GetString("amount");
                return Task.CompletedTask;
            })
            .RunAsync();

        Assert.Equal("12.5", captured);
    }

    [Fact]
    public async Task typed_json_source_publishes_with_its_clr_type()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-typed", 3, []);
        await containers.Fixture.Scenario()
            .When(EventHubs.Publish(Containers.Orders, Json.From(order)))
            .Then(EventHubs.Published<OrderPlaced>(Containers.Orders, Json.From(new { order.OrderId })))
            .RunAsync();
    }

    [Fact]
    public async Task inline_json_without_a_type_is_refused()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .When(EventHubs.Publish(Containers.Orders, """{ "a": 1 }"""))
            .Then(_ => Task.CompletedTask)
            .RunAsync());
        Assert.Contains("needs a message type", ex.Message);
    }

    [Fact]
    public async Task published_timeout_shows_the_nearest_candidate_diff()
    {
        var orderId = Guid.NewGuid().ToString();
        await hubs.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c-near", 5, []));
        await hubs.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == orderId, Wait);

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(EventHubs.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 6 }""").Within(TimeSpan.FromSeconds(1)))
            .RunAsync());

        Assert.Contains("Nearest candidates", ex.Message);
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task publish_sends_headers_as_application_properties()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(EventHubs.Publish(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName,
                headers: "CustomDelay=10&CustomJobName=job-{{orderId}}"))
            .Then(ctx => Eventually.Assert(() =>
            {
                var message = Assert.Single(hubs.Recorder.Messages(Containers.Orders),
                    m => m.Text.Contains(ctx.Variables["orderId"], StringComparison.Ordinal));
                Assert.Equal("10", message.Headers["CustomDelay"]);
                Assert.Equal($"job-{ctx.Variables["orderId"]}", message.Headers["CustomJobName"]);
                Assert.Equal(typeof(OrderPlaced).FullName, message.Type);
                return Task.CompletedTask;
            }, Wait))
            .RunAsync();
    }

    [Fact]
    public async Task published_filters_by_native_correlation_id()
    {
        var orderId = Guid.NewGuid().ToString();
        await hubs.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 1, []), correlationId: "other");
        await hubs.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 2, []), correlationId: $"{orderId}-close");
        await containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(EventHubs.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""")
                .WithCorrelation("{{orderId}}-close")
                .Satisfies<OrderPlaced>(o => Assert.Equal(2, o.Amount)))
            .RunAsync();

        // Rides the AMQP correlation-id field, not an application property.
        var hit = hubs.Recorder.Messages(Containers.Orders).First(m => m.CorrelationId == $"{orderId}-close");
        Assert.DoesNotContain(hit.Headers.Keys, k => k.Contains("orrelation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task not_published_passes_for_other_bodies_and_fails_for_a_match()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(EventHubs.Publish(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 1 }""", type: typeof(OrderPlaced).FullName))
            .Then(EventHubs.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(EventHubs.NotPublished<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 2 }"""))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(EventHubs.Publish(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName))
            .Then(EventHubs.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(EventHubs.NotPublished(Containers.Orders, null, """{ "orderId": "{{orderId}}" }"""))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task published_works_as_a_given_barrier()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-barrier", 1, []);
        await hubs.Sender.PublishAsync(Containers.Orders, order);
        await containers.Fixture.Scenario()
            .With("orderId", order.OrderId)
            .Given(EventHubs.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .When(_ => Task.CompletedTask)
            .Then(_ => Task.CompletedTask)
            .RunAsync();
    }

    [Fact]
    public async Task unconfigured_hubs_are_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hubs.Sender.PublishJsonAsync("nope", "{}", "T", ct: TestContext.Current.CancellationToken));
        Assert.Contains("not configured", ex.Message);
        Assert.Throws<InvalidOperationException>(() => hubs.Recorder.Messages("nope"));
    }
}
