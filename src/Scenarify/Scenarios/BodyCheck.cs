using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>
/// The checks a step applies to a JSON body: any number of <c>Matches</c>, <c>MatchesExactly</c>, <c>Contains</c> and
/// <c>Satisfies</c>, plus at most one <c>MatchesSnapshot</c>. Shared by HTTP, stream and Redis steps.
/// </summary>
/// <remarks><c>Ignoring</c> and <c>UnorderedArrays</c> apply to every JSON comparison on the step.</remarks>
public sealed class BodyCheck
{
    private readonly List<Action<JsonNode?, ScenarioContext, string>> checks = [];
    private readonly List<string> only = [];
    private readonly List<string> ignoring = [];
    private bool unordered;
    private bool snapshot;
    private string? snapshotName;

    /// <summary>True when at least one check is configured.</summary>
    public bool IsSet => checks.Count > 0 || snapshot;

    /// <summary>True when the step has a snapshot check (which needs a name resolved once per run).</summary>
    public bool IsSnapshot => snapshot;

    /// <summary>The options JSON comparisons on this step use (subset unless a check says otherwise).</summary>
    public JsonMatchOptions MatchOptions => new() { UnorderedArrays = unordered, Ignoring = ignoring };

    /// <summary>Subset match against <paramref name="json"/>.</summary>
    public void Matches(JsonSource json) => AddMatch(json, exact: false);

    /// <summary>Exact match against <paramref name="json"/>.</summary>
    public void MatchesExactly(JsonSource json) => AddMatch(json, exact: true);

    /// <summary>
    /// Some element subset-matches <paramref name="element"/>. Elements are the body's items when it is an array, or,
    /// with <paramref name="within"/>, the items of every array (and every non-array node) that path selects —
    /// e.g. <c>markets[*].selections</c> searches all selections of all markets.
    /// </summary>
    public void Contains(JsonSource element, string? within = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        checks.Add((actual, context, description) =>
        {
            var expected = element.Resolve(context.Variables);
            var where = within is null ? "" : $" within {within}";
            List<JsonNode?> candidates;
            if (within is null)
            {
                if (actual is not JsonArray array)
                    throw new JsonMatchException($"{description}: expected an array containing {expected?.ToJsonString()}, got:{Environment.NewLine}{JsonMatch.Pretty(actual)}");
                candidates = [.. array];
            }
            else
            {
                candidates = JsonPaths.Select(actual, within)
                    .SelectMany(hit => hit.Node is JsonArray inner ? inner.AsEnumerable() : [hit.Node])
                    .ToList();
            }

            if (candidates.Any(item => JsonMatch.IsMatch(expected, item, MatchOptions)))
                return;
            var nearest = candidates.Select(item => (item, diff: JsonMatch.Compare(expected, item, MatchOptions))).OrderBy(x => x.diff.Count).FirstOrDefault();
            var detail = nearest.diff is null
                ? $"Nothing to search{where}. Body:{Environment.NewLine}{JsonMatch.Pretty(actual)}"
                : "Nearest element:" + Environment.NewLine + JsonMatch.FormatFailure(nearest.diff, nearest.item);
            throw new JsonMatchException($"{description}: no element of {candidates.Count}{where} matched {expected?.ToJsonString()}.{Environment.NewLine}{detail}");
        });
    }

    /// <summary>Compare against a stored snapshot.</summary>
    public void MatchesSnapshot(string? name)
    {
        if (snapshot)
            throw new InvalidOperationException("A step takes one snapshot.");
        snapshot = true;
        snapshotName = name;
    }

    /// <summary>Paths a new snapshot records.</summary>
    public void Only(IEnumerable<string> paths)
    {
        if (!snapshot)
            throw new InvalidOperationException("Only(...) only applies after MatchesSnapshot().");
        only.AddRange(paths);
    }

    /// <summary>Paths removed before comparing.</summary>
    public void Ignoring(IEnumerable<string> paths) => ignoring.AddRange(paths);

    /// <summary>Compare arrays in any order.</summary>
    public void UnorderedArrays() => unordered = true;

    /// <summary>A C# assertion on the body deserialized as <typeparamref name="T"/>.</summary>
    public void Satisfies<T>(Action<T> assertion, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        checks.Add((actual, _, description) =>
        {
            var value = actual is null ? default : actual.Deserialize<T>(options ?? Json.DefaultOptions);
            if (value is null)
                throw new XunitException($"{description}: body did not deserialize to {typeof(T).Name}");
            try
            {
                assertion(value);
            }
            catch (Exception ex)
            {
                throw new XunitException($"{description}: {ex.Message}{Environment.NewLine}Actual:{Environment.NewLine}{JsonMatch.Pretty(actual)}", ex);
            }
        });
    }

    /// <summary>A C# predicate on the body deserialized as <typeparamref name="T"/>.</summary>
    public void Satisfies<T>(Func<T, bool> predicate, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Satisfies<T>(value =>
        {
            if (!predicate(value))
                throw new XunitException($"{typeof(T).Name} did not satisfy the predicate");
        }, options);
    }

    /// <summary>Resolves the snapshot name for this run (call once per step run, not per retry).</summary>
    public string? ResolveSnapshotName(ScenarioContext context) =>
        snapshot ? context.NextSnapshotName(snapshotName) : null;

    /// <summary>Throws when <paramref name="actual"/> fails any check.</summary>
    public void Verify(JsonNode? actual, ScenarioContext context, string? resolvedSnapshotName, string description)
    {
        foreach (var check in checks)
            check(actual, context, description);
        if (snapshot)
        {
            JsonSnapshot.AssertMatches(actual, resolvedSnapshotName, context.Variables,
                only.Count > 0 ? only : null, ignoring.Count > 0 ? ignoring : null);
        }
    }

    private void AddMatch(JsonSource json, bool exact)
    {
        ArgumentNullException.ThrowIfNull(json);
        checks.Add((actual, context, description) =>
            JsonMatch.AssertMatches(json.Resolve(context.Variables), actual, MatchOptions with { Exact = exact },
                $"{description} vs {json.Describe()}"));
    }
}
