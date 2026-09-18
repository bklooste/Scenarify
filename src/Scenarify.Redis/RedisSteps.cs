using System.Text.Json.Nodes;

using StackExchange.Redis;

namespace Scenarify;

/// <summary>
/// Direct Redis steps. <b>Discouraged:</b> seed through the service's API or events where possible; a test that
/// seeds Redis directly says why in a one-line comment. Keys, fields and values expand <c>{{tokens}}</c>, and keys get
/// <see cref="RedisFeature.KeyPrefix"/> in front. Writes expire after <see cref="RedisFeature.SeedTtl"/> (10 minutes
/// by default) unless <c>expiry</c> is passed; <see cref="Timeout.InfiniteTimeSpan"/> means never. Every step works as a
/// Given, the When or a Then.
/// </summary>
public static class Redis
{
    /// <summary><c>SET key json</c>.</summary>
    public static DelegateStep Set(string key, JsonSource value, TimeSpan? expiry = null) =>
        Write($"redis SET {key}", key, expiry, (db, k, vars, ttl) =>
            ttl is { } t ? db.StringSetAsync(k, value.ResolveText(vars), t) : db.StringSetAsync(k, value.ResolveText(vars)));

    /// <summary><c>SET key text</c> with a plain (non-JSON) value.</summary>
    public static DelegateStep SetText(string key, string text, TimeSpan? expiry = null) =>
        Write($"redis SET {key}", key, expiry, (db, k, vars, ttl) =>
            ttl is { } t ? db.StringSetAsync(k, vars.ExpandText(text), t) : db.StringSetAsync(k, vars.ExpandText(text)));

    /// <summary><c>HSET key field json</c>.</summary>
    public static DelegateStep HashSet(string key, string field, JsonSource value, TimeSpan? expiry = null) =>
        Write($"redis HSET {key} {field}", key, expiry, async (db, k, vars, ttl) =>
        {
            await db.HashSetAsync(k, vars.ExpandText(field), value.ResolveText(vars));
            await ExpireAsync(db, k, ttl);
        });

    /// <summary><c>ZADD key score member</c>; the member is text with tokens expanded.</summary>
    public static DelegateStep SortedSetAdd(string key, string member, double score, TimeSpan? expiry = null) =>
        Write($"redis ZADD {key}", key, expiry, async (db, k, vars, ttl) =>
        {
            await db.SortedSetAddAsync(k, vars.ExpandText(member), score);
            await ExpireAsync(db, k, ttl);
        });

    /// <summary><c>JSON.SET key path json</c> (needs the RedisJSON module).</summary>
    public static DelegateStep JsonSet(string key, JsonSource value, string path = "$", TimeSpan? expiry = null) =>
        Write($"redis JSON.SET {key}", key, expiry, async (db, k, vars, ttl) =>
        {
            await db.ExecuteAsync("JSON.SET", k, path, value.ResolveText(vars));
            await ExpireAsync(db, k, ttl);
        });

    /// <summary>
    /// Subscribes to pub/sub <paramref name="channel"/> (tokens expand; no key prefix — channels are not keys) so a later
    /// <see cref="PublishedOn"/> can see what the service publishes. Use as a Given, before the trigger.
    /// </summary>
    public static DelegateStep Subscribe(string channel) =>
        new($"redis SUBSCRIBE {channel}", ctx => ctx.Fixture.Feature<RedisFeature>().SubscribeAsync(ctx.Variables.ExpandText(channel)));

    /// <summary>Then: a message whose JSON subset-matches <paramref name="expected"/> eventually arrives on a subscribed channel.</summary>
    public static DelegateStep PublishedOn(string channel, JsonSource expected, TimeSpan? timeout = null) =>
        new($"redis published on {channel}", async ctx =>
        {
            var name = ctx.Variables.ExpandText(channel);
            var resolved = expected.Resolve(ctx.Variables);
            var feature = ctx.Fixture.Feature<RedisFeature>();
            await Scenarify.Eventually.Assert(() =>
            {
                var messages = feature.ChannelMessages(name);
                if (messages.Any(m => JsonMatch.IsMatch(resolved, TryParse(m))))
                    return Task.CompletedTask;
                throw new Xunit.Sdk.XunitException(
                    $"No message on channel {name} matched {resolved?.ToJsonString()}. Received {messages.Count}: {string.Join(" | ", messages.Take(5))}");
            }, timeout ?? ctx.Timeout, $"published on {name}");
        });

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary><c>GET key</c> returns JSON; add a comparison.</summary>
    public static RedisGetStep Get(string key) =>
        new($"redis GET {key}", async (db, k, _) => (string?)await db.StringGetAsync(k), key, field: null);

