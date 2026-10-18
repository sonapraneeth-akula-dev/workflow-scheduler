using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wf.Agent.Sampling;

internal sealed unsafe partial class WindowsNetworkTrace : IDisposable
{
    private static readonly Guid NetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private readonly Guid markerProvider = Guid.NewGuid();
    private readonly string name = SessionName();
    private readonly ConcurrentDictionary<int, Subscription> processes = new();
    private readonly object controlGate = new();
    private readonly object flushGate = new();
    private readonly AutoResetEvent marker = new(false);
    private readonly nint loggerName;
    private readonly Properties* properties;
    private GCHandle context;
    private ulong session, consumerHandle = ulong.MaxValue, markerRegistration;
    private Task? consumer;
    private string? failure;
    private volatile bool disposed;

    private static string SessionName()
    {
        using var process = Process.GetCurrentProcess();
        return $"wf-resource-{process.Id}-{process.StartTime.ToUniversalTime().Ticks:x}-{Guid.NewGuid():N}";
    }

    public WindowsNetworkTrace()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Native ETW resource accounting requires a 64-bit process.");
        loggerName = Marshal.StringToHGlobalUni(name);
        properties = (Properties*)NativeMemory.AllocZeroed((nuint)(sizeof(Properties) + (name.Length + 1) * 2));
        properties->TotalSize = (uint)(sizeof(Properties) + (name.Length + 1) * 2);
        properties->SessionGuid = Guid.NewGuid();
        properties->ClientContext = 1;
        properties->WnodeFlags = 0x20000;
        properties->BufferSize = 64;
        properties->MinimumBuffers = 8;
        properties->MaximumBuffers = 64;
        properties->LogFileMode = 0x100;
        properties->FlushTimer = 1;
        properties->LoggerNameOffset = (uint)sizeof(Properties);
        name.AsSpan().CopyTo(new Span<char>((byte*)properties + sizeof(Properties), name.Length));
        try
        {
            Check(StartTrace(out var created, name, properties), "StartTrace native task-network session");
            session = created;
            context = GCHandle.Alloc(this);
            var logfile = new Logfile
            {
                LoggerName = loggerName, ProcessTraceMode = 0x100 | 0x1000 | 0x10000000,
                EventRecordCallback = &OnEvent, BufferCallback = &OnBuffer, Context = GCHandle.ToIntPtr(context)
            };
            consumerHandle = OpenTrace(&logfile);
            if (consumerHandle == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenTrace native task-network session failed.");
            consumer = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            EnableNetwork();
            Check(EventRegister(in markerProvider, 0, 0, out markerRegistration), "EventRegister private flush marker");
            Check(EnableTrace(session, in markerProvider, 1, 4, 0, 0, 5000, null), "EnableTrace private flush marker");
        }
        catch { Dispose(); throw; }
    }

