using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scenarify;

/// <summary>
/// The values a scenario substitutes for <c>{{name}}</c> tokens in paths, bodies, MockServer
/// expectations and expected results.
/// </summary>
/// <remarks>
/// <para>Built-ins: <c>brand</c>, <c>customerId</c>, <c>guid</c> (fresh per instance), <c>now</c>
/// (ISO-8601 UTC, fixed per instance) and offsets such as <c>now+1d</c>, <c>now-30m</c>
/// (units <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>).</para>
/// <para><c>{{any}}</c> and <c>{{any:kind}}</c> are matchers, not variables: expansion leaves them in
/// place for <see cref="JsonMatch"/>.</para>
/// <para>A token that is a whole JSON string value (<c>"{{stake}}"</c>) is replaced by the variable
/// with its own JSON type, so a numeric variable becomes a number. A token inside a longer string
/// is replaced textually. An unquoted token (<c>"odds": {{odds}}</c>) is replaced by the variable's
/// JSON.</para>
/// </remarks>
public sealed partial class ScenarioVariables
{
    /// <summary>The prefix shared by all matcher tokens.</summary>
    public const string AnyToken = "any";

    /// <summary>The matcher for a missing or default-valued property.</summary>
    public const string AbsentToken = "absent";

    private readonly Dictionary<string, JsonNode?> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonSource> sources = new(StringComparer.Ordinal);
    private readonly ScenarioVariables? parent;
    private readonly DateTimeOffset now;

    /// <summary>Creates variables with the built-ins set.</summary>
    public ScenarioVariables(string brand, string customerId, DateTimeOffset? now = null)
    {
        this.now = now ?? DateTimeOffset.UtcNow;
        Set("brand", brand);
        Set("customerId", customerId);
        Set("guid", Guid.NewGuid().ToString());
        Set("now", FormatTime(this.now));
    }

    private ScenarioVariables(ScenarioVariables parent)
    {
        now = parent.now;
        this.parent = parent;
    }

