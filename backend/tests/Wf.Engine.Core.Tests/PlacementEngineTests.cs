using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Engine.Core.Placement;

namespace Wf.Engine.Core.Tests;

public sealed class PlacementEngineTests
{
    private static readonly Guid Run = Guid.Parse("0199ea00-0000-7000-8000-000000000001");
    private static readonly string Sha = new('a', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Too_few_matching_machine_IDs_or_requested_count_wait_without_mutating_state(bool machineIds)
    {
        var engine = new PlacementEngine();
        var selector = machineIds ? new PlacementSelector(machineIds: [Agent(2).Id]) : new PlacementSelector(count: 2);
        var decision = engine.Propose(Request() with { Policy = new(selector) }, [Agent(1)]);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.WaitingForMachine, decision.Reason);
        Assert.Equal(PlacementSnapshot.Empty, engine.Snapshot);
    }

    [Fact]
    public void Explicit_IDs_intersect_other_constraints_and_default_to_all_requested_machines()
    {
        var engine = new PlacementEngine();
        var selector = new PlacementSelector(os: "linux", machineIds: [Agent(2).Id, Agent(3).Id]);
        var request = Request() with { Policy = new(selector) };
        Assert.Null(engine.Propose(request, [Agent(1), Agent(2)]).Agent);
        Assert.Null(engine.Propose(request, [Agent(2), Agent(3) with { Os = "windows" }]).Agent);
        var agents = new[] { Agent(1) with { WarmRuns = ImmutableHashSet.Create(Run) }, Agent(3), Agent(2) };
        var selected = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var decision = engine.Propose(request, agents);
            selected.Add(decision.Agent!.Id);
            engine.Apply(decision);
        }
        Assert.Equal([Agent(2).Id, Agent(3).Id, Agent(2).Id, Agent(3).Id], selected);
    }

    [Fact]
    public void Count_limits_a_stable_input_order_independent_cohort_without_busy_machine_spillover()
    {
        var engine = new PlacementEngine();
        var request = Request() with { Policy = new(new PlacementSelector(os: "linux", count: 2)) };
        var first = Agent(1) with { SlotsUsed = 1, MaxTasks = 1 };
        var second = Agent(2);
        var extra = Agent(3) with { WarmRuns = ImmutableHashSet.Create(Run) };
        var decision = engine.Propose(request, [extra, second, first]);
        Assert.Equal(second.Id, decision.Agent!.Id);
        Assert.Equal(second.Id, engine.Propose(request, [first, second, extra]).Agent!.Id);
        engine.Apply(decision);
        var before = engine.Snapshot;
        Assert.Null(engine.Propose(request, [first, second with { SlotsUsed = second.MaxTasks }, extra]).Agent);
        Assert.Equal(before, engine.Snapshot);
        var restored = new PlacementEngine(JsonSerializer.Deserialize<PlacementSnapshot>(JsonSerializer.Serialize(before))!);
        Assert.Equal(second.Id, restored.Propose(request, [extra, first, second]).Agent!.Id);
    }

    [Theory]
    [InlineData("unhealthy")]
    [InlineData("draining")]
    [InlineData("reserved")]
    [InlineData("label")]
    [InlineData("pool")]
    [InlineData("os")]
    public void Unusable_or_nonmatching_agents_never_satisfy_requested_machine_count(string reason)
    {
        var first = Agent(1) with { Labels = ImmutableDictionary<string, string>.Empty.Add("role", "perf") };
        var second = first with { Id = Agent(2).Id, Name = "agent-2" };
        second = reason switch
        {
            "unhealthy" => second with { Healthy = false },
            "draining" => second with { Draining = true },
            "reserved" => second with { ReservedForRun = Guid.NewGuid(), ReservationEpoch = 1 },
            "label" => second with { Labels = ImmutableDictionary<string, string>.Empty },
            "pool" => second with { Pool = "other" },
            "os" => second with { Os = "windows" },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var request = Request() with
        {
            Policy = new(new PlacementSelector(os: "linux", pool: "ci", labels: [new("role", "perf")], count: 2))
        };
        var decision = new PlacementEngine().Propose(request, [second, first]);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.WaitingForMachine, decision.Reason);
    }

    [Fact]
    public void Count_preserves_existing_affinity_before_stable_UUID_cohort_ordering()
    {
        var engine = new PlacementEngine();
        var policy = new PlacementPolicy(Affinity: new(AffinityKind.Run));
        engine.Apply(engine.Propose(Request() with { Policy = policy }, [Agent(3)]));
        var request = Request() with { Policy = policy with { Selector = new(count: 2) } };
        var decision = engine.Propose(request, [Agent(1), Agent(2), Agent(3)]);
        Assert.Equal(Agent(3).Id, decision.Agent!.Id);
        Assert.Equal(PlacementReason.Affinity, decision.Reason);
    }

    [Fact]
    public void Count_subset_can_replace_a_lost_agent_only_with_another_allowed_ID()
    {
        var request = Request() with
        {
            Policy = new(new PlacementSelector(machineIds: [Agent(1).Id, Agent(2).Id], count: 1)),
            Attempt = 2, RePlacement = true, PreviousAgentId = Agent(1).Id
        };
        var decision = new PlacementEngine().Propose(request,
            [Agent(1), Agent(2), Agent(3) with { WarmRuns = ImmutableHashSet.Create(Run) }]);
        Assert.Equal(Agent(2).Id, decision.Agent!.Id);
        Assert.Null(new PlacementEngine().Propose(request, [Agent(1), Agent(3)]).Agent);
        Assert.Equal(Agent(2).Id, new PlacementEngine().Propose(request with
        {
            Policy = new(new PlacementSelector(machineIds: [Agent(1).Id, Agent(2).Id], count: 1),
                new(AffinityKind.Run))
        }, [Agent(2)]).Agent!.Id);
    }

    [Theory]
    [InlineData("unhealthy")]
    [InlineData("draining")]
    [InlineData("os")]
    [InlineData("pool")]
    [InlineData("machine")]
    [InlineData("label-missing")]
    [InlineData("label-value")]
    [InlineData("slots")]
    [InlineData("ram")]
    [InlineData("reserved-run")]
    [InlineData("reserved-node")]
    public void Every_hard_constraint_excludes_an_otherwise_matching_agent(string constraint)
    {
        var agent = Agent(1) with { Labels = ImmutableDictionary<string, string>.Empty.Add("dotnet", "10") };
        agent = constraint switch
        {
            "unhealthy" => agent with { Healthy = false },
            "draining" => agent with { Draining = true },
            "os" => agent with { Os = "windows" },
            "pool" => agent with { Pool = "other" },
            "machine" => agent with { Name = "other" },
            "label-missing" => agent with { Labels = null },
            "label-value" => agent with { Labels = ImmutableDictionary<string, string>.Empty.Add("dotnet", "9") },
            "slots" => agent with { SlotsUsed = agent.MaxTasks },
            "ram" => agent with { AvailableRamMb = 511 },
            "reserved-run" => agent with { ReservedForRun = Guid.NewGuid(), ReservationEpoch = 1 },
            "reserved-node" => agent with { ReservedForRun = Run, ReservedForNode = "other", ReservationEpoch = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(constraint))
        };
        var request = Request() with
        {
            RequiredRamMb = 512,
            Policy = new(new PlacementSelector(os: "linux", pool: "ci", labels: [new("dotnet", "10")], machines: ["agent-1"]))
        };
        var engine = new PlacementEngine();
        var decision = engine.Propose(request, [agent]);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.WaitingForMachine, decision.Reason);
        Assert.Equal(PlacementSnapshot.Empty, engine.Snapshot);
        Assert.Throws<InvalidOperationException>(() => engine.Apply(decision));
    }

    [Fact]
    public void Exact_ram_boundary_and_own_node_reservation_are_eligible()
    {
        var agent = Agent(1) with { AvailableRamMb = 512, ReservedForRun = Run, ReservedForNode = "node", ReservationEpoch = 4 };
        var decision = new PlacementEngine().Propose(Request() with { RequiredRamMb = 512 }, [agent]);
        Assert.Equal(agent.Id, decision.Agent!.Id);
        Assert.Equal(4, decision.Agent.ReservationEpoch);
    }

    [Fact]
    public void Label_presence_accepts_any_non_missing_value_and_machine_names_are_alternatives()
    {
        var agent = Agent(2) with { Labels = ImmutableDictionary<string, string>.Empty.Add("dotnet", "10") };
        var request = Request() with
        {
            Policy = new(new PlacementSelector(labels: [new("dotnet")], machines: ["agent-1", "agent-2"]))
        };
        Assert.Equal(agent.Id, new PlacementEngine().Propose(request, [agent]).Agent!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_run_or_package_precedes_lower_load_without_bypassing_capacity(bool package)
    {
        var cold = Agent(1);
        var warm = Agent(2) with
        {
            SlotsUsed = 3, MaxTasks = 4,
            WarmRuns = package ? null : ImmutableHashSet.Create(Run),
            WarmPackages = package ? ImmutableHashSet.Create(Sha.ToUpperInvariant()) : null
        };
        var engine = new PlacementEngine();
        var decision = engine.Propose(Request(), [cold, warm]);
        Assert.Equal(warm.Id, decision.Agent!.Id);
        Assert.Equal(PlacementReason.WarmHost, decision.Reason);
        Assert.Equal(cold.Id, engine.Propose(Request(), [cold, warm with { SlotsUsed = 4 }]).Agent!.Id);
    }

    [Fact]
    public void Least_load_compares_ratios_not_raw_slot_counts()
    {
        var agents = new[] { Agent(1) with { SlotsUsed = 1, MaxTasks = 2 }, Agent(2) with { SlotsUsed = 3, MaxTasks = 8 } };
        var decision = new PlacementEngine().Propose(Request(), agents);
        Assert.Equal(agents[1].Id, decision.Agent!.Id);
        Assert.Equal(PlacementReason.LeastLoaded, decision.Reason);
    }

    [Fact]
    public void Round_robin_is_input_order_independent_and_advances_only_after_commit()
    {
        var engine = new PlacementEngine();
        var agents = new[] { Agent(3), Agent(1), Agent(2) };
        var first = engine.Propose(Request(), agents);
        Assert.Equal(Agent(1).Id, first.Agent!.Id);
        Assert.Equal(first.Agent.Id, engine.Propose(Request(), agents.Reverse().ToArray()).Agent!.Id);
        Assert.Equal(0, engine.Snapshot.Revision);
        var actual = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var decision = engine.Propose(Request() with { TaskKey = $"task-{index}" }, agents);
            actual.Add(decision.Agent!.Id);
            engine.Apply(decision);
        }
        Assert.Equal(new[] { Agent(1).Id, Agent(2).Id, Agent(3).Id, Agent(1).Id }, actual);
        Assert.Throws<InvalidOperationException>(() => engine.Apply(first));
    }

    [Theory]
    [InlineData(AffinityKind.None)]
    [InlineData(AffinityKind.Node)]
    [InlineData(AffinityKind.Run)]
    [InlineData(AffinityKind.SameMachineAs)]
    public void Each_affinity_mode_applies_the_expected_pin(AffinityKind kind)
    {
        var engine = Seed(kind);
        var decision = engine.Propose(AffinityRequest(kind), [Agent(1), Agent(2) with { WarmRuns = ImmutableHashSet.Create(Run) }]);
        Assert.Equal(Agent(kind == AffinityKind.None ? 2 : 1).Id, decision.Agent!.Id);
        if (kind != AffinityKind.None) Assert.Equal(PlacementReason.Affinity, decision.Reason);
    }

    [Fact]
    public void Same_machine_waits_for_the_reference_node_first_dispatch()
    {
        var decision = new PlacementEngine().Propose(AffinityRequest(AffinityKind.SameMachineAs), [Agent(1)]);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.WaitingForAffinity, decision.Reason);
    }

    [Theory]
    [InlineData(AffinityKind.Node)]
    [InlineData(AffinityKind.Run)]
    [InlineData(AffinityKind.SameMachineAs)]
    public void Platform_replacement_moves_pin_and_later_retries_adopt_the_already_moved_pin(AffinityKind kind)
    {
        var engine = Seed(kind);
        var request = AffinityRequest(kind);
        var agents = new[] { Agent(1) with { Healthy = false }, Agent(2), Agent(3) with { Draining = true } };
        Assert.Null(engine.Propose(request, agents).Agent);
        var retry = request with { Attempt = 2, RePlacement = true, PreviousAgentId = Agent(1).Id };
        var decision = engine.Propose(retry, agents);
        Assert.Equal(Agent(2).Id, decision.Agent!.Id);
        engine.Apply(decision);
        var next = engine.Propose(retry with { TaskKey = "next" },
            [Agent(2) with { SlotsUsed = 1 }, Agent(3) with { WarmRuns = ImmutableHashSet.Create(Run) }]);
        Assert.Equal(Agent(2).Id, next.Agent!.Id);
        var restored = new PlacementEngine(JsonSerializer.Deserialize<PlacementSnapshot>(JsonSerializer.Serialize(engine.Snapshot))!);
        Assert.Equal(Agent(2).Id, restored.Propose(request, [Agent(2), Agent(3)]).Agent!.Id);
    }

    [Fact]
    public void No_auto_retry_never_places_platform_retry_or_moves_pins()
    {
        var engine = Seed(AffinityKind.Run);
        var before = engine.Snapshot;
        var decision = engine.Propose(AffinityRequest(AffinityKind.Run) with
        {
            Attempt = 2, RePlacement = true, PreviousAgentId = Agent(1).Id, AutoRetry = false
        }, [Agent(2)]);
        Assert.Null(decision.Agent);
        Assert.Equal(PlacementReason.AutoRetryDisabled, decision.Reason);
        Assert.Equal(before, engine.Snapshot);
    }

    [Fact]
    public void Stale_and_foreign_decisions_are_rejected_and_run_cleanup_is_isolated()
    {
        var engine = new PlacementEngine();
        var otherRun = Guid.NewGuid();
        var first = engine.Propose(Request() with { Policy = new(Affinity: new(AffinityKind.Run)) }, [Agent(1)]);
        var stale = engine.Propose(Request(), [Agent(2)]);
        Assert.Throws<InvalidOperationException>(() => new PlacementEngine().Apply(first));
        engine.Apply(first);
        Assert.Throws<InvalidOperationException>(() => engine.Apply(stale));
        engine.Apply(engine.Propose(Request() with { RunId = otherRun }, [Agent(2)]));
        engine.ForgetRun(Run);
        Assert.DoesNotContain(Run, engine.Snapshot.RunPins.Keys);
        Assert.Single(engine.Snapshot.NodePins);
        Assert.StartsWith($"{otherRun:D}/", engine.Snapshot.NodePins.Single().Key);
    }

    [Fact]
    public void Invalid_requests_capacities_and_snapshots_are_not_silently_skipped()
    {
        var engine = new PlacementEngine();
        Assert.Throws<ArgumentException>(() => engine.Propose(Request() with { RunId = Guid.Empty }, [Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request() with { PackageSha256 = "bad" }, [Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request() with { RequiredRamMb = -1 }, [Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request() with { RePlacement = true }, [Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request() with
        {
            Policy = new(new PlacementSelector(pool: new(variable: "pool")))
        }, [Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request(), [Agent(1), Agent(1)]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request(), [Agent(1) with { AvailableRamMb = -1 }]));
        Assert.Throws<ArgumentException>(() => engine.Propose(Request(), [Agent(1) with { MaxTasks = 0 }]));
        Assert.Throws<ArgumentException>(() => new PlacementEngine(PlacementSnapshot.Empty with { Revision = -1 }));
        Assert.Throws<ArgumentException>(() => new PlacementEngine(PlacementSnapshot.Empty with
        {
            NodePins = ImmutableDictionary<string, Guid>.Empty.Add("bad-key", Agent(1).Id)
        }));
    }

    private static PlacementEngine Seed(AffinityKind kind)
    {
        var engine = new PlacementEngine();
        var seed = Request() with
        {
            NodeKey = kind == AffinityKind.SameMachineAs ? "reference" : "node",
            Policy = kind == AffinityKind.Run ? new(Affinity: new(AffinityKind.Run)) : null
        };
        engine.Apply(engine.Propose(seed, [Agent(1)]));
        return engine;
    }

    private static PlacementRequest AffinityRequest(AffinityKind kind) => Request() with
    {
        TaskKey = "next", Policy = new(Affinity: new(kind, kind == AffinityKind.SameMachineAs ? "reference" : null))
    };

    private static PlacementRequest Request() => new(Run, "node", "task", 1, Sha);
    private static PlacementAgent Agent(int number) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), $"agent-{number}", "linux", "ci", 0, 4, 1024);
}