    /// <summary><c>HGET key field</c> returns JSON; add a comparison.</summary>
    public static RedisGetStep HashGet(string key, string field) =>
        new($"redis HGET {key} {field}", async (db, k, f) => (string?)await db.HashGetAsync(k, f), key, field);

    /// <summary><c>JSON.GET key</c> returns JSON; add a comparison.</summary>
    public static RedisGetStep JsonGet(string key) =>
        new($"redis JSON.GET {key}", async (db, k, _) => (string?)await db.ExecuteAsync("JSON.GET", k), key, field: null);

    /// <summary>The full key for <paramref name="key"/>: the feature's prefix plus the key, tokens expanded.</summary>
    public static string ResolveKey(ScenarioContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);
        var feature = context.Fixture.Feature<RedisFeature>();
        return context.Variables.ExpandText(feature.KeyPrefix + key);
    }

    private static DelegateStep Write(string description, string key, TimeSpan? expiry,
        Func<IDatabase, string, ScenarioVariables, TimeSpan?, Task> run) =>
        new(description, async ctx =>
        {
            var feature = ctx.Fixture.Feature<RedisFeature>();
            var ttl = expiry ?? feature.SeedTtl;
            await run(await feature.DatabaseAsync(), ResolveKey(ctx, key), ctx.Variables, ttl == Timeout.InfiniteTimeSpan ? null : ttl);
        });

    private static Task ExpireAsync(IDatabase db, string key, TimeSpan? ttl) =>
        ttl is { } t ? db.KeyExpireAsync(key, t) : Task.CompletedTask;
}

/// <summary>Reads a Redis value and checks it as JSON; optionally retried.</summary>
public sealed class RedisGetStep : IThenStep, IGivenStep
{
    private readonly string description;
    private readonly Func<IDatabase, string, string?, Task<string?>> read;
    private readonly string key;
    private readonly string? field;
    private readonly BodyCheck check = new();
    private bool eventually;
    private TimeSpan? timeout;

    internal RedisGetStep(string description, Func<IDatabase, string, string?, Task<string?>> read, string key, string? field)
    {
        this.description = description;
        this.read = read;
        this.key = key;
        this.field = field;
    }

    /// <summary>The value contains everything in <paramref name="expected"/>.</summary>
    public RedisGetStep Matches(JsonSource expected)
    {
        check.Matches(expected);
        return this;
    }

    /// <summary>The value equals <paramref name="expected"/>.</summary>
    public RedisGetStep MatchesExactly(JsonSource expected)
    {
        check.MatchesExactly(expected);
        return this;
    }

    /// <summary>The value (or the arrays <paramref name="within"/> selects) has an element matching <paramref name="element"/>.</summary>
    public RedisGetStep Contains(JsonSource element, string? within = null)
    {
        check.Contains(element, within);
        return this;
    }

    /// <summary>Retry until it matches.</summary>
    public RedisGetStep Eventually(TimeSpan? within = null)
    {
        eventually = true;
        timeout = within;
        return this;
    }

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => ThenAsync(context);

    /// <inheritdoc />
    public async Task ThenAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!check.IsSet)
            throw new InvalidOperationException($"{description} needs Matches(...), MatchesExactly(...) or Contains(...).");
        var db = await context.Fixture.Feature<RedisFeature>().DatabaseAsync();
        var fullKey = Redis.ResolveKey(context, key);
        var fullField = field is null ? null : context.Variables.ExpandText(field);

        async Task VerifyOnce()
        {
            var text = await read(db, fullKey, fullField)
                ?? throw new Xunit.Sdk.XunitException($"{description}: key {fullKey} not found");
            JsonNode? actual = JsonMatch.ParseActual(text);
            check.Verify(actual, context, null, $"{description} ({fullKey})");
        }

        if (eventually)
            await Scenarify.Eventually.Assert(VerifyOnce, timeout ?? context.Timeout, description);
        else
            await VerifyOnce();
    }

    /// <inheritdoc />
    public override string ToString() => description;
}
