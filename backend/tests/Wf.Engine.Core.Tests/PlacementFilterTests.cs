using System.Collections.Immutable;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Placement;

namespace Wf.Engine.Core.Tests;

public sealed class PlacementFilterTests
{
    [Theory]
    [InlineData("os")]
    [InlineData("pool")]
    [InlineData("label")]
    [InlineData("machine")]
    public void Symbolic_selectors_cannot_allocate_machines(string field)
    {
        var variable = new SelectorValue(variable: "environment");
        var selector = field switch
        {
            "os" => new PlacementSelector(os: variable),
            "pool" => new PlacementSelector(pool: variable),
            "label" => new PlacementSelector(labels: [new("role", variable)]),
            "machine" => new PlacementSelector(machines: [variable]),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Throws<ArgumentException>(() => PlacementFilter.EnsureResolved(selector));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("os")]
    [InlineData("pool")]
    [InlineData("name")]
    [InlineData("presence")]
    [InlineData("value")]
    public void Reservation_and_task_selectors_share_ordinal_intersection_semantics(string mismatch)
    {
        var id = Guid.NewGuid();
        var agent = new PlacementAgent(id, "perf-a", "linux", "ci", 0, 1, 4096,
            Labels: ImmutableDictionary<string, string>.Empty.Add("role", "perf").Add("capability", "load"));
        var selector = new PlacementSelector(os: "linux", pool: "ci", labels: [new("role", "perf"), new("capability")],
            machineIds: [id]);
        PlacementFilter.EnsureResolved(selector);
        Assert.True(PlacementFilter.Matches(selector, agent));
        var changed = mismatch switch
        {
            "id" => agent with { Id = Guid.NewGuid() },
            "os" => agent with { Os = "windows" },
            "pool" => agent with { Pool = "CI" },
            "name" => agent with { Name = "PERF-A" },
            "presence" => agent with { Labels = agent.Labels!.Remove("capability") },
            "value" => agent with { Labels = agent.Labels!.SetItem("role", "PERF") },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        if (mismatch == "name") selector = new(machines: ["perf-a"]);
        Assert.False(PlacementFilter.Matches(selector, changed));
    }
}
