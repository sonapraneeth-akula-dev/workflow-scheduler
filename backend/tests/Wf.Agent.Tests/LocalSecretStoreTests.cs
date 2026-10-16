using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;
using Wf.Agent.Secrets;
using Wf.Contracts.Definition;

namespace Wf.Agent.Tests;

public sealed class LocalSecretStoreTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".agent-artifacts", "secret-stores-3adec91f",
        "tests", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("token")]
    [InlineData("secret://local/service/token")]
    [InlineData("legacy/safe.name")]
    public async Task Protected_lifecycle_survives_reopen_replaces_atomically_and_does_not_change_reference(string name)
    {
        var reference = new SecretReference(name);
        var store = new LocalSecretStore(directory);
        const string value = "clé-🔑-confidential-value\nsecond-line";
        await store.WriteAsync(reference, value, Token);
        Assert.Equal(value, await new LocalSecretStore(directory).ResolveAsync(reference, Token));
        var file = Assert.Single(Directory.GetFiles(directory));
        Assert.EndsWith(".secret", file);
        Assert.DoesNotContain(reference.SecretName, Path.GetFileName(file));
        var bytes = await File.ReadAllBytesAsync(file, Token);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(value, Encoding.UTF8.GetString(bytes));
            Assert.Throws<CryptographicException>(() =>
            {
                if (OperatingSystem.IsWindows()) ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            });
            using var identity = WindowsIdentity.GetCurrent();
            var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules) Assert.Equal(identity.User, rule.IdentityReference);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        await store.WriteAsync(reference, "replacement", Token);
        Assert.Equal("replacement", await store.ResolveAsync(reference, Token));
        Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(name, reference.Name);
    }

    [Fact]
    public async Task Legacy_and_local_qualified_names_share_one_local_secret_without_alias_fallback()
    {
        var store = new LocalSecretStore(directory);
        await store.WriteAsync(new("service/token"), "value", Token);
        Assert.Equal("value", await store.ResolveAsync(new("secret://local/service/token"), Token));
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.ResolveAsync(new("secret://cloud/service/token"), Token));
        Assert.Equal("Secret 'secret://cloud/service/token' is missing or unavailable.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Missing_corrupt_and_plaintext_DPAPI_files_fail_without_echo_or_inner_exception()
    {
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("missing");
        Assert.Null((await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.ResolveAsync(reference, Token))).InnerException);
        await store.WriteAsync(reference, "secret-text-never-in-error", Token);
        var file = Assert.Single(Directory.GetFiles(directory));
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllBytesAsync(file, "secret-text-never-in-error"u8.ToArray(), Token);
            var error = await Assert.ThrowsAsync<SecretResolutionException>(async () => await store.ResolveAsync(reference, Token));
            Assert.DoesNotContain("secret-text-never-in-error", error.ToString());
            Assert.Null(error.InnerException);
        }
        await File.WriteAllBytesAsync(file, [], Token);
        await Assert.ThrowsAsync<SecretResolutionException>(async () => await store.ResolveAsync(reference, Token));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("secret://local/../escape")]
    [InlineData("secret://local/%2e%2e/escape")]
    [InlineData("secret://local/a\\b")]
    [InlineData("C:\\escape")]
    [InlineData("secret://local//absolute")]
    [InlineData("secret://local/a:stream")]
    public void Traversal_and_invalid_references_use_the_existing_model(string name)
    {
        _ = new LocalSecretStore(directory);
        Assert.Throws<ArgumentException>(() => new SecretReference(name));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Permission_downgrade_is_rejected_not_repaired_or_overwritten(bool root)
    {
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("token");
        await store.WriteAsync(reference, "do-not-leak", Token);
        var file = Assert.Single(Directory.GetFiles(directory));
        var path = root ? directory : file;
        if (OperatingSystem.IsWindows())
        {
            var rule = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow);
            if (root)
            {
                var acl = new DirectoryInfo(path).GetAccessControl();
                acl.AddAccessRule(rule);
                new DirectoryInfo(path).SetAccessControl(acl);
            }
            else
            {
                var acl = new FileInfo(path).GetAccessControl();
                acl.AddAccessRule(rule);
                new FileInfo(path).SetAccessControl(acl);
            }
        }
        else File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherRead);
        var error = await Assert.ThrowsAsync<SecretResolutionException>(async () => await store.ResolveAsync(reference, Token));
        Assert.DoesNotContain("do-not-leak", error.ToString());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.WriteAsync(reference, "replacement", Token));
        if (root) Assert.Throws<UnauthorizedAccessException>(() => new LocalSecretStore(directory));
    }

    [Fact]
    public async Task File_symlinks_cannot_read_outside_store()
    {
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("token");
        await store.WriteAsync(reference, "outside", Token);
        var path = Assert.Single(Directory.GetFiles(directory));
        var target = Path.Combine(directory, "target");
        File.Move(path, target);
        try { File.CreateSymbolicLink(path, target); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Assert.Skip("Symbolic links require Windows Developer Mode or SeCreateSymbolicLinkPrivilege."); }
        await Assert.ThrowsAsync<SecretResolutionException>(async () => await store.ResolveAsync(reference, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.WriteAsync(reference, "replacement", Token));
        var link = Path.Combine(directory, "link");
        Directory.CreateSymbolicLink(link, directory);
        Assert.Throws<UnauthorizedAccessException>(() => new LocalSecretStore(Path.Combine(link, "child")));
    }

    [Fact]
    public async Task Cancellation_and_invalid_value_never_replace_the_previous_value()
    {
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("token");
        await store.WriteAsync(reference, "original", Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ResolveAsync(reference, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(reference, "replacement", cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(reference, "bad\0value", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(reference, new string('é', LocalSecretStore.MaxSecretBytes), Token));
        Assert.Equal("original", await store.ResolveAsync(reference, Token));
        Assert.Single(Directory.GetFiles(directory));
        Assert.Throws<ArgumentException>(() => new LocalSecretStore("relative"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linux_foreign_owned_storage_is_rejected_even_when_agent_is_root(bool root)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
            Assert.Skip("Linux root fixture verifies ownership checks independently of filesystem access.");
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("token");
        await store.WriteAsync(reference, "never-read-foreign-owned", Token);
        var path = root ? directory : Assert.Single(Directory.GetFiles(directory));
        await Command("chown", "65534", path);
        try
        {
            await Assert.ThrowsAsync<SecretResolutionException>(async () => await store.ResolveAsync(reference, Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.WriteAsync(reference, "replacement", Token));
            if (root) Assert.Throws<UnauthorizedAccessException>(() => new LocalSecretStore(directory));
        }
        finally { await Command("chown", "0", path); }
    }

    [Fact]
    public async Task Linux_nonregular_file_is_rejected_without_blocking_on_a_FIFO()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux FIFO fixture.");
        var store = new LocalSecretStore(directory);
        var reference = new SecretReference("token");
        await store.WriteAsync(reference, "regular", Token);
        var file = Assert.Single(Directory.GetFiles(directory));
        File.Delete(file);
        await Command("mkfifo", "-m", "600", file);
        await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await store.ResolveAsync(reference, Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), Token));
    }

    private static async Task Command(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(Token);
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
