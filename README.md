# Scenarify

[![CI](https://github.com/bklooste/Scenarify/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/Scenarify/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.svg)](https://www.nuget.org/packages/Scenarify)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.Redis.svg?label=Scenarify.Redis)](https://www.nuget.org/packages/Scenarify.Redis)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.EventHubs.svg?label=Scenarify.EventHubs)](https://www.nuget.org/packages/Scenarify.EventHubs)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.ServiceBus.svg?label=Scenarify.ServiceBus)](https://www.nuget.org/packages/Scenarify.ServiceBus)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.Kafka.svg?label=Scenarify.Kafka)](https://www.nuget.org/packages/Scenarify.Kafka)
[![NuGet](https://img.shields.io/nuget/v/Scenarify.Kinesis.svg?label=Scenarify.Kinesis)](https://www.nuget.org/packages/Scenarify.Kinesis)

Given\*/When/Then+ scenario testing for xUnit v3 service tests: JSON matching with token expansion, MockServer
stubs, a `ServiceTestFixture` that spins up the service under test, and opt-in add-ons for asserting on and
publishing messages over Redis Streams (`Scenarify.Redis`), Azure Event Hubs (`Scenarify.EventHubs`), Azure
Service Bus (`Scenarify.ServiceBus`), Kafka (`Scenarify.Kafka`) and AWS Kinesis (`Scenarify.Kinesis`).

## Why not just write the HTTP calls directly?

You can, and Scenarify doesn't stop you (see "Escape hatches" below) — but most service tests don't need to:

- **No `Task.Delay`.** Every step that waits (`Mock.Received`, `Streams.Published`, `.Eventually()`) polls with a
  real timeout and reports the *last actual failure*, not a generic "still false after N tries."
- **A broken dependency costs one timeout, not one per wait.** When three waits in a row each burn their full
  timeout, the environment is down rather than slow, and the waits after them fail immediately instead of each
  spending another 30s to learn the same thing — the difference between a two-minute red build and a forty-minute
  one. A success clears the count, and a deliberately fast negative test never trips it. Tune with
  `Eventually.ConsecutiveTimeoutLimit` / `EVENTUALLY_TIMEOUT_LIMIT`, or set it to 0 to poll every time.
- **JSON checks that don't fight you.** Subset matching by default (`Matches`), matcher tokens for the values you
  don't control (`{{any:guid}}`, `{{any:datetime}}`, `{{contains:text}}`, `{{absent}}`), and a failure message that
  lists every mismatched path before printing the whole body — not just "expected true, got false."
- **Isolation instead of cleanup.** Fresh `{{customerId}}`/`{{guid}}` tokens per scenario mean tests can run in any
  order, in parallel, with no `ResetAsync()` between them.
- **One fixture, not six.** `ServiceTestFixture` handles configuration layering, MockServer setup, health waits, and
  optional add-on features (`RedisFeature` for streams). Every project's `Fixture.cs` becomes a handful of lines.

## Features

- **Scenario builder**: `Given*` (setup) → `When` (trigger) → `Then+` (checks). There is exactly one `When`: earlier
  sends that only set the stage are `.Given(...)`; a trigger that genuinely is a short run of actions is
  `.When(first, second, ...)` (one When, children run in order, sharing variables). `WhenConcurrently` is for
  concurrency tests and a `.Given(ctx => ...)`/`.When(...)`/`.Then(...)` escape hatch for anything else.
- **HTTP steps**: `Http.Get/Post/Put/Patch/Delete`, scopes/brand/user headers with token expansion, `.Eventually()`
  to poll a Given, `.Capture(name, path)`, snapshot and subset/exact JSON checks.
- **MockServer steps**: `Mock.Get/Post/Put/Delete/Stub` expectations (request body/header/query matching),
  `Mock.Received`/`Mock.NotReceived` against recorded requests, native MockServer expectation JSON.
- **JSON**: `Json.File/Inline/From/Parse<T>` sources, `Json.Patch` (RFC 7396 merge patch), `Json.Format` for mixing
  C# values with `{{tokens}}` in one template without fighting C# raw-string interpolation, `Json.Array`.
- **Tokens**: built-ins (`brand`, `customerId`, `guid`, `now`, `now+1d`/`now-30m`), `.With(name, value)` for
  scenario-scoped variables, `.Capture` for values pulled out of a response.
- **Snapshots**: `.MatchesSnapshot()` against a reviewed `<test>.snapshot.json`, `$only`/`$ignoring` path selection,
  reverse token substitution, and a `.received.json` writer for a manual accept step — nothing is ever auto-accepted.
- **`ServiceTestFixture`**: configuration layering (in-memory defaults → env vars → `DOTNET_` env vars), MockServer
  init with a single reset, parallel health waits (including other services your test calls directly), and
  `EnsureOnceAsync` for setup shared across tests.
- **`StandardApiTests`**: one line covering health + the OpenAPI/Swagger document for every service.
- **`Scenarify.Redis`** (add-on): `Streams.Publish`/`Streams.Published`/`Streams.NotPublished` over
  [RedisEvents](https://github.com/bklooste/RedisEvents), `Redis.Set/Get/HashSet/SortedSetAdd/JsonSet` seeding with a
  key prefix and default TTL, `Redis.Subscribe`/`Redis.PublishedOn` for pub/sub, and a `RedisFeature` that starts
  alongside the service under test.
- **`Scenarify.EventHubs`** (add-on): `EventHubs.Publish`/`Published`/`NotPublished` over Azure Event Hubs, reading
  every partition from the tail at start so a recorder never replays history. Correlation id uses the SDK's native
  `EventData.CorrelationId`; message type and extra headers travel in `EventData.Properties`.
- **`Scenarify.ServiceBus`** (add-on): `ServiceBus.Send`/`Sent`/`NotSent` over Azure Service Bus queues, or topics
  via a subscription name, with native `CorrelationId`/`ApplicationProperties`/`PartitionKey` support.
- **`Scenarify.Kafka`** (add-on): `Kafka.Produce`/`Produced`/`NotProduced` over Apache Kafka, seeking every assigned
  partition to its high watermark at start for the same tail-only recording guarantee. Message type and correlation
  id travel as record headers (Kafka has no native fields for either).
- **`Scenarify.Kinesis`** (add-on): `Kinesis.Publish`/`Published`/`NotPublished` over AWS Kinesis Data Streams.
  Kinesis records carry no type, correlation id or headers, so — unlike the other add-ons — these steps only match
  on partition key and JSON body content; nothing is invented to fake the missing fields.
- Case records (`PostThenGet`, `PostThenResponse`, `PostThenMockReceived`, plus the equivalents named for each add-on's
  own vocabulary — `PublishThenGet`/`PostThenPublished`/`PublishThenMockReceived` in `Scenarify.Redis`,
  `EventHubsPublishThenGet`/`EventHubsPostThenPublished`/`EventHubsPublishThenMockReceived` in `Scenarify.EventHubs`,
  `SendThenGet`/`PostThenSent`/`SendThenMockReceived` in `Scenarify.ServiceBus`,
  `ProduceThenGet`/`PostThenProduced`/`ProduceThenMockReceived` in `Scenarify.Kafka`, and the `Kinesis`-prefixed
  equivalents in `Scenarify.Kinesis`) for theory-driven tests, all xUnit-serializable so `[MemberData]` rows show up
  and rerun individually.

## Quickstart

```bash
dotnet add package Scenarify
dotnet add package Scenarify.Redis       # only if your test publishes or observes Redis Streams messages
dotnet add package Scenarify.EventHubs   # only for Azure Event Hubs
dotnet add package Scenarify.ServiceBus  # only for Azure Service Bus
dotnet add package Scenarify.Kafka       # only for Apache Kafka
dotnet add package Scenarify.Kinesis     # only for AWS Kinesis Data Streams
```

A fixture, once per test project:

```csharp
public sealed class Fixture() : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,                                     // wait, reset once, then load MockServerFiles
    MockServerFiles = ["mockserver/pricing-service.json"], // loaded before the health wait
    DefaultScopes = "orders-r:{{brand}} orders-w",         // sent as auth-claim-scopes (tokens expand)
    Features = [new RedisFeature(publish: ["orders"], record: ["order-shipped"])],
});

[CollectionDefinition(nameof(ServiceTestCollection))]
public sealed class ServiceTestCollection : ICollectionFixture<Fixture>;
```

A scenario:

```csharp
[Fact]
public Task order_created_then_listed() =>
    fixture.Scenario()
        .When(Http.Post("v1/orders/{{brand}}", "cases/orders/create.json"))
        .Capture("orderId", "$")
        .Then(Http.Get("v1/orders/{{brand}}/{{orderId}}").Matches("""{ "status": "Pending" }"""))
        .RunAsync();

[Fact]
public Task order_shipped_notifies_customer() =>
    fixture.Scenario()
        .Given(Mock.Post("/notifications/v1/send/{{customerId}}"))
        .When(Streams.Publish("orders", "cases/orders/shipped.json",
            type: typeof(OrderShipped).FullName, key: "{{orderId}}"))
        .Then(Mock.Received("POST", "/notifications/v1/send/{{customerId}}",
            """{ "template": "OrderShipped" }"""))
        .RunAsync();
```

### A more complete example: several mocks, a capture, and a stream check

Given\* steps aren't limited to one mock, and a scenario can check several things about the same trigger — here a
checkout charges a payment gateway, reserves stock, and its own status flips once both finish:

```csharp
[Fact]
public Task checkout_charges_payment_and_reserves_stock() =>
    fixture.Scenario()
        .Given(Mock.Post("/payments/v1/charge/{{customerId}}", """{ "status": "Approved" }"""))
        .Given(Mock.Post("/inventory/v1/reserve", requestBody: """{ "sku": "{{sku}}" }"""))
        .When(Http.Post("v1/checkout/{{brand}}", "cases/checkout/cart.json"))
        .Capture("orderId", "$.orderId")
        .Then(Http.Get("v1/orders/{{brand}}/{{orderId}}").Matches("""{ "status": "Paid" }"""))
        .Then(Mock.Received("POST", "/payments/v1/charge/{{customerId}}", """{ "amount": 49.99 }"""))
        .Then(Mock.Received("POST", "/inventory/v1/reserve", """{ "sku": "{{sku}}" }"""))
        .RunAsync();
```

### Redis Streams (`Scenarify.Redis`)

Seed Redis state with a `Given`, trigger over HTTP or by publishing a message, then assert on a message a downstream
consumer published — `Streams.Published<T>` waits for a subset match, the same way `Mock.Received` does for HTTP:

```csharp
[Fact]
public Task order_shipped_reserves_stock() =>
    fixture.Scenario()
        .Given(Redis.Set("inventory:{{sku}}", """{ "quantity": 10 }"""))
        .When(Streams.Publish("orders", "cases/orders/shipped.json",
            type: typeof(OrderShipped).FullName, key: "{{orderId}}"))
        .Then(Streams.Published<StockReserved>("inventory-events",
            """{ "sku": "{{sku}}", "quantity": 1 }"""))
        .RunAsync();
```

`Redis.Set/HashSet/SortedSetAdd/JsonSet` are discouraged outside cases like this one, where nothing else can reach the
state a test needs to seed — prefer driving setup through the API or an event. Seeded keys expire after
`RedisFeature(seedTtl:)` (10 minutes by default) so a forgotten write doesn't linger.

### Other broker add-ons

`Scenarify.EventHubs`, `Scenarify.ServiceBus` and `Scenarify.Kafka` follow the same shape as `Scenarify.Redis` — a
feature in `ServiceTestOptions.Features`, a `Publish`/`Send`/`Produce` to trigger, a `Published`/`Sent`/`Produced` to
assert — with each add-on's steps named for its own ecosystem's vocabulary rather than forcing one term everywhere:

```csharp
// Scenarify.EventHubs — correlation id is the SDK's native EventData.CorrelationId
.When(EventHubs.Publish("orders", "cases/orders/shipped.json", type: typeof(OrderShipped).FullName, key: "{{orderId}}"))
.Then(EventHubs.Published<StockReserved>("inventory-events", """{ "sku": "{{sku}}" }"""))

// Scenarify.ServiceBus — queues by name, topics via subscriptions: { "order-events": "order-events-sub" }
.When(ServiceBus.Send("orders", "cases/orders/shipped.json", type: typeof(OrderShipped).FullName))
.Then(ServiceBus.Sent<StockReserved>("inventory-events", """{ "sku": "{{sku}}" }"""))

// Scenarify.Kafka — type/correlation id travel as record headers; a recorder seeks every assigned partition
// to its high watermark at start, so it never replays history already on the topic
.When(Kafka.Produce("orders", "cases/orders/shipped.json", type: typeof(OrderShipped).FullName))
.Then(Kafka.Produced<StockReserved>("inventory-events", """{ "sku": "{{sku}}" }"""))
```

`Scenarify.Kinesis` is narrower by necessity: Kinesis records have no type, correlation id or header fields, so
`Kinesis.Publish`/`Published`/`NotPublished` match only on partition key and JSON body content — nothing is
invented to fake a wire format Kinesis doesn't have:

```csharp
.When(Kinesis.Publish("orders", "cases/orders/shipped.json", partitionKey: "{{orderId}}"))
.Then(Kinesis.Published("inventory-events", """{ "sku": "{{sku}}" }"""))
```

### Workers with no HTTP listener

By default the fixture polls `GET {SvcHttpUrl}/health`. For a worker, say so and (optionally) supply a readiness probe:

```csharp
new ServiceTestOptions
{
    ServesHttp = false,   // no /health wait; StandardApiTests becomes a no-op
    Readiness = ReadinessProbe.PublishAndObserve(
        publish: (f, ct) => PublishPing(f, ct),          // repeated each attempt
        observe: (f, ct) => PingWasHandled(f, ct)),      // true once the effect is seen
}
```

`ReadinessProbe.From(...)` wraps any check, `ReadinessProbe.None` skips waiting, or implement `IReadinessProbe`. A probe
runs after features start and is retried until `StartupTimeout`; other `Services` are still health-checked.

`StartupTimeout` is applied per startup phase — MockServer, then `Features`, then the health and readiness waits — not
as one budget shared across all of them. A feature that takes its time slows startup; it does not leave the health wait
with a token that is already cancelled and a failure that blames the service.

### Mixing C# values with tokens

Tokens use `{{name}}`, and so does C# raw-string interpolation — a `$$"""` string reads `{{customerId}}` as a C#
expression, so mixing a C# value with a scenario token forces an unreadable `$$$"""` string with C# values in triple
braces. Use `Json.Format` instead: write the template with tokens only, pass the C# values as an object (looked up
before the scenario's own variables):

```csharp
Json.Format("""{ "jobId": "{{jobId}}", "attempt": "{{attempt}}", "status": "{{status}}" }""",
    new { jobId = Json.Token($"job{n}"), attempt = n, status })
```

Or `.With(name, value)` when the value belongs to the whole scenario rather than one body — tokens inside the value
still expand when it's used:

```csharp
.With("lineItems", items.Select((item, i) => new { id = $"{i + 1}", item, addedAtUtc = "{{now}}" }))
.When(PublishOrder("""{ "lineItems": "{{lineItems}}" }"""))
```

### Checks

| Method | Compares |
|---|---|
| `Matches(json)` | Only the fields written; extras are ignored (the default) |
| `MatchesExactly(json)` | All fields; extras fail |
| `MatchesSnapshot()` | The recorded `snapshots/<Class>/<test>.snapshot.json` |
| `Contains(json, within?)` | Some element subset-matches, in the body array or every array a path selects |
| `Satisfies<T>(t => ...)` | A C# assertion, for computed expectations |

Matcher values: `{{contains:text}}`, `{{absent}}` (missing, or a default the service omitted), `{{any}}`,
`{{any:guid}}`, `{{any:datetime}}`, `{{any:number}}`, `{{any:string}}`, `{{any:bool}}`, `{{any:object}}`,
`{{any:array}}`. Arrays are ordered unless `.UnorderedArrays()` is set. A failure lists every difference by JSON
path, then the whole actual body.

### Accepting a snapshot change

1. `.MatchesSnapshot()` fails and writes `<test>.received.json` next to the expected snapshot file.
2. Review it — known values already come back as `{{tokens}}`, unknown ids/times as `{{any:guid}}`/`{{any:datetime}}`.
3. Accept it by moving the file over the expected one (dropping the `.received` suffix), e.g.:
   ```bash
   for f in **/*.received.json; do mv "$f" "${f%.received.json}.snapshot.json"; done
   ```
   Nothing is ever accepted automatically.

### Escape hatches

A test that doesn't fit this shape (perf, concurrency, HTML flows) uses the primitives directly: `fixture.Client(...)`
(with `PostJsonAsync`/`PutJsonAsync(path, JsonSource, fixture.NewVariables())` for bodies), `fixture.ClientsFor("name")`,
`Eventually.Assert(...)`, `JsonMatch.AssertMatches(...)`, `fixture.Feature<RedisFeature>().Recorder`.

## Performance and security

Scenarify wraps `HttpClient`, `MockServerClient`, and (via `Scenarify.Redis`) `RedisEvents` — its own overhead is
negligible next to the network calls it's making; the [`RedisEvents` performance
notes](https://github.com/bklooste/RedisEvents#performance) apply to `Scenarify.Redis`'s use of it. Scenarify makes
no security decisions of its own: it sends whatever headers/tokens your fixture is configured with, and MockServer/
Redis credentials are exactly whatever connection details you give it. Treat test-fixture tokens and MockServer
expectations as test data, not production secrets.

## Repository layout

```
src/Scenarify/        Core: JSON sources/tokens/matching/snapshots, Eventually, MockServer helpers,
                       TestClients, ServiceTestFixture, the scenario builder, StandardApiTests
src/Scenarify.Redis/      Redis Streams add-on: StreamSender/StreamRecorder, RedisFeature, Streams.*/Redis.* steps
src/Scenarify.EventHubs/  Azure Event Hubs add-on: EventHubsSender/EventHubsRecorder, EventHubsFeature, EventHubs.* steps
src/Scenarify.ServiceBus/ Azure Service Bus add-on: ServiceBusSender/ServiceBusRecorder, ServiceBusFeature, ServiceBus.* steps
src/Scenarify.Kafka/      Apache Kafka add-on: KafkaSender/KafkaRecorder, KafkaFeature, Kafka.* steps
src/Scenarify.Kinesis/    AWS Kinesis add-on: KinesisSender/KinesisRecorder, KinesisFeature, Kinesis.* steps
tst/Scenarify.UnitTests/         Fast unit tests (TestType=UnitTest) — no Docker required
tst/Scenarify.Redis.Tests/       Testcontainers tests against real Redis + MockServer (TestType=ServiceTest)
tst/Scenarify.EventHubs.Tests/   Testcontainers tests against the real Azure Event Hubs emulator + Azurite
tst/Scenarify.ServiceBus.Tests/  Testcontainers tests against the real Azure Service Bus emulator + Azure SQL Edge
tst/Scenarify.Kafka.Tests/       Testcontainers tests against a real single-node KRaft Kafka broker
tst/Scenarify.Kinesis.Tests/     Testcontainers tests against real LocalStack (Kinesis) + MockServer
```

## Building & testing

```bash
dotnet build Scenarify.slnx
dotnet test Scenarify.slnx --filter "TestType=UnitTest"      # fast, no Docker
dotnet test Scenarify.slnx --filter "TestType=ServiceTest"   # needs Docker: Redis, MockServer, the Azure Event Hubs
                                                               # and Service Bus emulators, Kafka, and LocalStack
```

## License

MIT — see [LICENSE](LICENSE).
