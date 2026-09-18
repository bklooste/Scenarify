using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>How <see cref="JsonMatch"/> compares expected and actual JSON.</summary>
public sealed record JsonMatchOptions
{
    /// <summary>Subset matching (the default): actual may have properties expected does not mention.</summary>
    public static JsonMatchOptions Subset { get; } = new();

    /// <summary>Exact matching: extra properties in actual fail.</summary>
    public static JsonMatchOptions ExactMatch { get; } = new() { Exact = true };

    /// <summary>Fail on properties in actual that expected does not mention.</summary>
    public bool Exact { get; init; }

    /// <summary>Match arrays regardless of order (still the same length).</summary>
    public bool UnorderedArrays { get; init; }

    /// <summary>Paths removed from both sides before comparing (see <see cref="JsonPaths"/>).</summary>
    public IReadOnlyList<string> Ignoring { get; init; } = [];
}

/// <summary>One difference found by <see cref="JsonMatch"/>.</summary>
public sealed record JsonMismatch(string Path, string Expected, string Actual)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}: expected {Expected}, actual {Actual}";
}

/// <summary>Thrown when JSON does not match; the message lists every difference by path.</summary>
public sealed class JsonMatchException(string message) : XunitException(message)
{
}

/// <summary>
/// Compares JSON: subset by default, with matcher values <c>{{any}}</c>, <c>{{any:guid}}</c>,
/// <c>{{any:datetime}}</c>, <c>{{any:number}}</c>, <c>{{any:string}}</c>, <c>{{any:bool}}</c>,
/// <c>{{any:object}}</c>, <c>{{any:array}}</c>, <c>{{contains:text}}</c> (a string containing the text) in the expected side, and <c>{{absent}}</c>: the property is missing
/// or holds its default (<c>null</c>, <c>false</c>, <c>0</c>, <c>""</c>) — for services that omit default values.
/// </summary>
public static class JsonMatch
{
    private const string Missing = "(missing)";
    private const string AbsentMatcher = "absent";

    /// <summary>Asserts that <paramref name="actualJson"/> matches <paramref name="expected"/>.</summary>
    public static void AssertMatches(JsonSource expected, string? actualJson, JsonMatchOptions? options = null, ScenarioVariables? variables = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        AssertMatches(expected.Resolve(variables), ParseActual(actualJson), options, $"expected from {expected.Describe()}");
    }

    /// <summary>Asserts that <paramref name="actual"/> matches <paramref name="expected"/>.</summary>
    public static void AssertMatches(JsonNode? expected, JsonNode? actual, JsonMatchOptions? options = null, string? context = null)
    {
        var mismatches = Compare(expected, actual, options);
        if (mismatches.Count > 0)
            throw new JsonMatchException(FormatFailure(mismatches, actual, context));
    }

    /// <summary>True when <paramref name="actual"/> matches <paramref name="expected"/>.</summary>
    public static bool IsMatch(JsonNode? expected, JsonNode? actual, JsonMatchOptions? options = null) =>
        Compare(expected, actual, options).Count == 0;

    /// <summary>Every difference between expected and actual, by path.</summary>
    public static IReadOnlyList<JsonMismatch> Compare(JsonNode? expected, JsonNode? actual, JsonMatchOptions? options = null)
    {
        options ??= JsonMatchOptions.Subset;
        if (options.Ignoring.Count > 0)
        {
            expected = expected?.DeepClone();
            actual = actual?.DeepClone();
            foreach (var path in options.Ignoring)
            {
                JsonPaths.Remove(expected, path);
                JsonPaths.Remove(actual, path);
            }
        }

        var mismatches = new List<JsonMismatch>();
        CompareNode(expected, actual, present: true, "$", options, mismatches);
        return mismatches;
    }

