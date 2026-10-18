using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Sampling;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Resources;

namespace Wf.Benchmarks;

// Real-time paired experiment, not a throughput microbenchmark: never compress the 1 Hz cadence.
public static class SamplerOverheadBenchmarks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public sealed record Options(string Directory, int Pairs = 5, int Seconds = 30, int WarmupSeconds = 10,
        int Concurrency = 1, string? CgroupRoot = null);
    public sealed record Distribution(double Minimum, double Median, double P95, double Maximum);
    public sealed record WorkloadResult(long Cycles, long SentBytes, long ReceivedBytes,
        double WallSeconds, string[] Privileges);
    public sealed record Measurement(bool Enabled, int ProcessId, long ProcessStartUtcTicks, double CpuSeconds,
        double WallSeconds, double OneCoreCpuPercent, int Samples, long ProtobufBytes,
        double? MinimumIntervalSeconds, double? MaximumIntervalSeconds, string ObservationsFile,
        WorkloadResult[] WorkloadResults, double WarmupCpuSeconds, double WarmupWallSeconds,
        double JitCompilationSeconds, long JitCompiledMethods);
    public sealed record Pair(int Number, string Order, Measurement Baseline, Measurement Enabled,
        double AddedCpuPercentagePoints);

    public static double OneCoreCpuPercent(double cpuSeconds, double wallSeconds)
    {
        if (!double.IsFinite(cpuSeconds) || cpuSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(cpuSeconds), "CPU duration must be finite and nonnegative.");
        if (!double.IsFinite(wallSeconds) || wallSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(wallSeconds), "Monotonic wall duration must be finite and positive.");
        var percent = 100 * cpuSeconds / wallSeconds;
        return double.IsFinite(percent) ? percent : throw new ArgumentOutOfRangeException(nameof(cpuSeconds));
    }

    public static Distribution Summarize(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0 || ordered.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("A distribution requires finite observations.", nameof(values));
        return new(ordered[0], (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }

    public static bool WithinLimit(IEnumerable<double> addedCpuPercentagePoints)
    {
        var values = addedCpuPercentagePoints.ToArray();
        if (values.Length < 3 || values.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("At least three finite paired CPU deltas are required.", nameof(addedCpuPercentagePoints));
        return values.All(value => value <= 1);
    }

    public static void ValidateOptions(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory) || options.Pairs < 3 || options.Seconds < 10 ||
            options.WarmupSeconds < 5 || options.Concurrency is < 1 or > 16)
            throw new ArgumentException("Require output directory, >=3 pairs, >=10 measured seconds, >=5 warmup seconds, and 1..16 tasks.");
        if (OperatingSystem.IsLinux() && (options.CgroupRoot is null || !Path.IsPathFullyQualified(options.CgroupRoot)))
            throw new ArgumentException("Linux requires --cgroup-root with an absolute, isolated delegated cgroup-v2 root.");
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine("Usage: --sampler-overhead-bench <directory> [--pairs 5 --seconds 30 --warmup 10 --concurrency 1 --cgroup-root <path>]");
            return 2;
        }
        var pairs = new List<Pair>();
        Measurement? globalWarmup = null;
        Options? options = null;
        string? error = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            options = Parse(arguments);
            ValidateOptions(options);
            System.IO.Directory.CreateDirectory(options.Directory);
            await File.WriteAllTextAsync(Path.Combine(options.Directory, "privileges.txt"), await PrivilegesAsync());
            Console.WriteLine("Global native warmup: 90 real seconds at 1 Hz, then 10-second validation; all CPU retained separately.");
            globalWarmup = await MeasureAsync(options with { WarmupSeconds = 90, Seconds = 10 }, 0, true);
            for (var index = 0; index < options.Pairs; index++)
            {
                var enabledFirst = index % 2 == 1;
                var first = await MeasureAsync(options, index + 1, enabledFirst);
                var second = await MeasureAsync(options, index + 1, !enabledFirst);
                var baseline = enabledFirst ? second : first;
                var enabled = enabledFirst ? first : second;
                var pair = new Pair(index + 1, enabledFirst ? "enabled,disabled" : "disabled,enabled",
                    baseline, enabled, enabled.OneCoreCpuPercent - baseline.OneCoreCpuPercent);
                pairs.Add(pair);
                Console.WriteLine($"Pair {pair.Number}: baseline={baseline.OneCoreCpuPercent:F6}%, enabled={enabled.OneCoreCpuPercent:F6}%, added={pair.AddedCpuPercentagePoints:F6} pp");
            }
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Console.Error.WriteLine(error);
        }
        var passed = error is null && options is not null && pairs.Count == options.Pairs &&
            WithinLimit(pairs.Select(pair => pair.AddedCpuPercentagePoints));
        var report = new
        {
            SchemaVersion = 1, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow,
            Platform = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
            RuntimeOptions = new
            {
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                TieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                ReadyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun")
            },
            BenchmarkAssemblySha256 = AssemblyHash(typeof(SamplerOverheadBenchmarks).Assembly),
            SamplerAssemblySha256 = AssemblyHash(typeof(ResourceSampler).Assembly),
            AgentConfiguration = "Managed representative agent fixture; unmodified production ResourceSampler + ResourceSamplingService",
            Scope = "Steady-state agent-process CPU: native task CPU/RAM/network + machine sampling, JSON materialization/validation, AgentMessage and protobuf serialization",
            Excluded = "Task child CPU; startup/teardown and separately reported global warmup; AgentOutbox/ResultJournal; gRPC transport; server/storage/UI; kernel work charged outside this process; NativeAOT and installed-service identities",
            CpuFormula = "100 * delta(agent TotalProcessorTime seconds) / monotonic wall seconds; one core = 100%; NOT divided by logical processors",
            Workload = "Each task: 64 MiB touched allocation, 10 ms wall-clock CPU spin per paced 50 ms cycle, one 1024-byte UDP send and receive to its own loopback socket per cycle (20/s), no external traffic",
            IntervalSeconds = ResourceSamplingService.DefaultInterval.TotalSeconds,
            MaximumSupportedIntervalSeconds = ResourceSamplingService.MaximumInterval.TotalSeconds,
            GlobalWarmup = globalWarmup, Options = options, Pairs = pairs,
            BaselineCpuPercent = pairs.Count == 0 ? null : Summarize(pairs.Select(pair => pair.Baseline.OneCoreCpuPercent)),
            EnabledCpuPercent = pairs.Count == 0 ? null : Summarize(pairs.Select(pair => pair.Enabled.OneCoreCpuPercent)),
            AddedCpuPercentagePoints = pairs.Count == 0 ? null : Summarize(pairs.Select(pair => pair.AddedCpuPercentagePoints)),
            Gate = "Every paired signed enabled-minus-disabled CPU percentage-point delta <= 1; no clipping negative deltas",
            Passed = passed, Error = error
        };
        var output = Path.GetFullPath(arguments[0]);
        System.IO.Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "sampler-overhead.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine(passed ? "PASS: every paired agent CPU delta <= 1 one-core percentage point." : "FAIL: overhead gate or native sampling validation failed; see report.");
        return passed ? 0 : 1;
    }

    private static Options Parse(string[] arguments)
    {
        var options = new Options(Path.GetFullPath(arguments[0]));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length || !seen.Add(arguments[index]))
                throw new ArgumentException("Each option needs exactly one value; duplicate options are rejected.");
            var value = arguments[index + 1];
            options = arguments[index] switch
            {
                "--pairs" => options with { Pairs = int.Parse(value, CultureInfo.InvariantCulture) },
                "--seconds" => options with { Seconds = int.Parse(value, CultureInfo.InvariantCulture) },
                "--warmup" => options with { WarmupSeconds = int.Parse(value, CultureInfo.InvariantCulture) },
                "--concurrency" => options with { Concurrency = int.Parse(value, CultureInfo.InvariantCulture) },
                "--cgroup-root" => options with { CgroupRoot = value },
                _ => throw new ArgumentException($"Unknown option {arguments[index]}.")
            };
        }
        return options;
    }

    private static async Task<Measurement> MeasureAsync(Options options, int pair, bool enabled)
    {
        var label = pair == 0 ? "global-warmup" : $"pair-{pair:00}-{(enabled ? "enabled" : "disabled")}";
        using var sampler = ResourceSampler.Create(new NativeSamplingOptions(enabled, options.CgroupRoot));
        using var service = new ResourceSamplingService(sampler, TimeProvider.System,
            NullLogger<ResourceSamplingService>.Instance);
        var children = new List<(Process Process, ITaskResourceScope? Scope, IDisposable? Tracking)>();
        var messages = new List<(long At, byte[] Bytes)>();
        var results = new List<WorkloadResult>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.WarmupSeconds + options.Seconds + 60));
        try
        {
            if (enabled) await service.StartAsync(timeout.Token);
            for (var index = 0; index < options.Concurrency; index++)
            {
                var scope = enabled ? sampler.TryCreateScope(out var reason) ??
                    throw new InvalidOperationException($"No native scope: {reason}") : null;
                var info = WorkloadStart();
                if (scope is not null) scope.PrepareStart(info);
                else if (OperatingSystem.IsLinux()) PrepareUnscopedLinuxStart(info);
                var child = new Process { StartInfo = info };
                try { child.Start(); }
                catch { child.Dispose(); scope?.Dispose(); throw; }
                children.Add((child, scope, null));
                scope?.Bind(child);
                if (await child.StandardOutput.ReadLineAsync(timeout.Token) != "ready")
                    throw new InvalidOperationException($"Workload did not become ready: {await child.StandardError.ReadToEndAsync(timeout.Token)}");
                if (enabled)
                {
                    var tracking = service.Track(child, new AttemptRef
                    {
                        RunId = "sampler-overhead", NodeKey = "work", TaskKey = $"task-{index}", Attempt = 1
                    }, scope, publish: message =>
                    {
                        // Include real protobuf encoding; retain bytes for validation outside the timed window.
                        var bytes = message.ToByteArray();
                        lock (messages) messages.Add((Stopwatch.GetTimestamp(), bytes));
                    }, reservationEpoch: 1);
                    children[^1] = (child, scope, tracking);
                }
            }
            using var agent = Process.GetCurrentProcess();
            agent.Refresh();
            var warmupCpu = agent.TotalProcessorTime;
            var warmupStarted = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), timeout.Token);
            var warmupWall = Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds;
            agent.Refresh();
            var warmupCpuSeconds = (agent.TotalProcessorTime - warmupCpu).TotalSeconds;
            var jitBefore = JitInfo.GetCompilationTime();
            var methodsBefore = JitInfo.GetCompiledMethodCount();
            agent.Refresh();
            var cpuBefore = agent.TotalProcessorTime;
            var before = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(options.Seconds), timeout.Token);
            var after = Stopwatch.GetTimestamp();
            agent.Refresh();
            var cpu = (agent.TotalProcessorTime - cpuBefore).TotalSeconds;
            var wall = Stopwatch.GetElapsedTime(before, after).TotalSeconds;
            var jitSeconds = (JitInfo.GetCompilationTime() - jitBefore).TotalSeconds;
            var jitMethods = JitInfo.GetCompiledMethodCount() - methodsBefore;
            await File.WriteAllTextAsync(Path.Combine(options.Directory, label + "-process-cpu.json"),
                JsonSerializer.Serialize(new
                {
                    CpuSeconds = cpu, WallSeconds = wall, OneCoreCpuPercent = OneCoreCpuPercent(cpu, wall),
                    WarmupCpuSeconds = warmupCpuSeconds, WarmupWallSeconds = warmupWall,
                    JitCompilationSeconds = jitSeconds, JitCompiledMethods = jitMethods
                }, Json));
            await service.StopAsync(timeout.Token);
            foreach (var child in children) child.Tracking?.Dispose();
            foreach (var child in children)
            {
                await child.Process.StandardInput.WriteLineAsync("stop");
                await child.Process.WaitForExitAsync(timeout.Token);
                var result = await child.Process.StandardOutput.ReadToEndAsync(timeout.Token);
                if (child.Process.ExitCode != 0)
                    throw new InvalidOperationException($"Workload failed: {await child.Process.StandardError.ReadToEndAsync(timeout.Token)}");
                var workload = JsonSerializer.Deserialize<WorkloadResult>(result) ??
                    throw new InvalidDataException("Missing workload results.");
                ValidateWorkload(workload, options.WarmupSeconds + options.Seconds);
                results.Add(workload);
            }
            var measured = messages.Where(message => message.At >= before && message.At <= after)
                .Select(message => (Bytes: message.Bytes, Message: AgentMessage.Parser.ParseFrom(message.Bytes))).ToArray();
            var observations = measured.Select(message => new
            {
                message.Message.ResourceSample.Task.TaskKey, message.Message.ResourceSample.SampleSequence,
                Observation = ResourceContract.Parse(message.Message.ResourceSample.SampleJson.Memory)
            }).ToArray();
            var file = Path.Combine(options.Directory, label + "-observations.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(observations, Json));
            foreach (var observation in observations)
                ValidateObservation(observation.Observation);
            if (enabled && (observations.Length < (options.Seconds - 2) * options.Concurrency ||
                observations.Select(observation => observation.TaskKey).Distinct().Count() != options.Concurrency ||
                observations.GroupBy(observation => observation.TaskKey).Any(group =>
                    group.All(observation => observation.Observation.Task.CpuPercent == 0) ||
                    group.Sum(observation => (double)observation.Observation.Task.NetworkSentDeltaBytes!.Value) == 0 ||
                    group.Sum(observation => (double)observation.Observation.Task.NetworkReceivedDeltaBytes!.Value) == 0)))
                throw new InvalidDataException("Missing actual 1 Hz task samples; an incomplete observation cannot pass.");
            var intervals = observations.Select(observation => observation.Observation.Task.IntervalSeconds!.Value).ToArray();
            var measurement = new Measurement(enabled, agent.Id, agent.StartTime.ToUniversalTime().Ticks, cpu, wall,
                OneCoreCpuPercent(cpu, wall), observations.Length, measured.Sum(message => (long)message.Bytes.Length),
                intervals.Length == 0 ? null : intervals.Min(), intervals.Length == 0 ? null : intervals.Max(),
                Path.GetFileName(file), results.ToArray(), warmupCpuSeconds, warmupWall, jitSeconds, jitMethods);
            await File.WriteAllTextAsync(Path.Combine(options.Directory, label + ".json"), JsonSerializer.Serialize(measurement, Json));
            return measurement;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            foreach (var child in children)
            {
                child.Tracking?.Dispose();
                if (!child.Process.HasExited)
                {
                    child.Process.Kill(entireProcessTree: true);
                    await child.Process.WaitForExitAsync(CancellationToken.None);
                }
                child.Process.Dispose();
                child.Scope?.Dispose();
            }
        }
    }

    public static void ValidateObservation(ResourceObservation observation)
    {
        static bool Complete(ResourcePoint point) =>
            point.CpuPercent is { } cpu && double.IsFinite(cpu) && cpu >= 0 &&
            point.RamBytes is > 0 && point.IntervalSeconds is >= .5 and <= 5 &&
            point.NetworkSentBytes is not null && point.NetworkReceivedBytes is not null &&
            point.NetworkSentDeltaBytes is not null && point.NetworkReceivedDeltaBytes is not null &&
            point.NetworkSentBytesPerSecond is { } sent && double.IsFinite(sent) && sent >= 0 &&
            point.NetworkReceivedBytesPerSecond is { } received && double.IsFinite(received) && received >= 0 &&
            point.CpuUnavailableReason is null && point.MemoryUnavailableReason is null &&
            point.NetworkUnavailableReason is null && point.NetworkRateUnavailableReason is null;
        if (!Complete(observation.Task) || observation.Machine is null || !Complete(observation.Machine) ||
            observation.Task.CpuScope is not ("job-with-descendants" or "cgroup-with-descendants") ||
            observation.Task.NetworkScope is not ("host-process-etw-transport" or "cgroup-skb-l3-with-descendants"))
            throw new InvalidDataException("Incomplete native task/machine CPU, RAM, network or cadence; no fallback allowed.");
    }

    public static void ValidateWorkload(WorkloadResult result, double minimumSeconds)
    {
        if (!double.IsFinite(result.WallSeconds) || result.WallSeconds < minimumSeconds ||
            result.Cycles < minimumSeconds * 19 || result.Cycles > result.WallSeconds * 21 ||
            result.SentBytes != result.Cycles * 1024 || result.ReceivedBytes != result.SentBytes)
            throw new InvalidDataException("Representative workload did not execute its paced CPU/network cycles.");
    }

    private static ProcessStartInfo WorkloadStart()
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--sampler-overhead-workload" })
            info.ArgumentList.Add(argument);
        return info;
    }

    private static void PrepareUnscopedLinuxStart(ProcessStartInfo info)
    {
        var arguments = new[] { "--inh-caps=-all", "--ambient-caps=-all", "--no-new-privs", "--", info.FileName }
            .Concat(info.ArgumentList).ToArray();
        info.FileName = "/usr/bin/setpriv";
        info.ArgumentList.Clear();
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    public static async Task<int> RunWorkloadAsync()
    {
        var memory = new byte[64 * 1024 * 1024];
        for (var index = 0; index < memory.Length; index += 4096) memory[index] = 1;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Connect(socket.LocalEndPoint!);
        var packet = new byte[1024];
        Console.WriteLine("ready");
        // Console's synchronized reader can block synchronously even for ReadLineAsync.
        var stop = Task.Run(Console.ReadLine);
        long cycles = 0;
        var start = Stopwatch.GetTimestamp();
        while (!stop.IsCompleted)
        {
            var spin = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(spin).TotalMilliseconds < 10) Thread.SpinWait(100);
            if (await socket.SendAsync(packet, SocketFlags.None) != packet.Length ||
                await socket.ReceiveAsync(packet, SocketFlags.None) != packet.Length)
                throw new IOException("Incomplete owned loopback UDP datagram.");
            cycles++;
            var remaining = TimeSpan.FromMilliseconds(cycles * 50) - Stopwatch.GetElapsedTime(start);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        GC.KeepAlive(memory);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Cycles = cycles, SentBytes = cycles * packet.Length, ReceivedBytes = cycles * packet.Length,
            WallSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds,
            Privileges = OperatingSystem.IsLinux() ? File.ReadLines("/proc/self/status").Where(line =>
                line.StartsWith("CapEff:", StringComparison.Ordinal) || line.StartsWith("NoNewPrivs:", StringComparison.Ordinal)).ToArray() : []
        }));
        return 0;
    }

    private static async Task<string> PrivilegesAsync()
    {
        if (OperatingSystem.IsLinux())
            return string.Join(Environment.NewLine, File.ReadLines("/proc/self/status").Where(line =>
                line.StartsWith("Uid:", StringComparison.Ordinal) || line.StartsWith("Cap", StringComparison.Ordinal) ||
                line.StartsWith("NoNewPrivs:", StringComparison.Ordinal)));
        using var process = Process.Start(new ProcessStartInfo("whoami", "/priv")
            { UseShellExecute = false, RedirectStandardOutput = true })!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? text : throw new InvalidOperationException("Cannot record current Windows token privileges.");
    }

    private static string AssemblyHash(Assembly assembly)
    {
        using var file = File.OpenRead(assembly.Location);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
