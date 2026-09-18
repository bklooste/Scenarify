namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public class JsonPathsAndSnapshotTests
{
    private const string Bet = """
        {
          "betId": "bet-0000001",
          "status": "Settled",
          "payout": 35.0,
          "legs": [
            { "product": { "marketType": "win", "code": "FWIN" }, "odds": 3.5 },
            { "product": { "marketType": "place", "code": "FPLC" }, "odds": 1.5 }
          ]
        }
        """;

    [Theory]
    [InlineData("status", "\"Settled\"")]
    [InlineData("$.status", "\"Settled\"")]
    [InlineData("$.legs[1].odds", "1.5")]
    [InlineData("legs[0].product.code", "\"FWIN\"")]
    [InlineData("$['payout']", "35.0")]
    public void select_single(string path, string expected)
    {
        Assert.Equal(expected, JsonPaths.SelectSingle(JsonNode.Parse(Bet), path)!.ToJsonString());
    }

    [Fact]
    public void select_wildcard_yields_concrete_paths()
    {
        var hits = JsonPaths.Select(JsonNode.Parse(Bet), "legs[*].product.marketType").ToList();
        Assert.Equal(["$.legs[0].product.marketType", "$.legs[1].product.marketType"], hits.Select(h => h.Path));
    }

    [Fact]
    public void select_single_explains_zero_or_many()
    {
        Assert.Contains("selected nothing", Assert.Throws<InvalidOperationException>(() => JsonPaths.SelectSingle(JsonNode.Parse(Bet), "nope")).Message);
        Assert.Contains("more than one", Assert.Throws<InvalidOperationException>(() => JsonPaths.SelectSingle(JsonNode.Parse(Bet), "legs[*].odds")).Message);
    }

    [Fact]
    public void project_keeps_structure_of_selected_paths_only()
    {
        var projected = JsonPaths.Project(JsonNode.Parse(Bet), ["status", "legs[*].product.marketType", "payout"]);
        Assert.Equal(
            """{"status":"Settled","legs":[{"product":{"marketType":"win"}},{"product":{"marketType":"place"}}],"payout":35.0}""",
            projected!.ToJsonString());
    }

    [Fact]
    public void project_merges_overlapping_array_paths()
    {
        var projected = JsonPaths.Project(JsonNode.Parse(Bet), ["legs[*].odds", "legs[0].product.code"]);
        Assert.Equal("""{"legs":[{"odds":3.5,"product":{"code":"FWIN"}},{"odds":1.5}]}""", projected!.ToJsonString());
    }

    [Fact]
    public void project_single_index_fills_other_elements_with_any()
    {
        var projected = JsonPaths.Project(JsonNode.Parse(Bet), ["legs[1].odds"]);
        Assert.Equal("""{"legs":["{{any}}",{"odds":1.5}]}""", projected!.ToJsonString());
        Assert.True(JsonMatch.IsMatch(projected, JsonNode.Parse(Bet)));
    }

    [Fact]
    public void remove_handles_wildcards()
    {
        var node = JsonNode.Parse(Bet);
        JsonPaths.Remove(node, "legs[*].product");
        Assert.DoesNotContain("product", node!.ToJsonString());
    }

    [Fact]
    public void reverse_substitution_uses_tokens_and_any_matchers()
    {
        var vars = new ScenarioVariables("sbx", "cust-7d1f9a2e").Set("betId", "bet-0000001").Set("stake", 10);
        var actual = JsonNode.Parse("""
            { "betId": "bet-0000001", "customerId": "cust-7d1f9a2e", "brand": "sbx", "stake": 10, "units": 10,
              "walletId": "cust-7d1f9a2e:AUD", "txId": "5b4e7c3a-0a36-4a38-9a90-5d2e9a2b1c11",
              "placedAt": "2026-09-15T10:11:12.123Z", "status": "Accepted" }
            """);

        var value = JsonSnapshot.ToSnapshotValue(actual, vars)!;

        Assert.Equal("{{betId}}", value["betId"]!.GetValue<string>());
        Assert.Equal("{{customerId}}", value["customerId"]!.GetValue<string>());
        Assert.Equal("{{brand}}", value["brand"]!.GetValue<string>());    // short, but the property names it
        Assert.Equal("{{stake}}", value["stake"]!.GetValue<string>());
        Assert.Equal(10, value["units"]!.GetValue<int>());                  // small number, unrelated name: literal
        Assert.Equal("cust-7d1f9a2e:AUD", value["walletId"]!.GetValue<string>()); // composites stay literal
        Assert.Equal("{{any:guid}}", value["txId"]!.GetValue<string>());
        Assert.Equal("{{any:datetime}}", value["placedAt"]!.GetValue<string>());
        Assert.Equal("Accepted", value["status"]!.GetValue<string>());
    }

    [Fact]
    public void reverse_substitution_refuses_to_guess_between_variables()
    {
        var vars = new ScenarioVariables("sbx", "same-value-123").Set("betId", "same-value-123");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JsonSnapshot.ToSnapshotValue(JsonNode.Parse("""{ "id": "same-value-123" }"""), vars));
        Assert.Contains("'customerId'", ex.Message);
        Assert.Contains("'betId'", ex.Message);
    }

    [Fact]
    public void reverse_substitution_prefers_the_named_variable_when_values_collide()
    {
        var vars = new ScenarioVariables("sbx", "same-value-123").Set("betId", "same-value-123");
        var value = JsonSnapshot.ToSnapshotValue(JsonNode.Parse("""{ "betId": "same-value-123" }"""), vars)!;
        Assert.Equal("{{betId}}", value["betId"]!.GetValue<string>());
    }

    [Fact]
    public void missing_snapshot_fails_and_writes_received_with_only_paths()
    {
        var name = $"unit-{Guid.NewGuid():N}/missing";
        var vars = new ScenarioVariables("sbx", "cust-7d1f9a2e").Set("betId", "bet-0000001");

        var ex = Assert.Throws<JsonMatchException>(() =>
            JsonSnapshot.AssertMatches(Bet, name, vars, only: ["betId", "legs[*].product.marketType"]));

        var received = Path.Combine(JsonSnapshot.ReceivedDirectory, name + ".received.json");
        Assert.Contains(received, ex.Message);
        var written = JsonNode.Parse(File.ReadAllText(received))!;
        Assert.Equal("""["betId","legs[*].product.marketType"]""", written["$only"]!.ToJsonString());
        Assert.Equal("""{"betId":"{{betId}}","legs":[{"product":{"marketType":"win"}},{"product":{"marketType":"place"}}]}""",
            written["value"]!.ToJsonString());
    }

    [Fact]
    public void accepted_snapshot_passes_on_a_later_run_with_new_ids()
    {
        var name = $"unit-{Guid.NewGuid():N}/accepted";
        var first = new ScenarioVariables("sbx", "cust-aaaaaaaa").Set("betId", "bet-1111111");
        Assert.Throws<JsonMatchException>(() =>
            JsonSnapshot.AssertMatches(Bet.Replace("bet-0000001", "bet-1111111"), name, first, only: ["betId", "status"]));
        Accept(name);

        var second = new ScenarioVariables("sbx", "cust-bbbbbbbb").Set("betId", "bet-2222222");
        JsonSnapshot.AssertMatches(Bet.Replace("bet-0000001", "bet-2222222"), name, second);
    }

    [Fact]
    public void changed_value_fails_with_diff_and_rewrites_received()
    {
        var name = $"unit-{Guid.NewGuid():N}/changed";
        Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches(Bet, name, only: ["status"]));
        Accept(name);

        var ex = Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches(Bet.Replace("Settled", "Voided"), name));

        Assert.Contains("$.status: expected \"Settled\", actual \"Voided\"", ex.Message);
        var received = JsonNode.Parse(File.ReadAllText(Path.Combine(JsonSnapshot.ReceivedDirectory, name + ".received.json")))!;
        Assert.Equal("""["status"]""", received["$only"]!.ToJsonString());
    }

    [Fact]
    public void whole_snapshot_fails_on_new_fields()
    {
        var name = $"unit-{Guid.NewGuid():N}/whole";
        Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches("""{ "a": 1 }""", name));
        Accept(name);

        var ex = Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches("""{ "a": 1, "b": 2 }""", name));
        Assert.Contains("$.b", ex.Message);
    }

    [Fact]
    public void ignoring_snapshot_skips_those_paths()
    {
        var name = $"unit-{Guid.NewGuid():N}/ignoring";
        Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches("""{ "a": 1, "seq": 5 }""", name, ignoring: ["seq"]));
        Accept(name);

        JsonSnapshot.AssertMatches("""{ "a": 1, "seq": 99 }""", name);
    }

    [Fact]
    public void code_paths_that_disagree_with_the_file_fail()
    {
        var name = $"unit-{Guid.NewGuid():N}/disagree";
        Assert.Throws<JsonMatchException>(() => JsonSnapshot.AssertMatches(Bet, name, only: ["status"]));
        Accept(name);

        JsonSnapshot.AssertMatches(Bet, name, only: ["status"]);
        var ex = Assert.Throws<InvalidOperationException>(() => JsonSnapshot.AssertMatches(Bet, name, only: ["payout"]));
        Assert.Contains("differ", ex.Message);
    }

    [Fact]
    public void snapshot_name_comes_from_the_running_test()
    {
        Assert.Equal("JsonPathsAndSnapshotTests/snapshot_name_comes_from_the_running_test", JsonSnapshot.NameForCurrentTest());
    }

    [Theory]
    [InlineData(1)]
    public void parameterised_tests_need_a_case_name(int row)
    {
        Assert.Equal(1, row);
        Assert.Contains("case name", Assert.Throws<InvalidOperationException>(() => JsonSnapshot.NameForCurrentTest()).Message);
        Assert.Equal("JsonPathsAndSnapshotTests/parameterised_tests_need_a_case_name.row-one", JsonSnapshot.NameForCurrentTest("row one"));
    }

    private static void Accept(string name)
    {
        var target = Path.Combine(JsonSnapshot.Directory, name + ".snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Path.Combine(JsonSnapshot.ReceivedDirectory, name + ".received.json"), target, overwrite: true);
    }
}