    /// <summary>Formats a failure: the differences, then the full actual JSON.</summary>
    public static string FormatFailure(IReadOnlyList<JsonMismatch> mismatches, JsonNode? actual, string? context = null)
    {
        var sb = new StringBuilder();
        sb.Append("JSON did not match");
        if (context is not null)
            sb.Append(" (").Append(context).Append(')');
        sb.Append(CultureInfo.InvariantCulture, $": {mismatches.Count} difference{(mismatches.Count == 1 ? "" : "s")}").AppendLine();
        foreach (var mismatch in mismatches.Take(50))
            sb.Append("  ").AppendLine(mismatch.ToString());
        if (mismatches.Count > 50)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {mismatches.Count - 50} more");
        sb.AppendLine("Actual:");
        sb.Append(Pretty(actual));
        return sb.ToString();
    }

    /// <summary>Indented JSON for failure output.</summary>
    public static string Pretty(JsonNode? node) =>
        node?.ToJsonString(PrettyOptions) ?? "null";

    /// <summary>Parses an actual body: null for empty, a <see cref="JsonMatchException"/> showing the text when it is not JSON.</summary>
    public static JsonNode? ParseActual(string? actualJson)
    {
        if (string.IsNullOrWhiteSpace(actualJson))
            return null;
        try
        {
            return JsonNode.Parse(actualJson);
        }
        catch (JsonException ex)
        {
            throw new JsonMatchException($"Actual body is not JSON ({ex.Message}):{Environment.NewLine}{actualJson}");
        }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void CompareNode(JsonNode? expected, JsonNode? actual, bool present, string path, JsonMatchOptions options, List<JsonMismatch> mismatches)
    {
        if (TryGetMatcher(expected, out var matcher))
        {
            if (!present)
            {
                if (matcher != AbsentMatcher)
                    mismatches.Add(new(path, Show(expected), Missing));
            }
            else if (!MatcherAccepts(matcher, actual))
                mismatches.Add(new(path, $"{{{{{matcher}}}}}", Show(actual)));
            return;
        }

        if (!present)
        {
            mismatches.Add(new(path, Show(expected), Missing));
            return;
        }

        switch (expected)
        {
            case null:
                if (actual is not null)
                    mismatches.Add(new(path, "null", Show(actual)));
                return;

            case JsonObject eo:
                if (actual is not JsonObject ao)
                {
                    mismatches.Add(new(path, "an object", Show(actual)));
                    return;
                }
                foreach (var (key, value) in eo)
                {
                    var has = ao.TryGetPropertyValue(key, out var actualValue);
                    CompareNode(value, actualValue, has, JsonPaths.Append(path, key), options, mismatches);
                }
                if (options.Exact)
                {
                    foreach (var (key, value) in ao)
                    {
                        if (!eo.ContainsKey(key))
                            mismatches.Add(new(JsonPaths.Append(path, key), Missing, Show(value)));
                    }
                }
                return;

            case JsonArray ea:
                if (actual is not JsonArray aa)
                {
                    mismatches.Add(new(path, "an array", Show(actual)));
                    return;
                }
                if (options.UnorderedArrays)
                    CompareUnordered(ea, aa, path, options, mismatches);
                else
                    CompareOrdered(ea, aa, path, options, mismatches);
                return;

            default:
                if (!ValuesEqual((JsonValue)expected, actual))
                    mismatches.Add(new(path, Show(expected), Show(actual)));
                return;
        }
    }

    private static void CompareOrdered(JsonArray expected, JsonArray actual, string path, JsonMatchOptions options, List<JsonMismatch> mismatches)
    {
        if (expected.Count != actual.Count)
            mismatches.Add(new($"{path}.length", expected.Count.ToString(CultureInfo.InvariantCulture), actual.Count.ToString(CultureInfo.InvariantCulture)));

        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            CompareNode(expected[i], actual[i], present: true, JsonPaths.Append(path, i), options, mismatches);
    }

    private static void CompareUnordered(JsonArray expected, JsonArray actual, string path, JsonMatchOptions options, List<JsonMismatch> mismatches)
    {
        if (expected.Count != actual.Count)
            mismatches.Add(new($"{path}.length", expected.Count.ToString(CultureInfo.InvariantCulture), actual.Count.ToString(CultureInfo.InvariantCulture)));

        var used = new bool[actual.Count];
        for (var i = 0; i < expected.Count; i++)
        {
            var matched = false;
            for (var j = 0; j < actual.Count && !matched; j++)
            {
                if (used[j])
                    continue;
                var probe = new List<JsonMismatch>();
                CompareNode(expected[i], actual[j], present: true, path, options, probe);
                if (probe.Count == 0)
                    used[j] = matched = true;
            }

            if (!matched)
                mismatches.Add(new(JsonPaths.Append(path, i), $"{Show(expected[i])} somewhere in the array", "no unmatched element equal to it"));
        }
    }

    private static bool TryGetMatcher(JsonNode? expected, out string matcher)
    {
        matcher = "";
        if (expected is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            return false;
        var text = v.GetValue<string>();
        if (!text.StartsWith("{{", StringComparison.Ordinal) || !text.EndsWith("}}", StringComparison.Ordinal))
            return false;
        var name = text[2..^2].Trim();
        if (!ScenarioVariables.IsMatcher(name))
            return false;
        matcher = name;
        return true;
    }

    private static bool MatcherAccepts(string matcher, JsonNode? actual)
    {
        if (matcher.StartsWith(ScenarioVariables.ContainsToken, StringComparison.Ordinal))
        {
            return actual is JsonValue v && v.GetValueKind() == JsonValueKind.String
                && v.GetValue<string>().Contains(matcher[ScenarioVariables.ContainsToken.Length..], StringComparison.Ordinal);
        }

        var kind = actual?.GetValueKind() ?? JsonValueKind.Null;
        return matcher switch
        {
            "any" => true,
            AbsentMatcher => kind is JsonValueKind.Null or JsonValueKind.False
                || (kind == JsonValueKind.Number && decimal.TryParse(actual!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n == 0)
                || (kind == JsonValueKind.String && actual!.GetValue<string>().Length == 0),
            "any:string" => kind == JsonValueKind.String,
            "any:number" => kind == JsonValueKind.Number,
            "any:bool" => kind is JsonValueKind.True or JsonValueKind.False,
            "any:object" => kind == JsonValueKind.Object,
            "any:array" => kind == JsonValueKind.Array,
            "any:guid" => kind == JsonValueKind.String && Guid.TryParse(actual!.GetValue<string>(), out _),
            "any:datetime" => kind == JsonValueKind.String && LooksLikeDateTime(actual!.GetValue<string>()),
            _ => throw new InvalidOperationException(
                $"Unknown matcher '{{{{{matcher}}}}}'. Use absent, any, any:string, any:number, any:bool, any:object, any:array, any:guid or any:datetime."),
        };
    }

    internal static bool LooksLikeDateTime(string text) =>
        text.Length >= 10 && char.IsAsciiDigit(text[0]) && text.Contains('-')
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static bool ValuesEqual(JsonValue expected, JsonNode? actual)
    {
        if (actual is not JsonValue av)
            return false;

        var ek = expected.GetValueKind();
        var ak = av.GetValueKind();
        if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
        {
            var et = expected.ToJsonString();
            var at = av.ToJsonString();
            if (decimal.TryParse(et, NumberStyles.Float, CultureInfo.InvariantCulture, out var ed)
                && decimal.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var ad))
                return ed == ad;
            return double.Parse(et, CultureInfo.InvariantCulture) == double.Parse(at, CultureInfo.InvariantCulture);
        }

        if (ek != ak)
            return false;

        return ek switch
        {
            JsonValueKind.String => StringsEqual(expected.GetValue<string>(), av.GetValue<string>()),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => expected.ToJsonString() == av.ToJsonString(),
        };
    }

    // Timestamps are equal when they are the same instant, whatever the fractional-second digits or offset.
    private static bool StringsEqual(string expected, string actual) =>
        expected == actual
        || (expected.Contains('T') && actual.Contains('T') && LooksLikeDateTime(expected) && LooksLikeDateTime(actual)
            && DateTimeOffset.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e)
            && DateTimeOffset.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a)
            && e == a);

    private static string Show(JsonNode? node)
    {
        if (node is null)
            return "null";
        var text = node.ToJsonString(CompactOptions);
        return text.Length > 120 ? text[..117] + "..." : text;
    }

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
