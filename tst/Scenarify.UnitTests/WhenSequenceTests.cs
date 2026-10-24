using Xunit.Sdk;

namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public class WhenSequenceTests
{
    [Fact]
    public void Second_When_says_how_to_fix_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new UnstartedFixture().Scenario().When(_ => Task.CompletedTask).When(_ => Task.CompletedTask));

        Assert.Contains("exactly one When (the trigger under test)", ex.Message);
        Assert.Contains(".Given(...)", ex.Message);
        Assert.Contains(".When(first, second, ...)", ex.Message);
    }

    [Fact]
    public async Task Multi_step_When_runs_children_in_order_after_givens_with_variables()
    {
        var log = new List<string>();
        await new UnstartedFixture().Scenario()
            .With("x", "v1")
            .Given(Step("given", log))
            .When(Step("a", log), new DelegateStep("b", ctx =>
            {
                log.Add("b:" + ctx.Variables.ExpandText("{{x}}"));
                ctx.Variables.Set("x", "v2");
                return Task.CompletedTask;
            }), new DelegateStep("c", ctx =>
            {
                log.Add("c:" + ctx.Variables.ExpandText("{{x}}"));
                return Task.CompletedTask;
            }))
            .Then(Step("then", log))
            .RunAsync();

        Assert.Equal(["given", "a", "b:v1", "c:v2", "then"], log);
    }

    [Fact]
    public async Task Failing_child_fails_the_scenario_with_its_error_and_stops_the_rest()
    {
        var log = new List<string>();
        var ex = await Assert.ThrowsAsync<XunitException>(() => new UnstartedFixture().Scenario()
            .When(Step("a", log), new DelegateStep("b", _ => throw new InvalidOperationException("b broke")), Step("c", log))
            .Then(Step("then", log))
            .RunAsync());

        Assert.Contains("failed at When #1 (a then b then c)", ex.Message);
        Assert.Contains("b broke", ex.Message);
        Assert.Equal(["a"], log);
    }

    private static DelegateStep Step(string name, List<string> log) =>
        new(name, _ =>
        {
            log.Add(name);
            return Task.CompletedTask;
        });

    private sealed class UnstartedFixture() : ServiceTestFixture(new ServiceTestOptions());
}
