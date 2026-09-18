using System.Collections.Concurrent;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using RedisEvents.Extensions;

using Xunit.Sdk;

namespace XUnitRedisTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class StreamTests(Containers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly RedisFeature redis = containers.Fixture.Feature<RedisFeature>();

    [Fact]
    public async Task typed_message_round_trips_through_sender_and_recorder()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-1", 12.5m, ["a"]);
        await redis.Sender.PublishAsync(Containers.Orders, order, order.OrderId, correlationId: "corr-1");

        var received = await redis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(order.Amount, received.Amount);
        var raw = redis.Recorder.Messages(Containers.Orders, typeof(OrderPlaced).FullName).Single(m => m.PartitionKey == order.OrderId);
        Assert.Equal("corr-1", raw.CorrelationId);
    }

    [Fact]
    public async Task unmatched_messages_stay_available_to_later_waits()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(first, "c", 1, []));
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(second, "c", 2, []));

        await redis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == second, Wait);
        await redis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == first, Wait);
    }

    [Fact]
    public async Task recorder_starts_at_the_tail()
    {
        var before = Guid.NewGuid().ToString();
        var after = Guid.NewGuid().ToString();
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(before, "c", 1, []));

        await using var late = await StreamRecorder.StartAsync(containers.RedisConnection, [Containers.Orders]);
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(after, "c", 1, []));

        await late.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == after, Wait);
        Assert.DoesNotContain(late.Messages(Containers.Orders), m => m.Text.Contains(before, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("parity-default", null)]
    [InlineData("parity-two", 2)]
    public async Task sender_partitioning_agrees_with_a_real_AddStream_consumer(string topicPrefix, int? partitions)
    {
        var topic = $"{topicPrefix}-{Guid.NewGuid():N}";
        var seen = new ConcurrentDictionary<string, byte>();
        var builder = Host.CreateApplicationBuilder();
        var settings = new Dictionary<string, string?> { ["Streams:ConnectionString"] = containers.RedisConnection };
        if (partitions is not null)
            settings[$"Streams:Topics:{topic}:Partitions"] = partitions.ToString();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddStream(topic, (batch, _) =>
        {
            foreach (var msg in batch.Span)
                seen[Encoding.UTF8.GetString(msg.Body.Span)] = 0;
            return ValueTask.CompletedTask;
        });
        using var consumer = builder.Build();
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var partitionMap = partitions is null ? null : new Dictionary<string, int> { [topic] = partitions.Value };
        await using var sender = await StreamSender.StartAsync(containers.RedisConnection, [topic], partitionMap);
        var bodies = Enumerable.Range(0, 20).Select(i => $"{{\"n\":{i}}}").ToList();
        foreach (var body in bodies)
            await sender.PublishJsonAsync(topic, body, "Parity", partitionKey: Guid.NewGuid().ToString());

        await Eventually.Assert(() =>
        {
            Assert.Equal(bodies.Order(), seen.Keys.Order());
            return Task.CompletedTask;
        }, Wait);
        await consumer.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task scenario_publish_then_published_with_key_and_capture()
    {
        string? captured = null;
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Streams.Publish(Containers.TwoPartitions,
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "amount": 12.5, "items": ["x", "y"] }""",
                type: typeof(OrderPlaced).FullName, key: "{{customerId}}"))
            .Then(Streams.Published<OrderPlaced>(Containers.TwoPartitions, """{ "orderId": "{{orderId}}", "items": ["x", "y"] }""")
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
            .When(Streams.Publish(Containers.Orders, Json.From(order)))
            .Then(Streams.Published<OrderPlaced>(Containers.Orders, Json.From(new { order.OrderId })))
            .RunAsync();
    }

    [Fact]
    public async Task inline_json_without_a_type_is_refused()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .When(Streams.Publish(Containers.Orders, """{ "a": 1 }"""))
            .Then(_ => Task.CompletedTask)
            .RunAsync());
        Assert.Contains("needs a message type", ex.Message);
    }

    [Fact]
    public async Task published_timeout_shows_the_nearest_candidate_diff()
    {
        var orderId = Guid.NewGuid().ToString();
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c-near", 5, []));

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(Streams.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 6 }""").Within(TimeSpan.FromSeconds(1)))
            .RunAsync());

        Assert.Contains("Nearest candidates", ex.Message);
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task publish_sends_message_headers()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Streams.Publish(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName,
                headers: "CustomDelay=10&CustomJobName=job-{{orderId}}"))
            .Then(ctx => Eventually.Assert(() =>
            {
                var message = Assert.Single(redis.Recorder.Messages(Containers.Orders),
                    m => m.Text.Contains(ctx.Variables["orderId"], StringComparison.Ordinal));
                Assert.Equal("10", message.Headers["CustomDelay"]);
                Assert.Equal($"job-{ctx.Variables["orderId"]}", message.Headers["CustomJobName"]);
                return Task.CompletedTask;
            }))
            .RunAsync();
    }

    [Fact]
    public async Task published_filters_by_correlation_id()
    {
        var orderId = Guid.NewGuid().ToString();
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 1, []), correlationId: "other");
        await redis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c", 2, []), correlationId: $"{orderId}-close");
        await containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(Streams.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }""")
                .WithCorrelation("{{orderId}}-close")
                .Satisfies<OrderPlaced>(o => Assert.Equal(2, o.Amount)))
            .RunAsync();
    }

    [Fact]
    public async Task not_published_passes_for_other_bodies_and_fails_for_a_match()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Streams.Publish(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 1 }""", type: typeof(OrderPlaced).FullName))
            .Then(Streams.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Streams.NotPublished<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 2 }"""))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Streams.Publish(Containers.Orders, """{ "orderId": "{{orderId}}" }""", type: typeof(OrderPlaced).FullName))
            .Then(Streams.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Streams.NotPublished(Containers.Orders, null, """{ "orderId": "{{orderId}}" }"""))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task published_works_as_a_given_barrier()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-barrier", 1, []);
        await redis.Sender.PublishAsync(Containers.Orders, order);
        await containers.Fixture.Scenario()
            .With("orderId", order.OrderId)
            .Given(Streams.Published<OrderPlaced>(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Given(Redis.SortedSetAdd("zs:{{customerId}}", "{{orderId}}", 5))
            .When(_ => Task.CompletedTask)
            .Then(async ctx =>
            {
                var db = await ctx.Fixture.Feature<RedisFeature>().DatabaseAsync();
                Assert.Equal(5, await db.SortedSetScoreAsync(ctx.Variables.ExpandText("zs:{{customerId}}"), order.OrderId));
            })
            .RunAsync();
    }

    [Fact]
    public async Task seeded_keys_get_the_default_ttl_unless_overridden_and_steps_work_as_checks()
    {
        await containers.Fixture.Scenario()
            .Given(Redis.Set("ttl:{{customerId}}", """{ "a": 1 }"""))
            .Given(Redis.SortedSetAdd("ttl-z:{{customerId}}", "m", 1, expiry: TimeSpan.FromMinutes(1)))
            .Given(Redis.HashSet("ttl-h:{{customerId}}", "f", "{}", expiry: Timeout.InfiniteTimeSpan))
            .When(_ => Task.CompletedTask)
            .Then(Redis.Set("ttl:{{customerId}}", """{ "a": 2 }"""))
            .Then(Redis.Get("ttl:{{customerId}}").Matches("""{ "a": 2 }"""))
            .Then(async ctx =>
            {
                var db = await ctx.Fixture.Feature<RedisFeature>().DatabaseAsync();
                var ttl = await db.KeyTimeToLiveAsync(ctx.Variables.ExpandText("ttl:{{customerId}}"));
                Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
                Assert.InRange((await db.KeyTimeToLiveAsync(ctx.Variables.ExpandText("ttl-z:{{customerId}}")))!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
                Assert.Null(await db.KeyTimeToLiveAsync(ctx.Variables.ExpandText("ttl-h:{{customerId}}")));
            })
            .RunAsync();
    }

    [Fact]
    public async Task key_prefix_applies_to_writes_and_reads()
    {
        var prefixed = new MockBackedFixture(containers.MockUrl, containers.RedisConnection, keyPrefix: "test:{{brand}}:");
        await prefixed.InitializeAsync();
        try
        {
            await prefixed.Scenario()
                .Given(Redis.JsonSet("doc:{{customerId}}", """{ "items": [{ "id": 1 }, { "id": 2 }] }"""))
                .When(_ => Task.CompletedTask)
                .Then(Redis.JsonGet("doc:{{customerId}}").Contains("""{ "id": 2 }""", within: "items"))
                .Then(async ctx =>
                {
                    var db = await ctx.Fixture.Feature<RedisFeature>().DatabaseAsync();
                    Assert.True(await db.KeyExistsAsync(ctx.Variables.ExpandText("test:sbx:doc:{{customerId}}")));
                    Assert.False(await db.KeyExistsAsync(ctx.Variables.ExpandText("doc:{{customerId}}")));
                })
                .RunAsync();
        }
        finally
        {
            await prefixed.DisposeAsync();
        }
    }

    [Fact]
    public async Task pubsub_subscribe_then_published_on()
    {
        await containers.Fixture.Scenario()
            .Given(Redis.Subscribe("test:{{brand}}:wallet:{{customerId}}"))
            .When(async ctx =>
            {
                var db = await ctx.Fixture.Feature<RedisFeature>().DatabaseAsync();
                await db.PublishAsync(StackExchange.Redis.RedisChannel.Literal(ctx.Variables.ExpandText("test:{{brand}}:wallet:{{customerId}}")),
                    ctx.Variables.ExpandText("""{"customerId":"{{customerId}}","amount":5}"""));
            })
            .Then(Redis.PublishedOn("test:{{brand}}:wallet:{{customerId}}", """{ "amount": 5 }"""))
            .RunAsync();
    }

    [Fact]
    public async Task redis_seed_and_read_steps()
    {
        await containers.Fixture.Scenario()
            .Given(Redis.Set("unit:{{customerId}}", """{ "customerId": "{{customerId}}", "limit": 10 }"""))
            .Given(Redis.HashSet("unit-hash:{{brand}}", "{{customerId}}", """{ "tier": "gold" }"""))
            .When(_ => Task.CompletedTask)
            .Then(Redis.Get("unit:{{customerId}}").MatchesExactly("""{ "customerId": "{{customerId}}", "limit": 10 }"""))
            .Then(Redis.HashGet("unit-hash:{{brand}}", "{{customerId}}").Eventually().Matches("""{ "tier": "gold" }"""))
            .RunAsync();
    }
}
