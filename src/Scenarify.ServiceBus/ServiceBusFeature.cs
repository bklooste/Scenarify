namespace Scenarify;

/// <summary>
/// Fixture feature for services that use Azure Service Bus: a <see cref="ServiceBusSender"/> for the queues/topics the
/// test sends to and a <see cref="ServiceBusRecorder"/> for the queues/subscriptions it observes. Both start before the
/// service health wait. The entities must already exist (Service Bus clients cannot create them).
/// </summary>
/// <param name="publish">Queues or topics the tests send to.</param>
/// <param name="record">Queues or topics the tests observe; a topic needs an entry in <paramref name="subscriptions"/>.</param>
/// <param name="subscriptions">Topic name to the subscription the recorder receives from. Names absent here are queues.</param>
/// <param name="connectionString">Overrides <c>ServiceBus:ConnectionString</c> from the fixture configuration.</param>
public sealed class ServiceBusFeature(
    IEnumerable<string>? publish = null,
    IEnumerable<string>? record = null,
    IReadOnlyDictionary<string, string>? subscriptions = null,
    string? connectionString = null) : IFixtureFeature
{
    /// <summary>Default connection string when configuration has none: the local Service Bus emulator.</summary>
    public const string DefaultConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private readonly List<string> publishEntities = publish?.ToList() ?? [];
    private readonly List<string> recordEntities = record?.ToList() ?? [];
    private ServiceBusSender? sender;
    private ServiceBusRecorder? recorder;

    /// <summary>The resolved connection string (available after start).</summary>
    public string ConnectionString { get; private set; } = connectionString ?? "";

    /// <summary>The sender; throws when no publish entities were configured.</summary>
    public ServiceBusSender Sender =>
        sender ?? throw new InvalidOperationException("ServiceBusFeature has no publish entities: pass publish: [\"queue-or-topic\"].");

    /// <summary>The recorder; throws when no record entities were configured.</summary>
    public ServiceBusRecorder Recorder =>
        recorder ?? throw new InvalidOperationException("ServiceBusFeature records no entities: pass record: [\"queue-or-topic\"].");

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (ConnectionString.Length == 0)
            ConnectionString = fixture.Config["ServiceBus:ConnectionString"] ?? DefaultConnectionString;

        if (publishEntities.Count > 0)
            sender = ServiceBusSender.Start(ConnectionString, publishEntities);
        if (recordEntities.Count > 0)
            recorder = await ServiceBusRecorder.StartAsync(ConnectionString, recordEntities, subscriptions, ct);
        fixture.Log($"Service Bus ready: publish [{string.Join(", ", publishEntities)}], record [{string.Join(", ", recordEntities)}]");
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
