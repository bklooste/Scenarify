using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;
using Xunit.v3;

namespace Scenarify;

/// <summary>
/// JSON snapshots: an expected-JSON file that was generated from a real response and reviewed,
/// rather than typed. Stored at <c>snapshots/&lt;TestClass&gt;/&lt;test&gt;[.&lt;case&gt;].snapshot.json</c>.
/// </summary>
/// <remarks>
/// <para>File shape: <c>{ "$only": [paths] | "$ignoring": [paths], "value": ... }</c>. Values the scenario
/// knows (built-ins, <c>.With</c>, <c>.Capture</c>) are stored as <c>{{tokens}}</c>; unknown GUIDs and
/// timestamps as <c>{{any:guid}}</c> / <c>{{any:datetime}}</c>.</para>
/// <para>Nothing is auto-accepted. A missing or different snapshot fails the test and writes
/// <c>&lt;name&gt;.received.json</c> under <see cref="ReceivedDirectory"/> next to the expected file; move it over
/// the expected file (dropping the <c>.received</c> suffix) once you have reviewed it. See the README
/// ("Accepting a snapshot change") for a one-line script that does this for a whole test run.</para>
/// </remarks>
public static class JsonSnapshot
{
    /// <summary>Property name holding the recorded value.</summary>
    public const string ValueKey = "value";

    /// <summary>Property name holding the paths compared.</summary>
    public const string OnlyKey = "$only";

    /// <summary>Property name holding the paths ignored.</summary>
    public const string IgnoringKey = "$ignoring";

    /// <summary>Strings shorter than this are replaced by a token only when the path names the variable.</summary>
    public const int ShortValueLength = 8;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where snapshots are read from: <c>SNAPSHOT_DIR</c>, else <c>snapshots/</c> in the test output directory.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("SNAPSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "snapshots");

    /// <summary>
    /// Where received files are written: <c>SNAPSHOT_RECEIVED_DIR</c>, else <c>results/snapshots/</c> in the
    /// test output directory (<c>/app/results/snapshots</c> in a test container).
    /// </summary>
    public static string ReceivedDirectory =>
        Environment.GetEnvironmentVariable("SNAPSHOT_RECEIVED_DIR") ?? Path.Combine(AppContext.BaseDirectory, "results", "snapshots");

    /// <summary>
    /// The snapshot name for the running test: <c>&lt;TestClass&gt;/&lt;method&gt;[.&lt;caseName&gt;]</c>.
    /// A test method with parameters must supply <paramref name="caseName"/>, since its display name holds data values.
    /// </summary>
    public static string NameForCurrentTest(string? caseName = null)
    {
        var context = TestContext.Current;
        var className = context.TestClass?.TestClassName
            ?? throw new InvalidOperationException("Snapshots need a running test to name them; pass a name explicitly.");
        var method = context.TestMethod?.MethodName
            ?? throw new InvalidOperationException("Snapshots need a running test method to name them; pass a name explicitly.");

        var shortClass = className[(className.LastIndexOfAny(['.', '+']) + 1)..];
        var hasParameters = context.TestMethod is IXunitTestMethod xm && xm.Method.GetParameters().Length > 0;
        if (caseName is null && hasParameters)
            throw new InvalidOperationException(
                $"{shortClass}.{method} has parameters, so its snapshot needs a case name that does not depend on data values. " +
                "Add a 'name' parameter and call .Snapshot(name), or use a case record with a Name.");

        return caseName is null ? $"{shortClass}/{method}" : $"{shortClass}/{method}.{Sanitize(caseName)}";
    }

    /// <summary>
    /// Asserts that <paramref name="actualJson"/> matches the stored snapshot <paramref name="name"/>.
    /// <paramref name="only"/>/<paramref name="ignoring"/> are for recording a new snapshot; once accepted, the
    /// file holds them and the code should drop them.
    /// </summary>
    public static void AssertMatches(string? actualJson, string? name = null, ScenarioVariables? variables = null,
        IReadOnlyList<string>? only = null, IReadOnlyList<string>? ignoring = null) =>
        AssertMatches(JsonMatch.ParseActual(actualJson), name, variables, only, ignoring);

    /// <inheritdoc cref="AssertMatches(string?, string?, ScenarioVariables?, IReadOnlyList{string}?, IReadOnlyList{string}?)"/>
    public static void AssertMatches(JsonNode? actual, string? name = null, ScenarioVariables? variables = null,
        IReadOnlyList<string>? only = null, IReadOnlyList<string>? ignoring = null)
    {
        name ??= NameForCurrentTest();
        if (only is { Count: > 0 } && ignoring is { Count: > 0 })
            throw new ArgumentException("Use Only or Ignoring, not both.");

        var file = Path.Combine(Directory, name + ".snapshot.json");
        if (!File.Exists(file))
        {
            var received = WriteReceived(name, actual, variables, only, ignoring);
            throw new JsonMatchException(
                $"No snapshot '{name}' at {file}.{Environment.NewLine}Wrote {received}{Environment.NewLine}" +
                $"Review it, then move it over the expected file to accept (see README: \"Accepting a snapshot change\")."
                + $"{Environment.NewLine}Received:{Environment.NewLine}{File.ReadAllText(received)}");
        }

        var stored = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
            ?? throw new InvalidOperationException($"Snapshot {file} is not a JSON object.");
        var storedOnly = ReadPaths(stored, OnlyKey);
        var storedIgnoring = ReadPaths(stored, IgnoringKey);
        if (storedOnly is not null && storedIgnoring is not null)
            throw new InvalidOperationException($"Snapshot {file} has both {OnlyKey} and {IgnoringKey}; keep one.");

        CheckCodeAgrees(file, OnlyKey, only, storedOnly);
        CheckCodeAgrees(file, IgnoringKey, ignoring, storedIgnoring);

        var selected = Select(actual, storedOnly);
        var expected = variables is null ? stored[ValueKey]?.DeepClone() : variables.ExpandNode(stored[ValueKey]);
        var options = new JsonMatchOptions { Exact = true, Ignoring = storedIgnoring ?? [] };
        var mismatches = JsonMatch.Compare(expected, selected, options);
        if (mismatches.Count == 0)
            return;

        var receivedFile = WriteReceived(name, actual, variables, storedOnly, storedIgnoring);
        throw new JsonMatchException(
            JsonMatch.FormatFailure(mismatches, selected, $"snapshot '{name}'") +
            $"{Environment.NewLine}Wrote {receivedFile}; if the change is intended, move it over the expected file to accept"
                + " (see README: \"Accepting a snapshot change\").");
    }

    /// <summary>
    /// The value a snapshot stores for <paramref name="actual"/>: known variable values replaced by
    /// their tokens, unknown GUIDs and timestamps by matchers.
    /// </summary>
    public static JsonNode? ToSnapshotValue(JsonNode? actual, ScenarioVariables? variables)
    {
        var candidates = variables?.ExpandedValues()
            .Where(kv => kv.Value is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
            .Select(kv => (Name: kv.Key, Value: (JsonValue)kv.Value!))
            .ToList() ?? [];
        return Reverse(actual?.DeepClone(), "$", null, candidates);
    }

    private static JsonNode? Reverse(JsonNode? node, string path, string? propertyName, List<(string Name, JsonValue Value)> candidates)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    obj[key] = Reverse(obj[key]?.DeepClone(), JsonPaths.Append(path, key), key, candidates);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    arr[i] = Reverse(arr[i]?.DeepClone(), JsonPaths.Append(path, i), propertyName, candidates);
                return arr;
            case JsonValue value:
                var kind = value.GetValueKind();
                if (kind is not (JsonValueKind.String or JsonValueKind.Number))
                    return value;

                var matches = candidates.Where(c => SameValue(c.Value, value)).ToList();
                var strong = matches.Where(c => IsStrong(c.Value) || NameHints(propertyName, c.Name)).ToList();
                if (strong.Count > 1)
                {
                    var hinted = strong.Where(c => NameHints(propertyName, c.Name)).ToList();
                    if (hinted.Count != 1)
                        throw new InvalidOperationException(
                            $"Snapshot value at {path} equals variables {string.Join(" and ", strong.Select(c => $"'{c.Name}'"))}; " +
                            "give them different values so the snapshot records which one it is.");
                    strong = hinted;
                }

                if (strong.Count == 1)
                    return JsonValue.Create($"{{{{{strong[0].Name}}}}}");

                if (kind == JsonValueKind.String)
                {
                    var text = value.GetValue<string>();
                    if (Guid.TryParse(text, out _))
                        return JsonValue.Create("{{any:guid}}");
                    if (JsonMatch.LooksLikeDateTime(text))
                        return JsonValue.Create("{{any:datetime}}");
                }
                return value;
            default:
                return node;
        }
    }

