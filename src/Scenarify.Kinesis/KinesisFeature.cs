using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;

namespace Scenarify;

/// <summary>
/// Fixture feature for services that use AWS Kinesis Data Streams: a <see cref="KinesisSender"/> for the streams the
/// test publishes to and a <see cref="KinesisRecorder"/> for the streams it observes. Every named stream is created
/// (when missing) and waited on until <c>ACTIVE</c> before the service health wait, since Kinesis, unlike Redis, does
/// not create a stream on first write.
/// </summary>
/// <param name="publish">Streams the tests publish to.</param>
/// <param name="record">Streams the tests observe.</param>
/// <param name="serviceUrl">Overrides <c>Kinesis:ServiceUrl</c> from the fixture configuration.</param>
/// <param name="region">Overrides <c>Kinesis:Region</c> from the fixture configuration.</param>
public sealed class KinesisFeature(
    IEnumerable<string>? publish = null,
    IEnumerable<string>? record = null,
    string? serviceUrl = null,
    string? region = null) : IFixtureFeature
{
    /// <summary>Default endpoint when configuration has none: LocalStack's edge port.</summary>
    public const string DefaultServiceUrl = "http://localhost:4566";

    /// <summary>Default region when configuration has none.</summary>
    public const string DefaultRegion = "us-east-1";

    private readonly List<string> publishStreams = publish?.ToList() ?? [];
    private readonly List<string> recordStreams = record?.ToList() ?? [];
    private KinesisSender? sender;
    private KinesisRecorder? recorder;
    private AmazonKinesisClient? client;

    /// <summary>The resolved endpoint (available after start).</summary>
    public string ServiceUrl { get; private set; } = serviceUrl ?? "";

    /// <summary>The resolved region (available after start).</summary>
    public string Region { get; private set; } = region ?? "";

    /// <summary>The Kinesis client; throws before start.</summary>
    public AmazonKinesisClient Client =>
        client ?? throw new InvalidOperationException("KinesisFeature has not started.");

    /// <summary>The sender; throws when no publish streams were configured.</summary>
    public KinesisSender Sender =>
        sender ?? throw new InvalidOperationException("KinesisFeature has no publish streams: pass publish: [\"stream\"].");

    /// <summary>The recorder; throws when no record streams were configured.</summary>
    public KinesisRecorder Recorder =>
        recorder ?? throw new InvalidOperationException("KinesisFeature records no streams: pass record: [\"stream\"].");

    /// <summary>A client for <paramref name="serviceUrl"/> with dummy credentials (LocalStack ignores them).</summary>
    public static AmazonKinesisClient CreateClient(string serviceUrl, string region = DefaultRegion) =>
        new(new BasicAWSCredentials("test", "test"), new AmazonKinesisConfig
        {
            ServiceURL = serviceUrl,
            UseHttp = true,
            AuthenticationRegion = region,
        });

    /// <summary>Creates <paramref name="stream"/> (one shard) unless it exists, then waits until it is <c>ACTIVE</c>.</summary>
    public static async Task EnsureStreamAsync(IAmazonKinesis client, string stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            await client.CreateStreamAsync(new CreateStreamRequest { StreamName = stream, ShardCount = 1 }, ct);
        }
        catch (ResourceInUseException)
        {
            // Already exists (or is being created): fall through to the ACTIVE wait.
        }

        while (true)
        {
            var description = await client.DescribeStreamAsync(new DescribeStreamRequest { StreamName = stream }, ct);
            if (description.StreamDescription.StreamStatus == StreamStatus.ACTIVE)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (ServiceUrl.Length == 0)
            ServiceUrl = fixture.Config["Kinesis:ServiceUrl"] ?? DefaultServiceUrl;
        if (Region.Length == 0)
            Region = fixture.Config["Kinesis:Region"] ?? DefaultRegion;

        client = CreateClient(ServiceUrl, Region);
        foreach (var stream in publishStreams.Concat(recordStreams).Distinct(StringComparer.Ordinal))
            await EnsureStreamAsync(client, stream, ct);

        if (publishStreams.Count > 0)
            sender = new KinesisSender(client, publishStreams);
        if (recordStreams.Count > 0)
            recorder = await KinesisRecorder.StartAsync(client, recordStreams, ct);
        fixture.Log($"Kinesis streams ready on {ServiceUrl} ({Region}): publish [{string.Join(", ", publishStreams)}], record [{string.Join(", ", recordStreams)}]");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (recorder is not null)
            await recorder.DisposeAsync();
        client?.Dispose();
    }
}
