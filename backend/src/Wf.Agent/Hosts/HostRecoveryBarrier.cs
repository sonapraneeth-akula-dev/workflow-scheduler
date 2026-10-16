using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Wf.Agent.Hosts;

internal sealed partial class HostRecoveryBarrier
{
    private static readonly HashSet<string> held = new(StringComparer.Ordinal);
    private static readonly object sync = new();
    private readonly string directory;
    private readonly string boot;

    public HostRecoveryBarrier(string? directory)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "wf-agent", "host-lifetimes");
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Task-host containment requires a stable absolute per-user data directory.");
        this.directory = Path.GetFullPath(directory);
        TaskHostPool.CreatePrivateDirectory(this.directory);
        boot = BootIdentity();
    }

    public void AssertSafe(bool allowActive = true)
    {
        lock (sync) AssertSafeCore(allowActive);
    }

    private void AssertSafeCore(bool allowActive)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.pending"))
        {
            if (held.Contains(path))
            {
                if (allowActive) continue;
                throw new InvalidOperationException("Task-host reservation fencing is blocked by another active owned host lifetime.");
            }
            string recordedBoot;
            try
            {
                using var record = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(record, Encoding.UTF8, leaveOpen: true);
                recordedBoot = reader.ReadToEnd();
            }
            catch (FileNotFoundException) { continue; }
            catch (IOException error)
            { throw new InvalidOperationException("Task-host containment is quarantined: another agent owns an unfinished host lifetime.", error); }
            if (recordedBoot == boot)
                throw new InvalidOperationException(
                    "Task-host containment is quarantined after an unclean agent/host shutdown. " +
                    "No reservation fence can be acknowledged and no new work can start. " +
                    "Reboot this machine (not merely the agent) to guarantee orphan termination; do not delete lifetime records.");
            if (!Guid.TryParse(recordedBoot, out _))
                throw new InvalidDataException("Task-host containment recovery record is invalid; physical quiescence cannot be established.");
            File.Delete(path);
        }
    }

    public Lease Begin()
    {
        lock (sync)
        {
            AssertSafeCore(allowActive: true);
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.pending");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            try
            {
                stream.Write(Encoding.UTF8.GetBytes(boot));
                stream.Flush(flushToDisk: true);
                held.Add(path);
                return new Lease(path, stream);
            }
            catch { stream.Dispose(); throw; }
        }
    }

    internal sealed class Lease(string path, FileStream stream)
    {
        public void Complete()
        {
            lock (sync)
            {
                stream.Dispose();
                File.Delete(path);
                held.Remove(path);
            }
        }

        public void Abandon()
        {
            lock (sync)
            {
                stream.Dispose();
                held.Remove(path);
            }
        }
    }

    private static string BootIdentity()
    {
        if (OperatingSystem.IsLinux())
            return Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()).ToString("D");
        if (OperatingSystem.IsWindows())
        {
            var data = new byte[32];
            var result = NtQuerySystemInformation(90, data, data.Length, out _);
            if (result != 0) throw new Win32Exception($"Cannot establish Windows boot identity (NTSTATUS 0x{result:x8}).");
            return new Guid(data.AsSpan(0, 16)).ToString("D");
        }
        throw new PlatformNotSupportedException("Physical task-host containment requires Windows or Linux.");
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, byte[] data, int length, out int returned);
}
