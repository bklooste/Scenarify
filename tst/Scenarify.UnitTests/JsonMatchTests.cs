namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public class JsonMatchTests
{
    private static IReadOnlyList<JsonMismatch> Compare(string expected, string actual, JsonMatchOptions? options = null) =>
        JsonMatch.Compare(JsonNode.Parse(expected), JsonNode.Parse(actual), options);

    [Fact]
    public void subset_ignores_extra_properties()
    {
        Assert.Empty(Compare("""{ "a": 1 }""", """{ "a": 1, "b": 2 }"""));
    }

    [Fact]
    public void exact_reports_extra_properties_by_path()
    {
        var diff = Assert.Single(Compare("""{ "a": 1 }""", """{ "a": 1, "b": { "c": 2 } }""", JsonMatchOptions.ExactMatch));
        Assert.Equal("$.b", diff.Path);
        Assert.Equal("(missing)", diff.Expected);
    }

    [Fact]
    public void missing_property_is_reported_even_when_expected_null()
    {
        var diff = Assert.Single(Compare("""{ "a": null }""", """{ }"""));
        Assert.Equal("$.a", diff.Path);
        Assert.Equal("(missing)", diff.Actual);
    }

    [Fact]
    public void every_difference_is_listed_with_nested_paths()
    {
        var diffs = Compare(
            """{ "status": "Settled", "legs": [{ "odds": 2.5 }, { "odds": 3 }] }""",
            """{ "status": "Accepted", "legs": [{ "odds": 2.5 }, { "odds": 4 }] }""");
        Assert.Equal(["$.status", "$.legs[1].odds"], diffs.Select(d => d.Path));
        Assert.Equal("\"Settled\"", diffs[0].Expected);
        Assert.Equal("\"Accepted\"", diffs[0].Actual);
    }

    [Theory]
    [InlineData("10", "10.0")]
    [InlineData("1e2", "100")]
    [InlineData("0.1", "0.10")]
    public void numbers_compare_by_value(string expected, string actual)
    {
        Assert.Empty(Compare($$"""{ "n": {{expected}} }""", $$"""{ "n": {{actual}} }"""));
    }

    [Theory]
    [InlineData("2026-09-17T10:00:02.0790660Z", "2026-09-17T10:00:02.079066Z", true)]
    [InlineData("2026-09-17T10:00:02Z", "2026-09-17T20:00:02+10:00", true)]
    [InlineData("2026-09-17T10:00:02.0790660Z", "2026-09-17T10:00:02.079067Z", false)]
    [InlineData("2026-09-17", "2026-09-17T00:00:00Z", false)]
    public void timestamps_compare_as_instants(string expected, string actual, bool equal)
    {
        Assert.Equal(equal, Compare($$"""{ "t": "{{expected}}" }""", $$"""{ "t": "{{actual}}" }""").Count == 0);
    }

    [Fact]
    public void string_and_number_are_different()
    {
        Assert.Single(Compare("""{ "n": "10" }""", """{ "n": 10 }"""));
    }

    [Fact]
    public void ordered_arrays_report_length_and_element()
    {
        var diffs = Compare("""[1, 2, 3]""", """[1, 5]""");
        Assert.Equal(["$.length", "$[1]"], diffs.Select(d => d.Path));
    }

    [Fact]
    public void arrays_are_ordered_by_default()
    {
        Assert.NotEmpty(Compare("""[1, 2]""", """[2, 1]"""));
    }

    [Fact]
    public void unordered_arrays_match_any_order_with_subset_elements()
    {
        var options = new JsonMatchOptions { UnorderedArrays = true };
        Assert.Empty(Compare("""[{ "id": "b" }, { "id": "a" }]""", """[{ "id": "a", "x": 1 }, { "id": "b" }]""", options));
    }

    [Fact]
    public void unordered_arrays_do_not_reuse_an_element()
    {
        var options = new JsonMatchOptions { UnorderedArrays = true };
        var diffs = Compare("""[{ "id": "a" }, { "id": "a" }]""", """[{ "id": "a" }, { "id": "b" }]""", options);
        Assert.Equal("$[1]", Assert.Single(diffs).Path);
    }

    [Theory]
    [InlineData("{{any}}", "null")]
    [InlineData("{{any}}", "[1]")]
    [InlineData("{{any:guid}}", "\"5b4e7c3a-0a36-4a38-9a90-5d2e9a2b1c11\"")]
    [InlineData("{{any:datetime}}", "\"2026-09-15T10:11:12.123Z\"")]
    [InlineData("{{any:datetime}}", "\"2026-09-15T10:11:12+10:00\"")]
    [InlineData("{{any:number}}", "3.5")]
    [InlineData("{{any:string}}", "\"x\"")]
    [InlineData("{{any:bool}}", "false")]
    [InlineData("{{any:object}}", "{}")]
    [InlineData("{{any:array}}", "[]")]
    public void matchers_accept_their_shape(string matcher, string actual)
    {
        Assert.Empty(Compare($$"""{ "v": "{{matcher}}" }""", $$"""{ "v": {{actual}} }"""));
    }

    [Theory]
    [InlineData("{{any:guid}}", "\"not-a-guid\"")]
    [InlineData("{{any:datetime}}", "\"soon\"")]
    [InlineData("{{any:datetime}}", "\"42\"")]
    [InlineData("{{any:number}}", "\"3.5\"")]
    [InlineData("{{any:string}}", "1")]
    public void matchers_reject_other_shapes(string matcher, string actual)
    {
        var diff = Assert.Single(Compare($$"""{ "v": "{{matcher}}" }""", $$"""{ "v": {{actual}} }"""));
        Assert.Equal(matcher, diff.Expected);
    }

    [Fact]
    public void any_still_requires_presence()
    {
        Assert.Single(Compare("""{ "v": "{{any}}" }""", """{ }"""));
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "v": null }""")]
    [InlineData("""{ "v": false }""")]
    [InlineData("""{ "v": 0 }""")]
    [InlineData("""{ "v": 0.0 }""")]
    [InlineData("""{ "v": "" }""")]
    public void absent_accepts_missing_or_default(string actual)
    {
        Assert.Empty(Compare("""{ "v": "{{absent}}" }""", actual));
    }

    [Theory]
    [InlineData("""{ "v": true }""")]
    [InlineData("""{ "v": 1 }""")]
    [InlineData("""{ "v": "x" }""")]
    [InlineData("""{ "v": {} }""")]
    public void absent_rejects_real_values(string actual)
    {
        Assert.Equal("{{absent}}", Assert.Single(Compare("""{ "v": "{{absent}}" }""", actual)).Expected);
    }

    [Theory]
    [InlineData("\"Not enough funds in wallet\"", true)]
    [InlineData("\"not enough funds\"", false)]
    [InlineData("3", false)]
    public void contains_matcher_checks_substrings(string actual, bool ok)
    {
        var expected = new ScenarioVariables().ExpandJson($$"""{ "detail": "{{Json.Contains("Not enough funds")}}", "amount": "{{Json.Absent}}" }""");
        Assert.Equal(ok, JsonMatch.IsMatch(expected, JsonNode.Parse($$"""{ "detail": {{actual}} }""")));
    }

    [Fact]
    public void unknown_matcher_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Compare("""{ "v": "{{any:colour}}" }""", """{ "v": 1 }"""));
    }

    [Fact]
    public void ignoring_removes_paths_from_both_sides()
    {
        var options = new JsonMatchOptions { Exact = true, Ignoring = ["id", "legs[*].placedAt"] };
        Assert.Empty(Compare(
            """{ "id": 1, "legs": [{ "placedAt": "a", "x": 1 }] }""",
            """{ "id": 2, "legs": [{ "placedAt": "b", "x": 1 }] }""",
            options));
    }

    [Fact]
    public void failure_message_lists_differences_then_actual_body()
    {
        var ex = Assert.Throws<JsonMatchException>(() =>
            JsonMatch.AssertMatches(Json.Inline("""{ "status": "Settled" }"""), """{ "status": "Accepted", "id": 7 }"""));
        Assert.Contains("1 difference", ex.Message);
        Assert.Contains("$.status: expected \"Settled\", actual \"Accepted\"", ex.Message);
        Assert.Contains("Actual:", ex.Message);
        Assert.Contains("\"id\": 7", ex.Message);
    }

    [Fact]
    public void non_json_actual_shows_the_text()
    {
        var ex = Assert.Throws<JsonMatchException>(() => JsonMatch.AssertMatches(Json.Inline("{}"), "<html>oops</html>"));
        Assert.Contains("<html>oops</html>", ex.Message);
    }

    [Fact]
    public void assert_matches_expands_variables()
    {
        var vars = new ScenarioVariables("sbx", "cust-12345678").Set("stake", 10);
        JsonMatch.AssertMatches(Json.Inline("""{ "customerId": "{{customerId}}", "stake": "{{stake}}" }"""),
            """{ "customerId": "cust-12345678", "stake": 10.00 }""", variables: vars);
    }
}
