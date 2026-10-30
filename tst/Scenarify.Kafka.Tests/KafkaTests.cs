using Xunit.Sdk;

namespace XUnitKafkaTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class KafkaTests(Containers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly KafkaFeature kafka = containers.Fixture.Feature<KafkaFeature>();

    [Fact]
    public void feature_resolves_bootstrap_servers_from_config()
    {
        Assert.Equal(containers.BootstrapServers, kafka.BootstrapServers);
    }

    [Fact]
    public async Task typed_message_round_trips_through_sender_and_recorder()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-1", 12.5m, ["a"]);
        await kafka.Sender.PublishAsync(Containers.Orders, order, order.OrderId, correlationId: "corr-1");

        var received = await kafka.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(order.Amount, received.Amount);
        var raw = kafka.Recorder.Messages(Containers.Orders, typeof(OrderPlaced).FullName).Single(m => m.PartitionKey == order.OrderId);
        Assert.Equal("corr-1", raw.CorrelationId);
        Assert.DoesNotContain("type", raw.Headers.Keys);
        Assert.DoesNotContain("correlationId", raw.Headers.Keys);
    }

    [Fact]
    public async Task unmatched_messages_stay_available_to_later_waits()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(first, "c", 1, []));
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(second, "c", 2, []));

        await kafka.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == second, Wait);
        await kafka.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == first, Wait);
    }

    [Fact]
    public async Task recorder_starts_at_the_tail()
    {
        // Many records across partitions before start: any replay (earliest, or a lazy "latest") would show one.
        var before = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid().ToString()).ToList();
        foreach (var id in before)
            await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(id, "c-before", 1, []));

        await using var late = await KafkaRecorder.StartAsync(containers.BootstrapServers, [Containers.Orders]);
        // Produced immediately after start returns: a recorder positioned lazily would race this and miss it.
        var after = Guid.NewGuid().ToString();
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(after, "c-after", 1, []));

        await late.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == after, Wait);
        Assert.DoesNotContain(late.Messages(Containers.Orders), m => m.As<OrderPlaced>()?.CustomerId == "c-before");
    }

    [Fact]
    public async Task recorder_creates_a_missing_topic_and_sees_the_first_record()
    {
        var topic = $"fresh-{Guid.NewGuid():N}";
        await using var recorder = await KafkaRecorder.StartAsync(containers.BootstrapServers, [topic]);
        await using var sender = KafkaSender.Start(containers.BootstrapServers, [topic]);

        await sender.PublishJsonAsync(topic, """{ "n": 1 }""", "Fresh");

        var hit = await recorder.WaitForAsync(topic, "Fresh", """{ "n": 1 }""", Wait);
        Assert.Equal(topic, hit.Topic);
    }

    [Fact]
    public async Task producing_to_an_unconfigured_topic_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kafka.Sender.PublishJsonAsync("not-configured", "{}", "X"));
        Assert.Contains("not configured for publishing", ex.Message);
    }

    [Fact]
    public async Task scenario_produce_then_produced_with_key_and_capture()
    {
        string? captured = null;
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kafka.Produce(Containers.Payments,
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "amount": 12.5, "items": ["x", "y"] }""",
                type: typeof(OrderPlaced).FullName, key: "{{customerId}}"))
            .Then(Kafka.Produced<OrderPlaced>(Containers.Payments, """{ "orderId": "{{orderId}}", "items": ["x", "y"] }""")
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
    public async Task typed_json_source_produces_with_its_clr_type()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-typed", 3, []);
        await containers.Fixture.Scenario()
            .When(Kafka.Produce(Containers.Orders, Json.From(order)))
            .Then(Kafka.Produced<OrderPlaced>(Containers.Orders, Json.From(new { order.OrderId })))
            .RunAsync();
    }

    [Fact]
    public async Task inline_json_without_a_type_is_refused()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .When(Kafka.Produce(Containers.Orders, """{ "a": 1 }"""))
            .Then(_ => Task.CompletedTask)
            .RunAsync());
        Assert.Contains("needs a message type", ex.Message);
    }

    [Fact]
    public async Task produced_timeout_shows_the_nearest_candidate_diff()
    {
        var orderId = Guid.NewGuid().ToString();
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c-near", 5, []));

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(Kafka.Produced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 6 }""").Within(TimeSpan.FromSeconds(1)))
            .RunAsync());

        Assert.Contains("Nearest candidates", ex.Message);
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task produce_sends_record_headers()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kafka.Produce(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName,
                correlationId: "corr-{{orderId}}", headers: "CustomDelay=10&CustomJobName=job-{{orderId}}"))
            .Then(ctx => Eventually.Assert(() =>
            {
                var message = Assert.Single(kafka.Recorder.Messages(Containers.Orders),
                    m => m.Text.Contains(ctx.Variables["orderId"], StringComparison.Ordinal));
                Assert.Equal(typeof(OrderPlaced).FullName, message.Type);
                Assert.Equal($"corr-{ctx.Variables["orderId"]}", message.CorrelationId);
                Assert.Equal("10", message.Headers["CustomDelay"]);
                Assert.Equal($"job-{ctx.Variables["orderId"]}", message.Headers["CustomJobName"]);
                return Task.CompletedTask;
            }))
            .RunAsync();
    }

    [Fact]
    public async Task produced_filters_by_correlation_id()
    {
        var orderId = Guid.NewGuid().ToString();
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 1, []), correlationId: "other");
        await kafka.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 2, []), correlationId: $"{orderId}-close");
        await containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(Kafka.Produced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""")
                .WithCorrelation("{{orderId}}-close")
                .Satisfies<OrderPlaced>(o => Assert.Equal(2, o.Amount)))
            .RunAsync();
    }

    [Fact]
    public async Task not_produced_passes_for_other_bodies_and_fails_for_a_match()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kafka.Produce(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 1 }""", type: typeof(OrderPlaced).FullName))
            .Then(Kafka.Produced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Kafka.NotProduced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 2 }"""))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kafka.Produce(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName))
            .Then(Kafka.Produced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Kafka.NotProduced(Containers.Orders, null, """{ "orderId": "{{orderId}}" }"""))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task produced_works_as_a_given_barrier()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-barrier", 1, []);
        await kafka.Sender.PublishAsync(Containers.Orders, order);
        await containers.Fixture.Scenario()
            .With("orderId", order.OrderId)
            .Given(Kafka.Produced<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .When(Kafka.Produce(Containers.Payments, """{ "orderId": "{{orderId}}" }""", type: "PaymentTaken"))
            .Then(Kafka.Produced(Containers.Payments, "PaymentTaken", """{ "orderId": "{{orderId}}" }"""))
            .RunAsync();
    }
}
