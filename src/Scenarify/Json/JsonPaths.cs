using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Scenarify;

/// <summary>
/// The small path syntax shared by <c>Capture</c>, <c>Only</c> and <c>Ignoring</c>:
/// <c>a.b</c>, <c>list[*].x</c>, <c>list[0].x</c>, optionally prefixed with <c>$</c> or <c>$.</c>.
/// </summary>
public static class JsonPaths
{
    /// <summary>A sentinel written where a projected array element had nothing selected.</summary>
    internal const string AnyPlaceholder = "{{any}}";

    /// <summary>All nodes the path selects, with their concrete paths (<c>$.list[2].x</c>).</summary>
    public static IEnumerable<(string Path, JsonNode? Node)> Select(JsonNode? root, string path) =>
        Select(root, Parse(path), 0, "$");

    /// <summary>The single node the path selects; throws when it selects none or several.</summary>
    public static JsonNode? SelectSingle(JsonNode? root, string path)
    {
        var found = Select(root, path).Take(2).ToList();
        return found.Count switch
        {
            1 => found[0].Node,
            0 => throw new InvalidOperationException($"Path '{path}' selected nothing in {Describe(root)}"),
            _ => throw new InvalidOperationException($"Path '{path}' selected more than one value in {Describe(root)}"),
        };
    }

    /// <summary>Removes every property the path selects (in place). Array indices cannot be removed.</summary>
    public static void Remove(JsonNode? root, string path)
    {
        var segments = Parse(path);
        if (segments.Count == 0)
            throw new ArgumentException("Cannot remove the root.", nameof(path));
        if (segments[^1] is not PropertySegment last)
            throw new ArgumentException($"Path '{path}' must end in a property name to be ignored.", nameof(path));

        foreach (var (_, parent) in Select(root, segments.Take(segments.Count - 1).ToList(), 0, "$"))
        {
            if (parent is JsonObject obj)
                obj.Remove(last.Name);
        }
    }

    /// <summary>
    /// A copy of <paramref name="root"/> holding only what <paramref name="paths"/> select, with
    /// the enclosing object/array structure kept. Array elements that nothing selects become
    /// <c>{{any}}</c> so ordered array matching still lines up.
    /// </summary>
    public static JsonNode? Project(JsonNode? root, IEnumerable<string> paths)
    {
        JsonNode? result = null;
        var any = false;
        foreach (var path in paths)
        {
            any = true;
            var projected = Project(root, Parse(path), 0);
            if (projected.Found)
                result = Merge(result, projected.Node);
        }

        if (!any)
            throw new ArgumentException("At least one path is required.", nameof(paths));
        return result ?? new JsonObject();
    }

    /// <summary>Writes a concrete path segment list as text.</summary>
    internal static string Append(string parent, string property) =>
        IsSimpleName(property) ? $"{parent}.{property}" : $"{parent}['{property}']";

    internal static string Append(string parent, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{parent}[{index}]");

    private static bool IsSimpleName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '-');

    private static string Describe(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    private static IEnumerable<(string, JsonNode?)> Select(JsonNode? node, IReadOnlyList<Segment> segments, int index, string at)
    {
        if (index == segments.Count)
        {
            yield return (at, node);
            yield break;
        }

        switch (segments[index])
        {
            case PropertySegment p when node is JsonObject obj && obj.TryGetPropertyValue(p.Name, out var child):
                foreach (var hit in Select(child, segments, index + 1, Append(at, p.Name)))
                    yield return hit;
                break;
            case IndexSegment i when node is JsonArray arr && i.Index < arr.Count:
                foreach (var hit in Select(arr[i.Index], segments, index + 1, Append(at, i.Index)))
                    yield return hit;
                break;
            case WildcardSegment when node is JsonArray arr:
                for (var n = 0; n < arr.Count; n++)
                {
                    foreach (var hit in Select(arr[n], segments, index + 1, Append(at, n)))
                        yield return hit;
                }
                break;
            case WildcardSegment when node is JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    foreach (var hit in Select(child, segments, index + 1, Append(at, key)))
                        yield return hit;
                }
                break;
        }
    }

    private static (bool Found, JsonNode? Node) Project(JsonNode? node, IReadOnlyList<Segment> segments, int index)
    {
        if (index == segments.Count)
            return (true, node?.DeepClone());

        switch (segments[index])
        {
            case PropertySegment p when node is JsonObject obj && obj.TryGetPropertyValue(p.Name, out var child):
                var inner = Project(child, segments, index + 1);
                return inner.Found ? (true, new JsonObject { [p.Name] = inner.Node }) : (false, null);

            case IndexSegment i when node is JsonArray arr && i.Index < arr.Count:
                var element = Project(arr[i.Index], segments, index + 1);
                if (!element.Found)
                    return (false, null);
                var list = new JsonArray();
                for (var n = 0; n < arr.Count; n++)
                    list.Add(n == i.Index ? element.Node : JsonValue.Create(AnyPlaceholder));
                return (true, list);

            case WildcardSegment when node is JsonArray arr:
                var all = new JsonArray();
                foreach (var item in arr)
                {
                    var projected = Project(item, segments, index + 1);
                    all.Add(projected.Found ? projected.Node : JsonValue.Create(AnyPlaceholder));
                }
                return (true, all);

            case WildcardSegment when node is JsonObject obj:
                var props = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    var projected = Project(child, segments, index + 1);
                    if (projected.Found)
                        props[key] = projected.Node;
                }
                return (true, props);

            default:
                return (false, null);
        }
    }

    private static JsonNode? Merge(JsonNode? left, JsonNode? right)
    {
        if (left is null)
            return right;
        if (IsAnyPlaceholder(left))
            return right;
        if (IsAnyPlaceholder(right))
            return left;

        if (left is JsonObject lo && right is JsonObject ro)
        {
            foreach (var (key, value) in ro.ToList())
            {
                ro.Remove(key);
                lo[key] = lo.TryGetPropertyValue(key, out var existing) ? Merge(existing?.DeepClone(), value) : value;
            }
            return lo;
        }

        if (left is JsonArray la && right is JsonArray ra && la.Count == ra.Count)
        {
            var merged = new JsonArray();
            for (var n = 0; n < la.Count; n++)
                merged.Add(Merge(la[n]?.DeepClone(), ra[n]?.DeepClone()));
            return merged;
        }

        return right;
    }

    private static bool IsAnyPlaceholder(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s == AnyPlaceholder;

    internal static IReadOnlyList<Segment> Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<Segment>();
        var i = 0;
        if (path.StartsWith('$'))
            i = 1;

        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                    throw new FormatException($"Unclosed '[' in path '{path}'.");
                var inside = path[(i + 1)..close].Trim();
                if (inside == "*")
                    segments.Add(new WildcardSegment());
                else if (inside.Length >= 2 && inside[0] is '\'' or '"' && inside[^1] == inside[0])
                    segments.Add(new PropertySegment(inside[1..^1]));
                else if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    segments.Add(new IndexSegment(index));
                else
                    throw new FormatException($"Unsupported index '[{inside}]' in path '{path}'. Use [n], [*] or ['name'].");
                i = close + 1;
                continue;
            }

            var sb = new StringBuilder();
            while (i < path.Length && path[i] is not '.' and not '[')
                sb.Append(path[i++]);
            var name = sb.ToString();
            segments.Add(name == "*" ? new WildcardSegment() : new PropertySegment(name));
        }

        return segments;
    }

    internal abstract record Segment;

    internal sealed record PropertySegment(string Name) : Segment;

    internal sealed record IndexSegment(int Index) : Segment;

    internal sealed record WildcardSegment : Segment;
}
