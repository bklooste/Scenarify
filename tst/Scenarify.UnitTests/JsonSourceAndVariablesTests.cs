namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public class JsonSourceAndVariablesTests
{
    [Theory]
    [InlineData("""{ "a": 1 }""", "inline JSON")]
    [InlineData("""  [1]""", "inline JSON")]
    [InlineData("\"bare-string\"", "inline JSON")]
    [InlineData("cases/x.json", "cases/x.json")]
    public void implicit_string_picks_inline_or_file(string text, string description)
    {
        JsonSource source = text;
        Assert.Equal(description, source.Describe());
    }

    [Fact]
    public void file_source_reads_relative_to_output_directory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "cases", "unit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file-source.json"), """{ "id": "{{guid}}" }""");
        var vars = new ScenarioVariables();

        var node = Json.File("cases/unit/file-source.json").Resolve(vars);

        Assert.Equal(vars.GetString("guid"), node!["id"]!.GetValue<string>());
    }

    [Fact]
    public void missing_file_explains_copy_to_output()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => Json.File("cases/nope.json").ReadText());
        Assert.Contains("CopyToOutputDirectory", ex.Message);
    }

    [Fact]
    public void invalid_json_names_the_source()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Json.Inline("{ nope").Resolve());
        Assert.Contains("inline JSON", ex.Message);
    }

    [Fact]
    public void typed_source_carries_clr_type_and_serializes_web_style()
    {
        var source = Json.From(new Sample("x", Colour.Green, null));
        Assert.Equal(typeof(Sample), source.ClrType);
        Assert.Equal("""{"name":"x","colour":"Green"}""", source.ResolveText());
    }

    [Fact]
    public void anonymous_objects_serialize_their_properties()
    {
        Assert.Equal("""{"amount":10}""", Json.From(new { amount = 10 }).ResolveText());
    }

    [Fact]
    public void whole_string_token_keeps_variable_type()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("stake", 12.5m).Set("flag", true);
        var node = vars.ExpandJson("""{ "stake": "{{stake}}", "flag": "{{flag}}", "brand": "{{brand}}" }""")!;
        Assert.Equal("""{"stake":12.5,"flag":true,"brand":"sbx"}""", node.ToJsonString());
    }

    [Fact]
    public void unquoted_tokens_become_json()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("odds", 3.5).Set("ids", new[] { "a", "b" });
        var node = vars.ExpandJson("""{ "odds": {{odds}}, "ids": {{ids}}, "any": {{any:number}} }""")!;
        Assert.Equal("""{"odds":3.5,"ids":["a","b"],"any":"{{any:number}}"}""", node.ToJsonString());
    }

    [Fact]
    public void tokens_inside_strings_and_keys_are_textual()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("n", 7);
        var node = vars.ExpandJson("""{ "{{brand}}-key": "wallet:{{customerId}}:{{n}}", "brace": "{ not a token }" }""")!;
        Assert.Equal("""{"sbx-key":"wallet:c1:7","brace":"{ not a token }"}""", node.ToJsonString());
    }

    [Fact]
    public void escaped_quotes_do_not_confuse_the_scanner()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("n", 7);
        var node = vars.ExpandJson("""{ "a": "say \"{{brand}}\"", "b": {{n}} }""")!;
        Assert.Equal("say \"sbx\"", node["a"]!.GetValue<string>());
        Assert.Equal(7, node["b"]!.GetValue<int>());
    }

    [Fact]
    public void matcher_tokens_survive_expansion()
    {
        var node = new ScenarioVariables().ExpandJson("""{ "id": "{{any:guid}}", "x": "{{ any }}" }""")!;
        Assert.Equal("{{any:guid}}", node["id"]!.GetValue<string>());
        Assert.Equal("{{ any }}", node["x"]!.GetValue<string>());
    }

    [Fact]
    public void matcher_inside_a_longer_string_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new ScenarioVariables().ExpandJson("""{ "id": "x-{{any}}" }"""));
    }

    [Fact]
    public void unknown_variable_lists_known_ones()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ScenarioVariables().ExpandText("/v1/{{betId}}"));
        Assert.Contains("betId", ex.Message);
        Assert.Contains("customerId", ex.Message);
        Assert.Contains(".Capture", ex.Message);
    }

    [Fact]
    public void text_expansion_uses_raw_strings()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("eventId", Guid.Parse("5b4e7c3a-0a36-4a38-9a90-5d2e9a2b1c11"));
        Assert.Equal("v1/sbx/5b4e7c3a-0a36-4a38-9a90-5d2e9a2b1c11/c1", vars.ExpandText("v1/{{brand}}/{{eventId}}/{{customerId}}"));
    }

    [Fact]
    public void now_offsets_are_relative_to_the_fixed_now()
    {
        var now = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
        var vars = new ScenarioVariables("sbx", "c1", now);
        Assert.Equal("2026-09-15T10:00:00.0000000Z", vars.ExpandText("{{now}}"));
        Assert.Equal("2026-09-16T10:00:00.0000000Z", vars.ExpandText("{{now+1d}}"));
        Assert.Equal("2026-09-15T09:30:00.0000000Z", vars.ExpandText("{{now-30m}}"));
        Assert.Equal("2026-09-15T12:00:00.0000000Z", vars.ExpandText("{{now+2h}}"));
    }

    [Fact]
    public void patch_merges_objects_replaces_arrays_and_removes_nulls()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("n", 3);
        var patched = Json.Patch(
            """{ "name": "a", "terms": { "min": 1, "max": 9, "rows": [1, 2] }, "drop": true, "map": { "k": 1 } }""",
            """{ "terms": { "min": "{{n}}", "rows": [] }, "drop": null, "map": {}, "extra": "{{brand}}" }""");

        Assert.Equal("""{"name":"a","terms":{"min":3,"max":9,"rows":[]},"map":{},"extra":"sbx"}""", patched.ResolveText(vars));
    }

    [Fact]
    public void array_source_resolves_each_item_with_variables()
    {
        var vars = new ScenarioVariables("sbx", "c1");
        Assert.Equal("""[{"a":"sbx"},{"a":1,"b":"c1"}]""",
            Json.Array("""{ "a": "{{brand}}" }""", Json.Patch("""{ "a": 1 }""", """{ "b": "{{customerId}}" }""")).ResolveText(vars));
    }

    [Fact]
    public void typed_parse_and_indexer()
    {
        Assert.Equal(new Sample("x", Colour.Green, null), Json.Parse<Sample>("""{ "name": "x", "colour": "Green" }"""));
        Assert.Equal("sbx", new ScenarioVariables("sbx", "c1")["brand"]);
    }

    [Fact]
    public void empty_patch_at_the_root_changes_nothing()
    {
        Assert.Equal("""{"a":1}""", Json.Patch("""{ "a": 1 }""", "{}").ResolveText());
    }

    [Fact]
    public void patch_keeps_the_clr_type_of_a_typed_source()
    {
        var patched = Json.From(new Sample("x", Colour.Red, null)).Patch("""{ "name": "y" }""");
        Assert.Equal(typeof(Sample), patched.ClrType);
        Assert.Equal("""{"name":"y","colour":"Red"}""", patched.ResolveText());
    }

    [Fact]
    public void tokens_inside_variable_values_expand_at_use_time()
    {
        var vars = new ScenarioVariables("sbx", "c1")
            .Set("walletId", "{{customerId}}:{{currency}}")
            .Set("placed", new JsonObject { ["at"] = "{{now}}", ["wallet"] = "{{walletId}}" })
            .Set("currency", "AUD");

        Assert.Equal("c1:AUD", vars["walletId"]);
        var placed = vars.ExpandJson("""{ "p": "{{placed}}" }""")!["p"]!;
        Assert.Equal(vars.ExpandText("{{now}}"), placed["at"]!.GetValue<string>());
        Assert.Equal("c1:AUD", placed["wallet"]!.GetValue<string>());
    }

    [Fact]
    public void format_fills_tokens_from_csharp_values_then_scenario_variables()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("job3", "id-3");
        var n = 3;
        var status = "Processed";

        var source = Json.Format(
            """{ "jobId": "{{jobId}}", "customer": "{{customerId}}", "selectionId": "{{n}}", "label": "sel-{{n}}", "status": "{{status}}" }""",
            new { jobId = Json.Token($"job{n}"), n, status });

        Assert.Equal("""{"jobId":"id-3","customer":"c1","selectionId":3,"label":"sel-3","status":"Processed"}""", source.ResolveText(vars));
        Assert.False(vars.TryGet("status", out _));
    }

    [Fact]
    public void format_values_override_scenario_variables_and_accept_dictionaries()
    {
        var vars = new ScenarioVariables("sbx", "c1");
        var source = Json.Format(Json.Patch("""{ "b": "{{brand}}" }""", """{ "x": "{{x}}" }"""),
            new Dictionary<string, object?> { ["brand"] = "other", ["x"] = new[] { 1, 2 } });
        Assert.Equal("""{"b":"other","x":[1,2]}""", source.ResolveText(vars));
    }

    [Fact]
    public void format_locals_do_not_change_tokens_inside_stored_variables()
    {
        var vars = new ScenarioVariables("sbx", "c1")
            .Set("eventId", "e1")
            .Set("events", new JsonArray(new JsonObject { ["id"] = "{{eventId}}" }));

        var source = Json.Format("""{ "eventId": "{{eventId}}", "events": "{{events}}" }""", new { eventId = "e2" });

        Assert.Equal("""{"eventId":"e2","events":[{"id":"e1"}]}""", source.ResolveText(vars));
    }

    [Fact]
    public void json_source_variables_resolve_with_tokens_at_use_time()
    {
        var vars = new ScenarioVariables("sbx", "c1").Set("event", Json.Inline("""{ "brand": "{{brand}}", "id": "{{eventId}}" }"""));
        vars.Set("eventId", "e9");
        Assert.Equal("""{"e":{"brand":"sbx","id":"e9"}}""", vars.ExpandJson("""{ "e": "{{event}}" }""")!.ToJsonString());
        Assert.Contains("event", vars.ExpandedValues().Select(kv => kv.Key));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData(" 3.5 ", "3.5")]
    [InlineData("-2e3", "-2e3")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    public void bare_scalars_are_inline_json(string text, string json)
    {
        JsonSource source = text;
        Assert.Equal("inline JSON", source.Describe());
        Assert.Equal(json, source.ResolveText());
    }

    [Fact]
    public void token_builds_token_text()
    {
        Assert.Equal("{{job3}}", Json.Token("job3"));
        Assert.Throws<ArgumentException>(() => Json.Token(" "));
    }

    [Fact]
    public void self_referencing_variables_fail_clearly()
    {
        var vars = new ScenarioVariables().Set("a", "x{{b}}").Set("b", "y{{a}}");
        Assert.Contains("refers to itself", Assert.Throws<InvalidOperationException>(() => vars.ExpandText("{{a}}")).Message);
    }

    [Fact]
    public void snapshots_reverse_substitute_expanded_variable_values()
    {
        var vars = new ScenarioVariables("sbx", "cust-7d1f9a2e").Set("walletId", "{{customerId}}:AUD");
        var value = JsonSnapshot.ToSnapshotValue(JsonNode.Parse("""{ "walletId": "cust-7d1f9a2e:AUD" }"""), vars)!;
        Assert.Equal("{{walletId}}", value["walletId"]!.GetValue<string>());
    }

    [Fact]
    public void guid_is_fresh_per_instance()
    {
        Assert.NotEqual(new ScenarioVariables().GetString("guid"), new ScenarioVariables().GetString("guid"));
    }

    [Fact]
    public void matcher_names_cannot_be_variables()
    {
        Assert.Throws<ArgumentException>(() => new ScenarioVariables().Set("any:guid", "x"));
    }

    public enum Colour { Red, Green }

    public sealed record Sample(string Name, Colour Colour, string? Missing);
}
