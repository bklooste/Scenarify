using Amazon.Kinesis;
using Amazon.Kinesis.Model;

using Xunit.Sdk;

namespace XUnitKinesisTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class KinesisTests(Containers containers)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly KinesisFeature kinesis = containers.Fixture.Feature<KinesisFeature>();

    [Fact]
    public async Task typed_message_round_trips_through_sender_and_recorder()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-1", 12.5m, ["a"]);
        await kinesis.Sender.PublishAsync(Containers.Orders, order, order.OrderId, TestContext.Current.CancellationToken);

        var received = await kinesis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == order.OrderId, Wait);

        Assert.Equal(order.Amount, received.Amount);
        var raw = kinesis.Recorder.Messages(Containers.Orders).Single(m => m.PartitionKey == order.OrderId);
        Assert.Null(raw.Type);
        Assert.Empty(raw.Headers);
        Assert.False(string.IsNullOrEmpty(raw.SequenceNumber));
        Assert.NotNull(raw.ArrivedAt);
    }

    [Fact]
    public async Task unmatched_messages_stay_available_to_later_waits()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await kinesis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(first, "c", 1, []));
        await kinesis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(second, "c", 2, []));

        await kinesis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == second, Wait);
        await kinesis.Recorder.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == first, Wait);
    }

    [Fact]
    public async Task recorder_starts_at_the_tail()
    {
        var before = Guid.NewGuid().ToString();
        var after = Guid.NewGuid().ToString();
        await kinesis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(before, "c", 1, []));

        await using var late = await KinesisRecorder.StartAsync(kinesis.Client, [Containers.Orders], TestContext.Current.CancellationToken);
        await kinesis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(after, "c", 1, []));

        await late.WaitForAsync<OrderPlaced>(Containers.Orders, o => o.OrderId == after, Wait);
        Assert.DoesNotContain(late.Messages(Containers.Orders), m => m.Text.Contains(before, StringComparison.Ordinal));
    }

    [Fact]
    public async Task scenario_publish_then_published_with_key_and_capture()
    {
        string? captured = null;
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .Given(Kinesis.Publish(Containers.Keyed, """{ "orderId": "{{orderId}}", "amount": 1 }""", partitionKey: "someone-else"))
            .When(Kinesis.Publish(Containers.Keyed,
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "amount": 12.5, "items": ["x", "y"] }""",
                partitionKey: "{{customerId}}"))
            .Then(Kinesis.Published(Containers.Keyed, """{ "orderId": "{{orderId}}", "items": ["x", "y"] }""")
                .WithKey("{{customerId}}")
                .Satisfies<OrderPlaced>(o => Assert.Equal(12.5m, o.Amount))
                .Capture("amount", "amount"))
            .Then(Kinesis.Published<OrderPlaced>(Containers.Keyed, """{ "orderId": "{{orderId}}", "amount": 1 }"""))
            .Then(ctx =>
            {
                captured = ctx.Variables.GetString("amount");
                return Task.CompletedTask;
            })
            .RunAsync();

        Assert.Equal("12.5", captured);
    }

    [Fact]
    public async Task typed_json_source_publishes_its_body()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-typed", 3, []);
        await containers.Fixture.Scenario()
            .When(Kinesis.Publish(Containers.Orders, Json.From(order)))
            .Then(Kinesis.Published(Containers.Orders, Json.From(order)).Exactly().UnorderedArrays()
                .Within(Wait))
            .RunAsync();
    }

    [Fact]
    public async Task published_timeout_shows_the_nearest_candidate_diff()
    {
        var orderId = Guid.NewGuid().ToString();
        await kinesis.Sender.PublishAsync(Containers.Orders, new OrderPlaced(orderId, "c-near", 5, []));

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", orderId)
            .When(_ => Task.CompletedTask)
            .Then(Kinesis.Published(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 6 }""").Within(TimeSpan.FromSeconds(2)))
            .RunAsync());

        Assert.Contains("Nearest candidates", ex.Message);
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task not_published_passes_for_other_bodies_and_fails_for_a_match()
    {
        await containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kinesis.Publish(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 1 }"""))
            .Then(Kinesis.Published(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Kinesis.NotPublished(Containers.Orders, """{ "orderId": "{{orderId}}", "amount": 2 }"""))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => containers.Fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .When(Kinesis.Publish(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Kinesis.Published(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .Then(Kinesis.NotPublished(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task published_works_as_a_given_barrier()
    {
        var order = new OrderPlaced(Guid.NewGuid().ToString(), "c-barrier", 1, []);
        await kinesis.Sender.PublishAsync(Containers.Orders, order);
        await containers.Fixture.Scenario()
            .With("orderId", order.OrderId)
            .Given(Kinesis.Published(Containers.Orders, """{ "orderId": "{{orderId}}" }"""))
            .When(_ => Task.CompletedTask)
            .Then(_ => Task.CompletedTask)
            .RunAsync();
    }

    [Fact]
    public async Task feature_start_creates_missing_streams_and_waits_for_active()
    {
        var fresh = $"fresh-{Guid.NewGuid():N}";
        var fixture = new MockBackedFixture(containers.MockUrl, containers.KinesisUrl, extraStreams: [fresh]);
        await fixture.InitializeAsync();
        try
        {
            var feature = fixture.Feature<KinesisFeature>();
            var description = await feature.Client.DescribeStreamAsync(new DescribeStreamRequest { StreamName = fresh },
                TestContext.Current.CancellationToken);
            Assert.Equal(StreamStatus.ACTIVE, description.StreamDescription.StreamStatus);
            await feature.Sender.PublishJsonAsync(fresh, """{ "ok": true }""", ct: TestContext.Current.CancellationToken);

            // Existing streams are reused, not recreated.
            await KinesisFeature.EnsureStreamAsync(feature.Client, fresh, TestContext.Current.CancellationToken);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task unconfigured_streams_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => kinesis.Sender.PublishJsonAsync("nope", "{}"));
        Assert.Throws<InvalidOperationException>(() => kinesis.Recorder.Messages("nope"));
    }

    [Fact]
    public async Task case_record_runs_post_then_published()
    {
        // MockServer stands in for the service, so the record the "service" would put is put by the test up front.
        var marker = Guid.NewGuid().ToString();
        await kinesis.Sender.PublishJsonAsync(Containers.Orders, $$"""{ "marker": "{{marker}}" }""");
        await containers.Fixture.RunAsync(new KinesisPostThenPublished(
            "post-then-published", "echo/{{customerId}}", "{}", Containers.Orders, $$"""{ "marker": "{{marker}}" }""",
            Mocks: ["""[{ "httpRequest": { "method": "POST", "path": "/echo/{{customerId}}" }, "httpResponse": { "statusCode": 200 } }]"""]));
    }
}