    /// <summary>
    /// A child scope with <paramref name="locals"/> on top (an anonymous object, record or dictionary; property names
    /// are the token names). Used by <see cref="Json.Format"/>; the original is unchanged. Scenario variables still
    /// expand in their own scope, so a local never changes what a token inside a stored variable means.
    /// </summary>
    public ScenarioVariables WithLocals(object? locals)
    {
        var copy = new ScenarioVariables(this);
        switch (locals)
        {
            case null:
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var (key, value) in pairs)
                    copy.Set(key, value);
                break;
            default:
                foreach (var property in locals.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length == 0)
                        copy.Set(property.Name, property.GetValue(locals));
                }
                break;
        }
        return copy;
    }

    /// <summary>Creates variables with a fresh customer id and the given brand.</summary>
    public ScenarioVariables(string brand = "testBrand")
        : this(brand, Guid.NewGuid().ToString())
    {
    }

    /// <summary>The built-in brand.</summary>
    public string Brand => GetString("brand");

    /// <summary>The built-in customer id.</summary>
    public string CustomerId => GetString("customerId");

    /// <summary>The scenario's fixed "now".</summary>
    public DateTimeOffset Now => now;

    /// <summary>All explicitly held JSON variables, parent scopes included (built-ins included, <c>now±offset</c> and source variables excluded).</summary>
    public IReadOnlyDictionary<string, JsonNode?> Values =>
        parent is null ? values : parent.Values.Where(kv => !values.ContainsKey(kv.Key)).Concat(values).ToDictionary(StringComparer.Ordinal);

    private IEnumerable<string> AllNames =>
        (parent?.AllNames ?? []).Concat(values.Keys).Concat(sources.Keys).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Sets a variable. The value is stored as JSON; a <see cref="Guid"/> becomes its string form. A
    /// <see cref="JsonSource"/> (e.g. <c>Json.File(...)</c>) is kept as a source and resolved, with tokens, each time it is used.
    /// </summary>
    public ScenarioVariables Set(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (IsMatcher(name))
            throw new ArgumentException($"'{name}' is reserved for matchers.", nameof(name));

        if (value is JsonSource source)
        {
            values.Remove(name);
            sources[name] = source;
            return this;
        }

        sources.Remove(name);

        values[name] = value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            DateTimeOffset dto => JsonValue.Create(FormatTime(dto)),
            DateTime dt => JsonValue.Create(FormatTime(new DateTimeOffset(dt.ToUniversalTime()))),
            _ => JsonSerializer.SerializeToNode(value, value.GetType(), Json.DefaultOptions),
        };
        return this;
    }

    /// <summary>
    /// Tries to get a variable, including <c>now±offset</c>. Tokens inside a stored value are expanded now, at use
    /// time, so <c>.With("walletId", "{{customerId}}:AUD")</c> works whatever order variables are set in.
    /// </summary>
    public bool TryGet(string name, out JsonNode? value)
    {
        if (values.TryGetValue(name, out var stored))
        {
            value = ExpandStored(name, stored);
            return true;
        }

        if (sources.TryGetValue(name, out var source))
        {
            value = Guarded(name, () => source.Resolve(this));
            return true;
        }

        if (parent is not null && parent.TryGet(name, out value))
            return true;

        var match = NowOffsetRegex().Match(name);
        if (match.Success)
        {
            var amount = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["sign"].Value == "-")
                amount = -amount;
            var offset = match.Groups["unit"].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
            value = JsonValue.Create(FormatTime(now + offset));
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>A variable as a string, e.g. <c>ctx.Variables["eventId"]</c>; throws if it is not set.</summary>
    public string this[string name] => GetString(name);

    /// <summary>Every held variable with tokens in its value expanded (for snapshot reverse substitution).</summary>
    public IEnumerable<KeyValuePair<string, JsonNode?>> ExpandedValues() =>
        AllNames.Select(k => new KeyValuePair<string, JsonNode?>(k, TryGet(k, out var v) ? v : null));

    /// <summary>Gets a variable as a string; throws if it is not set.</summary>
    public string GetString(string name)
    {
        if (!TryGet(name, out var node))
            throw UnknownVariable(name);
        return AsText(node);
    }

    /// <summary>Expands tokens in plain text such as a URL path. Matcher tokens are not allowed here.</summary>
    public string ExpandText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("{{", StringComparison.Ordinal))
            return text;

        return TokenRegex().Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            if (IsMatcher(name))
                throw new InvalidOperationException($"Matcher token '{{{{{name}}}}}' can only be used in expected JSON, not in '{text}'.");
            if (!TryGet(name, out var node))
                throw UnknownVariable(name);
            return AsText(node);
        });
    }

    /// <summary>
    /// Expands tokens in JSON text and parses it: unquoted tokens become the variable's JSON, whole
    /// string tokens become the typed value, and tokens inside strings are replaced textually.
    /// Matcher tokens are kept as strings.
    /// </summary>
    public JsonNode? ExpandJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = JsonNode.Parse(ReplaceUnquotedTokens(json));
        return ExpandNode(node);
    }

    /// <summary>Expands string tokens inside an already-parsed node, returning a new node.</summary>
    public JsonNode? ExpandNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, child) in obj)
                    result[ExpandText(key)] = ExpandNode(child);
                return result;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var child in array)
                    list.Add(ExpandNode(child));
                return list;
            default:
                if (node.GetValueKind() != JsonValueKind.String)
                    return node.DeepClone();
                var text = node.GetValue<string>();
                if (!text.Contains("{{", StringComparison.Ordinal))
                    return node.DeepClone();

                var whole = TokenRegex().Match(text);
                if (whole.Success && whole.Length == text.Length)
                {
                    var name = whole.Groups["name"].Value;
                    if (IsMatcher(name))
                        return JsonValue.Create(text);
                    if (!TryGet(name, out var value))
                        throw UnknownVariable(name);
                    return value?.DeepClone();
                }

                return JsonValue.Create(TokenRegex().Replace(text, m =>
                {
                    var name = m.Groups["name"].Value;
                    if (IsMatcher(name))
                        throw new InvalidOperationException($"Matcher token '{m.Value}' must be the whole JSON value, not part of \"{text}\".");
                    if (!TryGet(name, out var value))
                        throw UnknownVariable(name);
                    return AsText(value);
                }));
        }
    }

    /// <summary>True for <c>any</c> and <c>any:kind</c>.</summary>
    public static bool IsMatcher(string tokenName) =>
        tokenName is AnyToken or AbsentToken
        || tokenName.StartsWith(AnyToken + ":", StringComparison.Ordinal)
        || tokenName.StartsWith(ContainsToken, StringComparison.Ordinal);

    /// <summary>The matcher prefix for "a string containing the text", e.g. <c>{{contains:Not enough funds}}</c>.</summary>
    public const string ContainsToken = "contains:";

    /// <summary>Formats a time the way <c>{{now}}</c> does: ISO-8601 UTC with a Z suffix.</summary>
    public static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string AsText(JsonNode? node) =>
        node switch
        {
            null => "null",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            _ => node.ToJsonString(),
        };

    private JsonNode? ExpandStored(string name, JsonNode? stored)
    {
        if (stored is null || !stored.ToJsonString().Contains("{{", StringComparison.Ordinal))
            return stored;
        return Guarded(name, () => ExpandNode(stored));
    }

    private static JsonNode? Guarded(string name, Func<JsonNode?> expand)
    {
        ExpansionDepth.Value++;
        try
        {
            if (ExpansionDepth.Value > MaxExpansionDepth)
                throw new InvalidOperationException($"Scenario variable '{name}' refers to itself through other variables (more than {MaxExpansionDepth} levels deep).");
            return expand();
        }
        finally
        {
            ExpansionDepth.Value--;
        }
    }

    private const int MaxExpansionDepth = 16;
    private static readonly AsyncLocal<int> ExpansionDepth = new();

    private string ReplaceUnquotedTokens(string json)
    {
        if (!json.Contains("{{", StringComparison.Ordinal))
            return json;

        var sb = new StringBuilder(json.Length);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length)
                    sb.Append(json[++i]);
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '{' && i + 1 < json.Length && json[i + 1] == '{')
            {
                var end = json.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    var name = json[(i + 2)..end].Trim();
                    if (IsMatcher(name))
                        sb.Append('"').Append("{{").Append(name).Append("}}").Append('"');
                    else if (TryGet(name, out var value))
                        sb.Append(value?.ToJsonString() ?? "null");
                    else
                        throw UnknownVariable(name);
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private InvalidOperationException UnknownVariable(string name) =>
        new($"Unknown scenario variable '{{{{{name}}}}}'. Known: {string.Join(", ", AllNames.Order())}, now±<n><s|m|h|d>. " +
            "Set it with .With(name, value) or .Capture(name, path).");

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z0-9_.:+\-]+)\s*\}\}")]
    internal static partial Regex TokenRegex();

    [GeneratedRegex(@"^now(?<sign>[+-])(?<n>\d+)(?<unit>[smhd])$")]
    private static partial Regex NowOffsetRegex();
}
