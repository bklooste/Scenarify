using Xunit.Sdk;

namespace XUnitServiceBusTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class ServiceBusTests(Containers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly ServiceBusFeature bus = containers.Fixture.Feature<ServiceBusFeature>();

    [Fact]
    public async Task feature_resolves_the_connection_string_from_configuration()
    {
        Assert.Equal(containers.ConnectionString, bus.ConnectionString);
        Assert.Contains("UseDevelopmentEmulator=true", ServiceBusFeature.DefaultConnectionString);
        await using var empty = new ServiceBusFeature();
        Assert.Throws<InvalidOperationException>(() => empty.Sender);
        Assert.Throws<InvalidOperationException>(() => empty.Recorder);
    }

    [Fact]
    public async Task typed_message_round_trips_through_a_queue()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-1", 12.5m, ["a"]);
        await bus.Sender.PublishAsync(Containers.Orders, order, order.OrderId, correlationId: "corr-1");

        var received = await bus.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(order.Amount, received.Amount);
        var raw = bus.Recorder.Messages(Containers.Orders, typeof(OrderPlaced).FullName).Single(m => m.PartitionKey == order.OrderId);
        Assert.Equal("corr-1", raw.CorrelationId);
        Assert.NotNull(raw.MessageId);
    }

    [Fact]
    public async Task typed_message_round_trips_through_a_topic_subscription()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-topic", 7, ["t"]);
        await bus.Sender.PublishAsync(Containers.OrderEvents, order);

        var received = await bus.Recorder.WaitForAsync<OrderPlaced>(Containers.OrderEvents, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(["t"], received.Items);
        Assert.DoesNotContain(bus.Recorder.Messages(Containers.Orders), m => m.Text.Contains(order.OrderId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task unmatched_messages_stay_available_to_later_waits()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await bus.Sender.PublishAsync(Containers.Orders, new OrderPlaced(first, "c", 1, []));
        await bus.Sender.PublishAsync(Containers.Orders, new OrderPlaced(second, "c", 2, []));

        await bus.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == second, Wait);
        await bus.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == first, Wait);
    }

    [Fact]
    public async Task unknown_entities_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.Sender.PublishJsonAsync("nope", "{}", "T"));
        Assert.Throws<InvalidOperationException>(() => bus.Recorder.Messages("nope"));
    }

    [Fact]
    public async Task scenario_send_then_sent_with_key_and_capture()
    {
        string? captured = null;
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(ServiceBus.Send(Containers.OrderEvents,
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "amount": 12.5, "items": ["x", "y"] }""",
                type: typeof(OrderPlaced).FullName, key: "{{customerId}}"))
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.OrderEvents, """{ "orderId": "{{orderId}}", "items": ["x", "y"] }""")
                .WithKey("{{customerId}}")
                .Within(Wait)
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
    public async Task typed_json_source_sends_with_its_clr_type()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-typed", 3, []);
        await containers.Fixture.Scenario()
            .When(ServiceBus.Send(Containers.Orders, Json.From(order)))
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, Json.From(new { order.OrderId })).Within(Wait))
            .RunAsync();
    }

    [Fact]
    public async Task inline_json_without_a_type_is_refused()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .When(ServiceBus.Send(Containers.Orders, """{ "a": 1 }"""))
            .Then(_ => Task.CompletedTask)
            .RunAsync());
        Assert.Contains("needs a message type", ex.Message);
    }

    [Fact]
    public async Task sent_timeout_shows_the_nearest_candidate_diff()
    {
        var orderId = Guid.NewGuid().ToString();
        await bus.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c-near", 5, []));
        await bus.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == orderId, Wait);

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 6 }""").Within(TimeSpan.FromSeconds(1)))
            .RunAsync());

        Assert.Contains("Nearest candidates", ex.Message);
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task send_maps_headers_to_application_properties()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(ServiceBus.Send(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName,
                headers: "CustomDelay=10&CustomJobName=job-{{orderId}}"))
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""").Within(Wait))
            .Then(ctx =>
            {
                var message = Assert.Single(bus.Recorder.Messages(Containers.Orders),
                    m => m.Text.Contains(ctx.Variables["orderId"], StringComparison.Ordinal));
                Assert.Equal("10", message.Headers["CustomDelay"]);
                Assert.Equal($"job-{ctx.Variables["orderId"]}", message.Headers["CustomJobName"]);
                return Task.CompletedTask;
            })
            .RunAsync();
    }

    [Fact]
    public async Task sent_filters_by_native_correlation_id()
    {
        var orderId = Guid.NewGuid().ToString();
        await bus.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 1, []), correlationId: "other");
        await bus.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 2, []), correlationId: $"{orderId}-close");
        await containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""")
                .WithCorrelation("{{orderId}}-close")
                .Within(Wait)
                .Satisfies<OrderPlaced>(o => Assert.Equal(2, o.Amount)))
            .RunAsync();
    }

    [Fact]
    public async Task not_sent_passes_for_other_bodies_and_fails_for_a_match()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(ServiceBus.Send(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 1 }""", type: typeof(OrderPlaced).FullName))
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""").Within(Wait))
            .Then(ServiceBus.NotSent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 2 }"""))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(ServiceBus.Send(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName))
            .Then(ServiceBus.Sent<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""").Within(Wait))
            .Then(ServiceBus.NotSent(Containers.Orders, null, """{ "orderId": "{{orderId}}" }"""))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task sent_works_as_a_given_barrier()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-barrier", 1, []);
        await bus.Sender.PublishAsync(Containers.OrderEvents, order);
        await containers.Fixture.Scenario()
            .With("orderId", order.OrderId)
            .Given(ServiceBus.Sent<OrderPlaced>(Containers.OrderEvents, """{ "orderId": "{{orderId}}" }""").Within(Wait))
            .When(_ => Task.CompletedTask)
            .Then(ServiceBus.NotSent<OrderPlaced>(Containers.OrderEvents, """{ "orderId": "{{orderId}}", "amount": 99 }"""))
            .RunAsync();
    }

    [Fact]
    public async Task case_record_runs_send_then_get()
    {
        await containers.Fixture.RunAsync(new SendThenGet(
            "send-then-get", Containers.Orders, typeof(OrderPlaced).FullName!, """{ "orderId": "{{customerId}}" }""",
            "cases/{{customerId}}", """{ "ok": true }""",
            Mocks: ["""[{ "httpRequest": { "method": "GET", "path": "/cases/{{customerId}}" }, "httpResponse": { "statusCode": 200, "body": { "type": "JSON", "json": { "ok": true } } } }]"""]));
    }
}
