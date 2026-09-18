using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scenarify;

/// <summary>
/// A piece of JSON test data: a file under the test output directory, an inline literal, or a typed
/// object. Anywhere a body, a MockServer expectation or an expected result is accepted, a
/// <see cref="JsonSource"/> is accepted.
/// </summary>
/// <remarks>
/// A plain <see cref="string"/> converts implicitly: text starting with <c>{</c>, <c>[</c> or <c>"</c> (after
/// whitespace) is inline JSON, anything else is a file path. That lets an <c>[InlineData]</c> row carry
/// either. Use <see cref="Json.File"/> / <see cref="Json.Inline"/> to be explicit.
/// </remarks>
public abstract class JsonSource
{
    private protected JsonSource() { }

    /// <summary>The CLR type of a typed source (<see cref="Json.From{T}"/>), used as the default stream message type.</summary>
    public virtual Type? ClrType => null;

    /// <summary>A short description for failure messages: the file path, or "inline".</summary>
    public abstract string Describe();

    /// <summary>The raw JSON text before token expansion.</summary>
    public abstract string ReadText();

    /// <summary>Reads the JSON, expands <c>{{tokens}}</c> from <paramref name="variables"/> and parses it.</summary>
    public virtual JsonNode? Resolve(ScenarioVariables? variables = null)
    {
        var text = ReadText();
        try
        {
            return variables is null
                ? JsonNode.Parse(text)
                : variables.ExpandJson(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Test JSON from {Describe()} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Reads, expands and serializes back to compact JSON text.</summary>
    public string ResolveText(ScenarioVariables? variables = null) =>
        Resolve(variables)?.ToJsonString() ?? "null";

    /// <summary>
    /// This JSON with <paramref name="patch"/> merged over it (RFC 7396 merge patch, applied after token expansion):
    /// objects merge, <c>null</c> removes a property, anything else replaces. An empty object in the patch replaces
    /// rather than being a silent no-op.
    /// </summary>
    public JsonSource Patch(JsonSource patch) => new PatchedSource(this, patch);

    /// <summary>Converts a string to a file source or an inline source; see the type remarks.</summary>
    public static implicit operator JsonSource(string fileOrInline) => Json.Parse(fileOrInline);

    /// <inheritdoc />
    public override string ToString() => Describe();

    internal sealed class FileSource(string path) : JsonSource
    {
        public string Path { get; } = path;

        public override string Describe() => Path;

        public override string ReadText()
        {
            var full = System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(AppContext.BaseDirectory, Path);
            if (!File.Exists(full))
                throw new FileNotFoundException(
                    $"Test data file '{Path}' not found at {full}. Is it copied to the output directory " +
                    "(<None Update=\"cases/**\" CopyToOutputDirectory=\"PreserveNewest\" />)?", full);
            return File.ReadAllText(full);
        }
    }

    internal sealed class InlineSource(string json) : JsonSource
    {
        public override string Describe() => "inline JSON";

        public override string ReadText() => json;
    }

    internal sealed class PatchedSource(JsonSource target, JsonSource patch) : JsonSource
    {
        public override Type? ClrType => target.ClrType;

        public override string Describe() => $"{target.Describe()} patched with {patch.Describe()}";

        public override string ReadText() => Resolve()?.ToJsonString() ?? "null";

        public override JsonNode? Resolve(ScenarioVariables? variables = null) =>
            MergePatch(target.Resolve(variables), patch.Resolve(variables), isRoot: true);

        // An empty object replaces a nested value, but an empty patch at the root changes nothing.
        private static JsonNode? MergePatch(JsonNode? target, JsonNode? patch, bool isRoot = false)
        {
            if (patch is JsonObject { Count: 0 } && isRoot)
                return target;
            if (patch is not JsonObject patchObject || patchObject.Count == 0)
                return patch?.DeepClone();

            var result = target as JsonObject ?? [];
            foreach (var (key, value) in patchObject)
            {
                if (value is null)
                    result.Remove(key);
                else
                    result[key] = MergePatch(result.TryGetPropertyValue(key, out var existing) ? existing?.DeepClone() : null, value);
            }
            return result;
        }
    }

    internal sealed class FormattedSource(JsonSource template, object values) : JsonSource
    {
        public override Type? ClrType => template.ClrType;

        public override string Describe() => $"{template.Describe()} formatted";

        public override string ReadText() => Resolve()?.ToJsonString() ?? "null";

        public override JsonNode? Resolve(ScenarioVariables? variables = null) =>
            template.Resolve((variables ?? new ScenarioVariables()).WithLocals(values));
    }

    internal sealed class ArraySource(JsonSource[] items) : JsonSource
    {
        public override string Describe() => $"array of {items.Length}";

        public override string ReadText() => Resolve()?.ToJsonString() ?? "[]";

        public override JsonNode? Resolve(ScenarioVariables? variables = null) =>
            new JsonArray(items.Select(i => i.Resolve(variables)).ToArray());
    }

    internal sealed class ObjectSource(object? value, Type type, JsonSerializerOptions options) : JsonSource
    {
        public override Type? ClrType => type;

        public override string Describe() => $"{type.Name} object";

        public override string ReadText() => JsonSerializer.Serialize(value, type, options);
    }
}

/// <summary>Factory methods for <see cref="JsonSource"/>.</summary>
public static class Json
{
    /// <summary>
    /// Serializer options for typed sources: web defaults, enums as strings, defaults omitted. Anonymous
    /// objects serialize correctly (unlike options with <c>IgnoreReadOnlyProperties</c>).
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    /// <summary>A file relative to the test output directory (<see cref="AppContext.BaseDirectory"/>).</summary>
    public static JsonSource File(string path) => new JsonSource.FileSource(path);

    /// <summary>An inline JSON literal. <c>{{tokens}}</c> may appear quoted or unquoted.</summary>
    public static JsonSource Inline(string json) => new JsonSource.InlineSource(json);

    /// <summary>A typed object, serialized with <paramref name="options"/> or <see cref="DefaultOptions"/>.</summary>
    public static JsonSource From<T>(T value, JsonSerializerOptions? options = null) =>
        new JsonSource.ObjectSource(value, value?.GetType() ?? typeof(T), options ?? DefaultOptions);

    /// <summary>
    /// A JSON template whose <c>{{tokens}}</c> are filled from <paramref name="values"/> first (an anonymous object's
    /// property names are the token names), then from the scenario's variables. Use it instead of <c>$$$"""</c> strings
    /// when a body mixes C# values with scenario tokens:
    /// <code>Json.Format("""{ "eventId": "{{eventId}}", "selectionId": "{{n}}", "status": "{{status}}" }""", new { n, status })</code>
    /// </summary>
    /// <remarks>A value used as a whole string (<c>"{{n}}"</c>) keeps its JSON type, so <c>n = 3</c> becomes the number 3.</remarks>
    public static JsonSource Format(JsonSource template, object values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return new JsonSource.FormattedSource(template, values);
    }

    /// <summary>
    /// The token text <c>{{name}}</c>, for the rare case where a token's name is built in C#
    /// (<c>Json.Token($"job{n}")</c> gives <c>{{job3}}</c>) — no brace escaping needed.
    /// </summary>
    public static string Token(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "{{" + name + "}}";
    }

    /// <summary>The <c>{{absent}}</c> matcher, for expected values built in C# (<c>Json.Format(..., new { amount = Json.Absent })</c>).</summary>
    public const string Absent = "{{absent}}";

    /// <summary>The <c>{{any}}</c> / <c>{{any:kind}}</c> matcher (kind: guid, datetime, number, string, bool, object, array).</summary>
    public static string Any(string? kind = null) => kind is null ? "{{any}}" : "{{any:" + kind + "}}";

    /// <summary>The <c>{{contains:text}}</c> matcher: a string containing <paramref name="text"/>.</summary>
    public static string Contains(string text) => "{{" + ScenarioVariables.ContainsToken + text + "}}";

    /// <summary>A JSON array of the given sources, each resolved with the scenario's variables.</summary>
    public static JsonSource Array(params JsonSource[] items) => new JsonSource.ArraySource(items);

    /// <summary>Resolves <paramref name="source"/> (no tokens) and deserializes it with <see cref="DefaultOptions"/>.</summary>
    public static T? Parse<T>(JsonSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var node = source.Resolve();
        return node is null ? default : node.Deserialize<T>(DefaultOptions);
    }

    /// <summary><paramref name="target"/> with <paramref name="patch"/> merged over it; see <see cref="JsonSource.Patch"/>.</summary>
    public static JsonSource Patch(JsonSource target, JsonSource patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Patch(patch);
    }

    /// <summary>
    /// Inline when the text is JSON-shaped — starts with <c>{</c>, <c>[</c> or <c>"</c>, or is a bare number,
    /// <c>true</c>, <c>false</c> or <c>null</c> — otherwise a file path.
    /// </summary>
    public static JsonSource Parse(string fileOrInline)
    {
        ArgumentNullException.ThrowIfNull(fileOrInline);
        var trimmed = fileOrInline.Trim();
        var inline = trimmed.Length > 0 && (trimmed[0] is '{' or '[' or '"'
            || trimmed is "true" or "false" or "null"
            || double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        return inline ? Inline(fileOrInline) : File(fileOrInline);
    }

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
