using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Wf.Agent.Sampling;

internal sealed unsafe partial class LinuxBpfNetworkCounters : IDisposable
{
    private int map = -1, group = -1, ingress = -1, egress = -1;
    private bool ingressAttached, egressAttached;
    private static long SyscallNumber => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 321,
        Architecture.Arm64 => 280,
        _ => throw new PlatformNotSupportedException("Native BPF accounting supports Linux x64 and arm64.")
    };

    public LinuxBpfNetworkCounters(string cgroup)
    {
        try
        {
            group = Open(cgroup, 0x10000 | 0x80000);
            if (group < 0) throw Error("open owned cgroup");
            var attributes = new Attributes { MapType = 2, KeySize = 4, ValueSize = 8, MaxEntries = 2 };
            map = Invoke(0, &attributes, "BPF_MAP_CREATE (requires CAP_BPF and CAP_NET_ADMIN)");
            ingress = Load(0);
            egress = Load(1);
            Attach(ingress, 0);
            ingressAttached = true;
            Attach(egress, 1);
            egressAttached = true;
        }
        catch { Dispose(); throw; }
    }

    public static void RequireCgroupV2(string path)
    {
        byte* information = stackalloc byte[256];
        if (Statfs(path, information) != 0) throw Error("statfs delegated cgroup");
        if (*(long*)information != 0x63677270)
            throw new InvalidOperationException("Native task accounting requires a real cgroup-v2 filesystem, not ordinary files.");
    }

    public static void CreateOwnedCgroup(string path)
    {
        if (Mkdir(path, 0x1c0) != 0) throw Error("mkdir new private task cgroup");
    }

    public NetworkBytes Read() => new(Read(1), Read(0));

    private ulong Read(uint direction)
    {
        ulong value = 0;
        var attributes = new Attributes { MapFd = (uint)map, Key = (ulong)&direction, Value = (ulong)&value };
        Invoke(1, &attributes, "BPF_MAP_LOOKUP_ELEM");
        return value;
    }

    private int Load(int direction)
    {
        // Only skb->len is read. No packet data, addresses, ports, or payload are copied.
        Instruction* instructions = stackalloc Instruction[]
        {
            new(0xbf, 6, 1), new(0x62, 10, offset: -4, immediate: direction),
            new(0xbf, 2, 10), new(0x07, 2, immediate: -4),
            new(0x18, 1, 1, immediate: map), new(0),
            new(0x85, immediate: 1), new(0x15, offset: 2),
            new(0x61, 1, 6), new(0xdb, 0, 1), new(0xb7, immediate: 1), new(0x95)
        };
        byte* license = stackalloc byte[] { (byte)'G', (byte)'P', (byte)'L', 0 };
        byte* log = stackalloc byte[4096];
        new Span<byte>(log, 4096).Clear();
        var attributes = new Attributes
        {
            ProgramType = 8, InstructionCount = 12, Instructions = (ulong)instructions, License = (ulong)license,
            LogLevel = 1, LogSize = 4096, Log = (ulong)log, ExpectedAttachType = (uint)direction
        };
        var fd = (int)Bpf(SyscallNumber, 5, &attributes, (nuint)sizeof(Attributes));
        if (fd >= 0) return fd;
        var error = Marshal.GetLastPInvokeError();
        var length = new ReadOnlySpan<byte>(log, 4096).IndexOf((byte)0);
        var diagnostic = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(log, Math.Min(length < 0 ? 4096 : length, 512)));
        throw new Win32Exception(error, $"BPF_PROG_LOAD cgroup_skb failed ({error}); CAP_BPF/CAP_NET_ADMIN required. {diagnostic}");
    }

    private void Attach(int program, uint direction)
    {
        var attributes = new Attributes { TargetFd = (uint)group, AttachFd = (uint)program, AttachType = direction };
        Invoke(8, &attributes, "BPF_PROG_ATTACH owned cgroup");
    }

    private void Detach(int program, uint direction)
    {
        var attributes = new Attributes { TargetFd = (uint)group, AttachFd = (uint)program, AttachType = direction };
        if (Bpf(SyscallNumber, 9, &attributes, (nuint)sizeof(Attributes)) < 0)
            throw Error("BPF_PROG_DETACH owned cgroup");
    }

    public void Dispose()
    {
        Exception? failure = null;
        try { if (egressAttached) Detach(egress, 1); }
        catch (Win32Exception exception) { failure = exception; }
        try { if (ingressAttached) Detach(ingress, 0); }
        catch (Win32Exception exception) { failure ??= exception; }
        foreach (var fd in new[] { ingress, egress, map, group }) if (fd >= 0) Close(fd);
        ingress = egress = map = group = -1;
        ingressAttached = egressAttached = false;
        if (failure is not null) throw failure;
    }

    private static int Invoke(int command, Attributes* attributes, string operation)
    {
        var result = (int)Bpf(SyscallNumber, command, attributes, (nuint)sizeof(Attributes));
        if (result < 0) throw Error(operation);
        return result;
    }

    private static Win32Exception Error(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed ({error}).");
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Attributes
    {
        [FieldOffset(0)] public uint MapType;
        [FieldOffset(4)] public uint KeySize;
        [FieldOffset(8)] public uint ValueSize;
        [FieldOffset(12)] public uint MaxEntries;
        [FieldOffset(0)] public uint MapFd;
        [FieldOffset(8)] public ulong Key;
        [FieldOffset(16)] public ulong Value;
        [FieldOffset(0)] public uint ProgramType;
        [FieldOffset(4)] public uint InstructionCount;
        [FieldOffset(8)] public ulong Instructions;
        [FieldOffset(16)] public ulong License;
        [FieldOffset(24)] public uint LogLevel;
        [FieldOffset(28)] public uint LogSize;
        [FieldOffset(32)] public ulong Log;
        [FieldOffset(68)] public uint ExpectedAttachType;
        [FieldOffset(0)] public uint TargetFd;
        [FieldOffset(4)] public uint AttachFd;
        [FieldOffset(8)] public uint AttachType;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Instruction(byte code, byte destination = 0, byte source = 0, short offset = 0, int immediate = 0)
    {
        private readonly byte code = code;
        private readonly byte registers = (byte)(destination | (source << 4));
        private readonly short offset = offset;
        private readonly int immediate = immediate;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Bpf(long number, int command, Attributes* attributes, nuint size);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
    [LibraryImport("libc", EntryPoint = "statfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statfs(string path, void* information);
    [LibraryImport("libc", EntryPoint = "mkdir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Mkdir(string path, uint mode);
}
