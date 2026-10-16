using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using Wf.Agent.Hosts;
using Wf.Contracts.Definition;

namespace Wf.Agent.Secrets;

/// <summary>Windows: CurrentUser DPAPI + private ACL. Unix: private 0700 directory and 0600 files.</summary>
public sealed partial class LocalSecretStore : ISecretStore
{
    public const int MaxSecretBytes = 25 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Entropy = "wf-agent/local-secrets/v1"u8.ToArray();
    private readonly string directory;

    public LocalSecretStore(string directory)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Local secret protection supports Windows DPAPI and Linux private files.");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.GetPathRoot(directory) == Path.TrimEndingDirectorySeparator(directory))
            throw new ArgumentException("Secrets:LocalDirectory must be an absolute, non-root private directory.");
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RejectLinks();
        if (!Directory.Exists(this.directory)) TaskHostPool.CreatePrivateDirectory(this.directory);
        ValidateProtection(this.directory, isDirectory: true);
    }

    public async ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var path = SecretPath(reference);
        byte[]? stored = null, clear = null;
        try
        {
            RejectLinks();
            ValidateProtection(directory, isDirectory: true);
            ValidateProtection(path, isDirectory: false);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > MaxSecretBytes + 1024) throw new SecretResolutionException(reference);
            stored = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(stored, cancellationToken);
            clear = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser) : stored;
            if (clear.Length is <= 0 or > MaxSecretBytes) throw new SecretResolutionException(reference);
            var value = Utf8.GetString(clear);
            if (value.Contains('\0')) throw new SecretResolutionException(reference);
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            CryptographicException or DecoderFallbackException)
        { throw new SecretResolutionException(reference); }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (stored is not null) CryptographicOperations.ZeroMemory(stored);
        }
    }

    /// <summary>Provision as the agent's OS user. Replaces atomically; never relaxes existing permissions.</summary>
    public async Task WriteAsync(SecretReference reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Contains('\0') || Utf8.GetByteCount(value) > MaxSecretBytes)
            throw new ArgumentException("Secret must contain 1–25600 UTF-8 bytes without NUL.");
        cancellationToken.ThrowIfCancellationRequested();
        var path = SecretPath(reference);
        RejectLinks();
        ValidateProtection(directory, isDirectory: true);
        if (File.Exists(path)) ValidateProtection(path, isDirectory: false);
        var staging = Path.Combine(directory, $".{Guid.NewGuid():N}.secret");
        var clear = Utf8.GetBytes(value);
        byte[]? stored = null;
        try
        {
            stored = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser) : clear;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(staging, options))
            {
                await output.WriteAsync(stored, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var user = identity.User ?? throw new UnauthorizedAccessException("Agent has no Windows user identity.");
                var security = new FileSecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(staging).SetAccessControl(security);
            }
            ValidateProtection(staging, isDirectory: false);
            RejectLinks();
            ValidateProtection(directory, isDirectory: true);
            if (File.Exists(path)) ValidateProtection(path, isDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (stored is not null) CryptographicOperations.ZeroMemory(stored);
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private string SecretPath(SecretReference reference)
    {
        if (reference.Store != "local") throw new SecretResolutionException(reference);
        // A fixed flat filename preserves safe legacy slash-separated names without creating path components.
        return Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(reference.SecretName))) + ".secret");
    }

    private void RejectLinks()
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Secret directory cannot contain symbolic links or reparse points.");
    }

    private static void ValidateProtection(string path, bool isDirectory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != isDirectory)
            throw new UnauthorizedAccessException("Secret storage must be a regular private file or directory.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (!Equals(security.GetOwner(typeof(SecurityIdentifier)), identity.User))
                throw new UnauthorizedAccessException("Secret storage must be owned by the agent user.");
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (rules.Count == 0)
                throw new UnauthorizedAccessException("Secret storage permissions must grant access only to the agent user.");
            foreach (FileSystemAccessRule rule in rules)
                if (rule.AccessControlType != AccessControlType.Allow || !Equals(rule.IdentityReference, identity.User))
                    throw new UnauthorizedAccessException("Secret storage permissions must grant access only to the agent user.");
        }
        else if (OperatingSystem.IsLinux())
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (isDirectory ? UnixFileMode.UserExecute : 0);
            // statx has an architecture-independent ABI; also reject foreign owners and non-regular files (e.g. FIFOs).
            if (Statx(-100, path, 0x100, 0x0b, out var status) != 0 || (status.Mask & 0x0b) != 0x0b ||
                status.UserId != GetEffectiveUserId() || (status.Mode & 0xf000) != (isDirectory ? 0x4000 : 0x8000) ||
                File.GetUnixFileMode(path) != expected)
                throw new UnauthorizedAccessException("Secret storage requires agent ownership, 0700 directory and 0600 regular files.");
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxFileStatus status);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
