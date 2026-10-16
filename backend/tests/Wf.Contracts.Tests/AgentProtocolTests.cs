using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Tests;

public sealed class AgentProtocolTests
{
    private static readonly AttemptRef Attempt = new()
    {
        RunId = "0199ea00-0000-7000-8000-000000000001", NodeKey = "build", TaskKey = "compile", Attempt = 2
    };

    public static IEnumerable<object[]> AgentMessages()
    {
        yield return [new AgentMessage
        {
            Sequence = 1,
            Hello = new Hello
            {
                Id = "agent-1", Name = "worker", Os = "linux", Arch = "x64", Cores = 8,
                RamMb = 16384, MaxTasks = 4, Labels = { ["dotnet"] = "10" }, Version = "1.0.0",
                Inflight = { Attempt }, ProtocolVersion = 1, Pool = "default"
            }
        }];
        yield return [new AgentMessage
        {
            Sequence = 2, Heartbeat = new Heartbeat
            {
                At = Timestamp.FromDateTime(DateTime.UnixEpoch), CpuPercent = 12.5,
                RamUsedMb = 250, DiskFreeMb = 32000, SlotsUsed = 1
            }
        }];
        yield return [new AgentMessage
        {
            Sequence = 3, TaskStarted = new TaskStarted { Task = Attempt, At = Timestamp.FromDateTime(DateTime.UnixEpoch) }
        }];
        yield return [new AgentMessage
        {
            Sequence = 4, TaskCompleted = new TaskCompleted
            {
                Task = Attempt, At = Timestamp.FromDateTime(DateTime.UnixEpoch), Outcome = TaskOutcome.Failed,
                OutputsJson = ByteString.CopyFromUtf8("""{"result":"partial"}"""),
                Error = new TaskError { Category = "ResourceLimit", Message = "Memory exceeded", NonRetryable = true },
                ResourceSummary = new ResourceSummary { PeakRamMb = 512, CpuTimeMs = 15, ElapsedMs = 100 }
            }
        }];
        yield return [new AgentMessage { Sequence = 5, Ack = new Ack { Sequence = 7, LeaderEpoch = 3 } }];
    }

    public static IEnumerable<object[]> ControlMessages()
    {
        yield return [new ControlMessage
        {
            Sequence = 1, LeaderEpoch = 3, Dispatch = new Dispatch
            {
                Task = Attempt, Version = "2.0.0", PackageUri = "https://localhost/packages/build.zip",
                Sha256 = new string('a', 64), DeadlineAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddHours(1)),
                ReservationEpoch = 4, InputsJson = ByteString.CopyFromUtf8("""{"source":{"sha":"abc"}}"""),
                VariablesJson = ByteString.CopyFromUtf8("""{"env":"dev"}"""),
                Limits = new ResourceLimits { CpuCores = 0.5, RamMb = 256 }, SecretRefs = { "deployToken" },
                Grants = { new UploadGrant
                {
                    Kind = "artifact", UploadUri = "https://localhost/uploads/grant", MaxBytes = 1024,
                    ExpiresAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddMinutes(15))
                } }
            }
        }];
        yield return [new ControlMessage
        {
            Sequence = 2, LeaderEpoch = 3, Cancel = new Cancel { Task = Attempt, GraceMs = 30000, Reason = "Aborted" }
        }];
        yield return [new ControlMessage
        {
            Sequence = 3, LeaderEpoch = 3,
            Drain = new Drain { DeadlineAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddMinutes(10)), Reason = "Maintenance" }
        }];
        yield return [new ControlMessage { Sequence = 4, LeaderEpoch = 3, Ack = new Ack { Sequence = 4 } }];
        yield return [new ControlMessage
        {
            Sequence = 5, LeaderEpoch = 3, NotLeader = new NotLeader { LeaderHint = "https://leader:7443" }
        }];
    }

    [Theory]
    [MemberData(nameof(AgentMessages))]
    public void Agent_payloads_round_trip(AgentMessage message)
    {
        Assert.Equal(message, AgentMessage.Parser.ParseFrom(message.ToByteArray()));
        Assert.NotEqual(AgentMessage.PayloadOneofCase.None, message.PayloadCase);
    }

    [Theory]
    [MemberData(nameof(ControlMessages))]
    public void Control_payloads_round_trip(ControlMessage message)
    {
        Assert.Equal(message, ControlMessage.Parser.ParseFrom(message.ToByteArray()));
        Assert.NotEqual(ControlMessage.PayloadOneofCase.None, message.PayloadCase);
    }

    [Fact]
    public void Report_header_capability_round_trips_without_changing_legacy_grants()
    {
        var report = new UploadGrant
        {
            Kind = "reports", UploadUri = "https://localhost/api/v1/report-publishes",
            BearerToken = "test-only-capability", MaxBytes = 10_000_000
        };
        var restored = UploadGrant.Parser.ParseFrom(report.ToByteArray());
        Assert.Equal(report, restored);
        Assert.DoesNotContain(restored.BearerToken, restored.UploadUri, StringComparison.Ordinal);
        Assert.Equal(5, UploadGrant.Descriptor.FindFieldByName("bearer_token").FieldNumber);
        var legacy = new UploadGrant { Kind = "logs", UploadUri = "/logs/grant" }.ToByteArray();
        var old = UploadGrant.Parser.ParseFrom(legacy);
        Assert.Empty(old.BearerToken);
        Assert.Equal(legacy, old.ToByteArray());
    }

    [Fact]
    public void Payload_replacement_clears_previous_variant()
    {
        var message = new ControlMessage { Dispatch = new Dispatch(), Cancel = new Cancel() };
        Assert.Null(message.Dispatch);
        Assert.Equal(ControlMessage.PayloadOneofCase.Cancel, message.PayloadCase);
    }

    [Fact]
    public void Unknown_fields_survive_forwarding()
    {
        var bytes = new byte[] { 8, 1, 0xa0, 6, 42 };
        Assert.Equal(bytes, AgentMessage.Parser.ParseFrom(bytes).ToByteArray());
    }

    [Fact]
    public void Truncated_payload_is_rejected()
    {
        Assert.Throws<InvalidProtocolBufferException>(() => AgentMessage.Parser.ParseFrom(new byte[] { 82, 10, 1 }));
    }

    [Fact]
    public void Connect_is_bidirectional_and_keeps_its_versioned_wire_name()
    {
        var service = AgentReflection.Descriptor.Services.Single();
        Assert.Equal("wf.agent.v1.AgentGateway", service.FullName);
        var method = service.Methods.Single();
        Assert.Equal("Connect", method.Name);
        Assert.True(method.IsClientStreaming);
        Assert.True(method.IsServerStreaming);
        Assert.Equal(AgentMessage.Descriptor, method.InputType);
        Assert.Equal(ControlMessage.Descriptor, method.OutputType);
    }
}
