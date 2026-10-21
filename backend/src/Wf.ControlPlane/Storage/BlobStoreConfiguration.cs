using Azure.Identity;
using Azure.Storage.Blobs;

namespace Wf.ControlPlane.Storage;

public static class BlobStoreConfiguration
{
    public static IBlobStore Create(IConfiguration configuration, byte[] hmacKey, TimeProvider time,
        IHostEnvironment? environment = null)
    {
        var provider = configuration["Storage:Provider"] ?? "FileSystem";
        if (provider == "FileSystem")
            return new FileSystemBlobStore(configuration["Storage:Root"] ??
                Path.Combine(AppContext.BaseDirectory, "wf-data", "blobs"), hmacKey, timeProvider: time);
        if (provider != "AzureBlob")
            throw new InvalidOperationException("Storage:Provider must be FileSystem or AzureBlob.");

        var container = configuration["Storage:Azure:Container"];
        if (container is null || container.Length is < 3 or > 63 || container[0] == '-' || container[^1] == '-' ||
            container.Contains("--", StringComparison.Ordinal) ||
            container.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-'))
            throw new InvalidOperationException("Storage:Azure:Container must be a valid lowercase Azure blob container name.");

        BlobServiceClient client;
        if (configuration["Storage:Azure:EmulatorConnectionString"] is { } emulator)
        {
            if (environment?.IsDevelopment() != true)
                throw new InvalidOperationException("Azure emulator credentials require an explicit Development environment.");
            if (configuration["Storage:Azure:ServiceUri"] is not null ||
                configuration["Storage:Azure:ManagedIdentityClientId"] is not null)
                throw new InvalidOperationException("Azure emulator and managed-identity settings cannot be combined.");
            try { client = new BlobServiceClient(emulator); }
            catch (Exception error) when (error is ArgumentException or FormatException)
            {
                // SDK parse errors can contain connection-string material.
                throw new InvalidOperationException("Storage:Azure:EmulatorConnectionString is invalid.");
            }
        }
        else
        {
            if (!Uri.TryCreate(configuration["Storage:Azure:ServiceUri"], UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
                endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new InvalidOperationException("Storage:Azure:ServiceUri must be an HTTPS service origin.");
            var identity = ManagedIdentityId.SystemAssigned;
            if (configuration["Storage:Azure:ManagedIdentityClientId"] is { } text)
            {
                if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
                    throw new InvalidOperationException("Storage:Azure:ManagedIdentityClientId must be a non-empty client UUID.");
                identity = ManagedIdentityId.FromUserAssignedClientId(id.ToString("D"));
            }
            client = new BlobServiceClient(endpoint, new ManagedIdentityCredential(identity));
        }
        return new AzureBlobStore(client, container, timeProvider: time);
    }
}
