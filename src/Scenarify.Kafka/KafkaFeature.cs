namespace Scenarify;

/// <summary>
/// Fixture feature for services that use Apache Kafka: a <see cref="KafkaSender"/> for the topics the test
/// produces to and a <see cref="KafkaRecorder"/> for the topics it observes. Both start before the service health wait.
/// </summary>
/// <remarks>One producer and one consumer cover every topic, so there is no per-topic lifecycle.</remarks>
/// <param name="publish">Topics the tests produce to.</param>
/// <param name="record">Topics the tests observe.</param>
/// <param name="bootstrapServers">Overrides <c>Kafka:BootstrapServers</c> from the fixture configuration.</param>
public sealed class KafkaFeature(
    IEnumerable<string>? publish = null,
    IEnumerable<string>? record = null,
    string? bootstrapServers = null) : IFixtureFeature
{
    /// <summary>Default bootstrap servers when configuration has none.</summary>
    public const string DefaultBootstrapServers = "localhost:9092";

    private readonly List<string> publishTopics = publish?.ToList() ?? [];
    private readonly List<string> recordTopics = record?.ToList() ?? [];
    private KafkaSender? sender;
    private KafkaRecorder? recorder;

    /// <summary>The resolved bootstrap servers (available after start).</summary>
    public string BootstrapServers { get; private set; } = bootstrapServers ?? "";

    /// <summary>The sender; throws when no publish topics were configured.</summary>
    public KafkaSender Sender =>
        sender ?? throw new InvalidOperationException("KafkaFeature has no publish topics: pass publish: [\"topic\"].");

    /// <summary>The recorder; throws when no record topics were configured.</summary>
    public KafkaRecorder Recorder =>
        recorder ?? throw new InvalidOperationException("KafkaFeature records no topics: pass record: [\"topic\"].");

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (BootstrapServers.Length == 0)
            BootstrapServers = fixture.Config["Kafka:BootstrapServers"] ?? DefaultBootstrapServers;

        if (publishTopics.Count > 0)
            sender = KafkaSender.Start(BootstrapServers, publishTopics);
        if (recordTopics.Count > 0)
            recorder = await KafkaRecorder.StartAsync(BootstrapServers, recordTopics, ct);
        fixture.Log($"Kafka ready on {BootstrapServers}: publish [{string.Join(", ", publishTopics)}], record [{string.Join(", ", recordTopics)}]");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (recorder is not null)
            await recorder.DisposeAsync();
        if (sender is not null)
            await sender.DisposeAsync();
    }
}