    public Subscription Subscribe(Process process)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out var handle, 0, 0, 2) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Duplicate owned task process handle failed.");
        var subscription = new Subscription(this, process.Id, handle);
        if (!processes.TryAdd(process.Id, subscription))
        {
            subscription.Dispose();
            throw new InvalidOperationException("A task-network owner is already registered for this PID.");
        }
        return subscription;
    }

    private void EnableNetwork()
    {
        byte* filter = stackalloc byte[20];
        new Span<byte>(filter, 20).Clear();
        filter[0] = 1;
        *(ushort*)(filter + 2) = 8;
        ushort[] ids = [10, 11, 26, 27, 42, 43, 58, 59];
        for (var index = 0; index < ids.Length; index++) *(ushort*)(filter + 4 + index * 2) = ids[index];
        var descriptor = new FilterDescriptor { Pointer = (ulong)filter, Size = 20, Type = 0x80000200 };
        var parameters = new EnableParameters { Version = 2, Filter = &descriptor, Count = 1 };
        Check(EnableTrace(session, in NetworkProvider, 1, 4, 0x30, 0, 5000, &parameters), "EnableTrace Kernel-Network send/receive events");
    }

    private void Consume()
    {
        var handle = consumerHandle;
        var status = ProcessTrace(&handle, 1, null, null);
        if (!disposed) Interlocked.CompareExchange(ref failure, $"Native task-network ETW consumer stopped ({status}).", null);
    }

    private string? ReadFailure()
    {
        lock (controlGate)
        {
            if (disposed) return "Native task-network ETW session is closed.";
            var status = ControlTrace(session, name, properties, 0);
            if (status != 0) Interlocked.CompareExchange(ref failure, $"Native task-network ETW query failed ({status}).", null);
            if (properties->EventsLost != 0 || properties->LogBuffersLost != 0 || properties->RealTimeBuffersLost != 0)
                Interlocked.CompareExchange(ref failure, "Native task-network ETW events were lost; byte counts are unavailable.", null);
            return Volatile.Read(ref failure);
        }
    }

    private void Flush()
    {
        lock (flushGate)
        {
            uint status;
            lock (controlGate)
            {
                if (disposed) return;
                marker.Reset();
                var descriptor = new EventDescriptor { Id = 1, Level = 4 };
                status = EventWrite(markerRegistration, &descriptor, 0, null);
                if (status == 0) status = ControlTrace(session, name, properties, 3);
            }
            if (status != 0 || !marker.WaitOne(TimeSpan.FromSeconds(3)))
                Interlocked.CompareExchange(ref failure, $"Native task-network ETW final flush did not complete ({status}).", null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(EventRecord* record)
    {
        var owner = (WindowsNetworkTrace)GCHandle.FromIntPtr(record->Context).Target!;
        try
        {
            if (owner.disposed) return;
            if (record->Provider == owner.markerProvider) { owner.marker.Set(); return; }
            if (record->Provider != NetworkProvider || record->DataLength < 8) return;
            var pid = *(uint*)record->Data;
            if (!owner.processes.TryGetValue((int)pid, out var process)) return;
            if (record->Version != 0)
            {
                Interlocked.CompareExchange(ref owner.failure, "Unsupported owned-process Kernel-Network event schema.", null);
                return;
            }
            var sent = record->Id is 10 or 26 or 42 or 58;
            var received = record->Id is 11 or 27 or 43 or 59;
            // Read only the owned PID and byte count. Never inspect addresses, ports, or packet payload.
            if (sent || received) process.Add(record->Timestamp, *(uint*)((byte*)record->Data + 4), sent);
        }
        catch
        {
            Interlocked.CompareExchange(ref owner.failure, "Native owned-process network event could not be counted.", null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnBuffer(Logfile* logfile)
    {
        var owner = (WindowsNetworkTrace)GCHandle.FromIntPtr(logfile->Context).Target!;
        if (logfile->EventsLost != 0)
            Interlocked.CompareExchange(ref owner.failure, "Native task-network ETW events were lost; byte counts are unavailable.", null);
        return 1;
    }

    public void Dispose()
    {
        lock (flushGate)
        lock (controlGate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var subscription in processes.Values) subscription.Dispose();
            processes.Clear();
            if (session != 0) ControlTrace(session, name, properties, 1);
            if (consumerHandle != ulong.MaxValue) CloseTrace(consumerHandle);
            if (markerRegistration != 0) EventUnregister(markerRegistration);
        }
        if (consumer is not null && !consumer.Wait(TimeSpan.FromSeconds(5)))
        {
            _ = consumer.ContinueWith(_ => ReleaseMemory(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
        ReleaseMemory();
    }

    private void ReleaseMemory()
    {
        if (context.IsAllocated) context.Free();
        NativeMemory.Free(properties);
        Marshal.FreeHGlobal(loggerName);
        marker.Dispose();
    }

    private static void Check(uint status, string operation)
    {
        if (status != 0) throw new Win32Exception((int)status, $"{operation} failed ({status}); ETW session/provider permissions are required.");
    }

    internal sealed class Subscription(WindowsNetworkTrace owner, int pid, SafeProcessHandle process) : IDisposable
    {
        private readonly object gate = new();
        private long start = long.MaxValue, end = long.MaxValue;
        private ulong sent, received;
        private string? unavailable;
        private bool disposed;

        public void Begin()
        {
            lock (gate) { sent = received = 0; start = Stopwatch.GetTimestamp(); end = long.MaxValue; CheckAlive(); }
        }

        public void End()
        {
            lock (gate) { end = Stopwatch.GetTimestamp(); CheckAlive(); }
            owner.Flush();
        }

        public NetworkBytes? Read(out string? reason)
        {
            reason = owner.ReadFailure();
            lock (gate)
            {
                if (disposed) { reason ??= "Owned task-network subscription is closed."; return null; }
                if (end == long.MaxValue) CheckAlive();
                reason ??= unavailable;
                return reason is null ? new NetworkBytes(sent, received) : null;
            }
        }

        public void Add(long at, uint bytes, bool sending)
        {
            lock (gate)
            {
                if (disposed || at < start || at > end) return;
                CheckAlive();
                if (unavailable is not null) return;
                if (sending) sent = checked(sent + bytes);
                else received = checked(received + bytes);
            }
        }

        private void CheckAlive()
        {
            if (WaitForSingleObject(process, 0) != 0x102)
                unavailable = "Owned task process exited before the network window was drained; refusing possible PID reuse.";
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                owner.processes.TryRemove(new KeyValuePair<int, Subscription>(pid, this));
                process.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct Properties
    {
        [FieldOffset(0)] public uint TotalSize;
        [FieldOffset(24)] public Guid SessionGuid;
        [FieldOffset(40)] public uint ClientContext;
        [FieldOffset(44)] public uint WnodeFlags;
        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(96)] public uint LogBuffersLost;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct Logfile
    {
        [FieldOffset(8)] public nint LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(400)] public delegate* unmanaged[Stdcall]<Logfile*, uint> BufferCallback;
        [FieldOffset(416)] public uint EventsLost;
        [FieldOffset(424)] public delegate* unmanaged[Stdcall]<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public nint Context;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EventRecord
    {
        [FieldOffset(16)] public long Timestamp;
        [FieldOffset(24)] public Guid Provider;
        [FieldOffset(40)] public ushort Id;
        [FieldOffset(42)] public byte Version;
        [FieldOffset(86)] public ushort DataLength;
        [FieldOffset(96)] public void* Data;
        [FieldOffset(104)] public nint Context;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct EventDescriptor
    {
        [FieldOffset(0)] public ushort Id;
        [FieldOffset(4)] public byte Level;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterDescriptor { public ulong Pointer; public uint Size, Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EnableParameters
    {
        public uint Version, EnableProperty, ControlFlags;
        public Guid Source;
        public FilterDescriptor* Filter;
        public uint Count;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTrace(out ulong handle, string name, Properties* properties);
    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong handle, string name, Properties* properties, uint command);
    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
    private static partial uint EnableTrace(ulong handle, in Guid provider, uint control, byte level,
        ulong any, ulong all, uint timeout, EnableParameters* parameters);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static partial ulong OpenTrace(Logfile* logfile);
    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace")]
    private static partial uint ProcessTrace(ulong* handles, uint count, void* start, void* end);
    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
    private static partial uint CloseTrace(ulong handle);
    [LibraryImport("advapi32.dll", EntryPoint = "EventRegister")]
    private static partial uint EventRegister(in Guid provider, nint callback, nint context, out ulong handle);
    [LibraryImport("advapi32.dll", EntryPoint = "EventUnregister")]
    private static partial uint EventUnregister(ulong handle);
    [LibraryImport("advapi32.dll", EntryPoint = "EventWrite")]
    private static partial uint EventWrite(ulong handle, EventDescriptor* descriptor, uint count, void* data);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int DuplicateHandle(nint source, nint handle, nint target, out SafeProcessHandle duplicate,
        uint access, int inherit, uint options);
    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
