namespace Scenarify;

/// <summary>
/// Fixture feature for services that use Azure Event Hubs: an <see cref="EventHubsSender"/> for the hubs the
/// test publishes to and an <see cref="EventHubsRecorder"/> for the hubs it observes, all in one namespace.
/// Both start before the service health wait, so the recorder's tail position predates anything the service sends.
/// </summary>
/// <param name="publish">Event Hub names the tests publish to.</param>
/// <param name="record">Event Hub names the tests observe.</param>
/// <param name="connectionString">Overrides <c>EventHubs:ConnectionString</c> from the fixture configuration.</param>
/// <param name="consumerGroup">Consumer group the recorder reads on; default <c>$Default</c>. Use a dedicated one when the
/// service also reads the recorded hubs and the namespace caps readers per group.</param>
public sealed class EventHubsFeature(
    IEnumerable<string>? publish = null,
    IEnumerable<string>? record = null,
    string? connectionString = null,
    string? consumerGroup = null) : IFixtureFeature
{
    /// <summary>The Event Hubs emulator's fixed local connection string, used when configuration has none.</summary>
    public const string DefaultConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private readonly List<string> publishHubs = publish?.ToList() ?? [];
    private readonly List<string> recordHubs = record?.ToList() ?? [];
    private EventHubsSender? sender;
    private EventHubsRecorder? recorder;

    /// <summary>The resolved connection string (available after start).</summary>
    public string ConnectionString { get; private set; } = connectionString ?? "";

    /// <summary>The consumer group the recorder reads on, or null for <c>$Default</c>.</summary>
    public string? ConsumerGroup { get; } = consumerGroup;

    /// <summary>The sender; throws when no publish hubs were configured.</summary>
    public EventHubsSender Sender =>
        sender ?? throw new InvalidOperationException("EventHubsFeature has no publish hubs: pass publish: [\"hub\"].");

    /// <summary>The recorder; throws when no record hubs were configured.</summary>
    public EventHubsRecorder Recorder =>
        recorder ?? throw new InvalidOperationException("EventHubsFeature records no hubs: pass record: [\"hub\"].");

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (ConnectionString.Length == 0)
            ConnectionString = fixture.Config["EventHubs:ConnectionString"] ?? DefaultConnectionString;

        if (publishHubs.Count > 0)
            sender = await EventHubsSender.StartAsync(ConnectionString, publishHubs, ct);
        if (recordHubs.Count > 0)
            recorder = await EventHubsRecorder.StartAsync(ConnectionString, recordHubs, ConsumerGroup, ct);
        fixture.Log($"Event Hubs ready: publish [{string.Join(", ", publishHubs)}], record [{string.Join(", ", recordHubs)}]");
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
