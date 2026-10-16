using Microsoft.Extensions.Configuration;

namespace Wf.Agent.Secrets;

public static class SecretStoreConfiguration
{
    public static ISecretStore Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Secrets");
        var provider = section["Provider"] ?? "Local";
        var vaults = section.GetSection("Vaults").GetChildren().ToArray();
        if (section.Value is not null || section.GetChildren().Any(child =>
            child.Key.Equals("Vaults", StringComparison.OrdinalIgnoreCase) ? child.Value is not null :
            !new[] { "Provider", "LocalDirectory" }.Contains(child.Key, StringComparer.OrdinalIgnoreCase) || child.GetChildren().Any()))
            throw new ArgumentException("Unrecognized Secrets configuration key.");
        return provider switch
        {
            "Local" when vaults.Length == 0 => new LocalSecretStore(section["LocalDirectory"] ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf-agent", "secrets")),
            "KeyVault" when section["LocalDirectory"] is null => new KeyVaultSecretStore(vaults.ToDictionary(vault => vault.Key,
                vault => ReadVault(vault), StringComparer.Ordinal)),
            _ => throw new ArgumentException("Secrets:Provider must be Local or KeyVault with only its own provider configuration.")
        };
    }

    private static KeyVaultStoreOptions ReadVault(IConfigurationSection vault)
    {
        if (vault.Value is not null || vault.GetChildren().Any(child =>
            !new[] { "VaultUri", "ManagedIdentityClientId" }.Contains(child.Key, StringComparer.OrdinalIgnoreCase) || child.GetChildren().Any()) ||
            string.IsNullOrWhiteSpace(vault["VaultUri"]))
            throw new ArgumentException("A secret vault alias requires VaultUri and optionally ManagedIdentityClientId.");
        return new(vault["VaultUri"]!, vault["ManagedIdentityClientId"]);
    }
}
