using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Wf.Agent.Sampling;

public sealed unsafe partial class WindowsResourceSampler(NativeSamplingOptions? options = null) : ResourceSampler(options)
{
    private readonly Lazy<WindowsNetworkTrace> trace = new(() => new WindowsNetworkTrace());
    protected override ITaskResourceScope CreateScope() => new WindowsTaskResourceScope(() => trace.Value);
    public override void Dispose() { if (trace.IsValueCreated) trace.Value.Dispose(); }

    public override ResourceCounters ReadMachine()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows counters require Windows.");
        var (total, available) = AgentTelemetry.Memory();
        var (idle, cpu) = AgentTelemetry.Cpu();
        var network = ReadMachineNetwork(out var networkUnavailable);
        // GetSystemTimes covers only the calling processor group on machines with multiple groups.
        var cpuUnavailable = GetActiveProcessorGroupCount() > 1
            ? "Machine CPU accounting across multiple Windows processor groups is unavailable." : null;
        return new ResourceCounters(cpu - idle, cpu, total - available, network, networkUnavailable, cpuUnavailable,
            CpuScope: "machine", MemoryScope: "physical-used", NetworkScope: "non-loopback-interfaces");
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();

    private static IReadOnlyDictionary<string, NetworkBytes>? ReadMachineNetwork(out string? unavailable)
    {
        unavailable = null;
        InterfaceTable* table = null;
        try
        {
            var status = GetIfTable(out table);
            if (status != 0)
            {
                unavailable = new Win32Exception((int)status).Message;
                return null;
            }
            var result = new Dictionary<string, NetworkBytes>();
            var rows = &table->First;
            for (uint index = 0; index < table->Count; index++)
            {
                ref var row = ref rows[index];
                if (row.Type == 24) continue; // IF_TYPE_SOFTWARE_LOOPBACK
                result.Add(row.Luid.ToString("x", CultureInfo.InvariantCulture), new(row.OutOctets, row.InOctets));
            }
            if (result.Count != 0) return result;
            unavailable = "No non-loopback network interfaces are visible.";
            return null;
        }
        finally { if (table is not null) FreeMibTable(table); }
    }

    // MIB_IF_TABLE2/MIB_IF_ROW2: one native 64-bit counter snapshot, without IP/DNS metadata or per-adapter queries.
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceTable { public uint Count; public InterfaceRow First; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceRow
    {
        public ulong Luid;
        public uint Index;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed, InOctets;
        public fixed ulong InStatistics[8];
        public ulong OutOctets;
        public fixed ulong OutStatistics[8];
    }
    [LibraryImport("iphlpapi.dll", EntryPoint = "GetIfTable2")]
    private static partial uint GetIfTable(out InterfaceTable* table);
    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(void* table);
}