    private static bool SameValue(JsonValue a, JsonValue b)
    {
        var ak = a.GetValueKind();
        if (ak != b.GetValueKind())
            return false;
        if (ak == JsonValueKind.String)
            return a.GetValue<string>() == b.GetValue<string>();
        return decimal.TryParse(a.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && x == y;
    }

    private static bool IsStrong(JsonValue value) =>
        value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length >= ShortValueLength;

    private static bool NameHints(string? propertyName, string variableName) =>
        propertyName is not null
        && (propertyName.Contains(variableName, StringComparison.OrdinalIgnoreCase)
            || variableName.Contains(propertyName, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Select(JsonNode? actual, IReadOnlyList<string>? only) =>
        only is { Count: > 0 } ? JsonPaths.Project(actual, only) : actual?.DeepClone();

    private static string WriteReceived(string name, JsonNode? actual, ScenarioVariables? variables, IReadOnlyList<string>? only, IReadOnlyList<string>? ignoring)
    {
        var selected = Select(actual, only);
        if (ignoring is { Count: > 0 })
        {
            foreach (var path in ignoring)
                JsonPaths.Remove(selected, path);
        }

        var document = new JsonObject();
        if (only is { Count: > 0 })
            document[OnlyKey] = new JsonArray(only.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        if (ignoring is { Count: > 0 })
            document[IgnoringKey] = new JsonArray(ignoring.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        document[ValueKey] = ToSnapshotValue(selected, variables);

        var file = Path.Combine(ReceivedDirectory, name + ".received.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, document.ToJsonString(WriteOptions) + Environment.NewLine, new UTF8Encoding(false));
        return file;
    }

    private static List<string>? ReadPaths(JsonObject stored, string key) =>
        stored[key] is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToList() : null;

    private static void CheckCodeAgrees(string file, string key, IReadOnlyList<string>? inCode, List<string>? inFile)
    {
        if (inCode is not { Count: > 0 })
            return;
        if (inFile is not null && inCode.SequenceEqual(inFile))
            return;
        throw new InvalidOperationException(
            $"Snapshot paths in code ({key}: {string.Join(", ", inCode)}) differ from {file} " +
            $"({(inFile is null ? "none" : string.Join(", ", inFile))}). The file is the source of truth once accepted: " +
            "remove the paths from code, or edit the file and rerun to re-record.");
    }

    private static string Sanitize(string caseName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(caseName.Length);
        foreach (var c in caseName)
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) || c == '/' ? '-' : c);
        return sb.ToString();
    }
}
