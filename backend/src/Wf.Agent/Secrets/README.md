# Agent secret stores

Only references (`secret://<store>/<name>` or legacy safe local names) belong in
variables and manifests. Secret variables stay `SecretReference` objects.
Read their agent-resolved values inside a task:

```csharp
var token = context.Secret(context.Var<SecretReference>("credential"));
```

`context.Secret("secret://local/service/token")` also works when that reference
was declared for the attempt. Undeclared access fails with nonretryable
`ValidationError`. `--describe` and validation never resolve secrets.

The private task-host protocol is **version 2**: rebuild compiled workflow
packages with this SDK. The host returns reference names for this attempt's
bound secret variables (including schema defaults), selected task's declared
HTTP-operation secret, and explicit dispatch references. The agent resolves
those names immediately before execution. Values travel only in memory over
the owner-private Unix-domain socket, separately from `Dispatch`. They never
enter the manifest, variables, launch arguments/environment or result journal.
Resolved values and UTF-8 base64 forms feed the existing output/error/log
masker **before** durable capture. Missing, inaccessible or unavailable values
fail the same attempt as nonretryable `ValidationError`, without a credential,
cloud response body or exception chain. Transformed/encoded data beyond those
forms, explicit report/artifact content and arbitrary task-side external writes
are not a general data-loss-prevention boundary.

## Local (default; no Azure account or login)

Configuration keys (environment equivalents use `__`):

```text
Secrets:Provider=Local
Secrets:LocalDirectory=<absolute dedicated private directory>
```

Default directory: `LocalApplicationData/wf-agent/secrets`, outside the repository.
Provision **as the same OS account that runs the agent**:

```text
<UTF-8 value on redirected stdin> | wf-agent --set-secret secret://local/service/token
```

Use a password manager or another trusted in-memory producer; do not place the
value in shell history, arguments, environment variables or a plaintext file.
The command requires redirected UTF-8 stdin, emits no value, and removes one
terminal LF/CRLF (if present). For programmatic provisioning,
`new LocalSecretStore(absoluteDirectory).WriteAsync(reference, value, token)`
preserves the exact value, including terminal newlines. Empty values, NUL and
values exceeding 25 KiB UTF-8 are rejected.

Windows storage uses **DPAPI `CurrentUser`** plus an agent-owned, user-only ACL.
Linux uses **OS permission protection**, not encryption: an agent-owned `0700`
directory and agent-owned regular `0600` files. Linux `statx` ownership/type
checks also apply to a root agent; don't run the agent as root unnecessarily.
Other OSes are explicitly unsupported by this provider. Names map to flat
SHA-256 filenames; legacy `service/token` and `secret://local/service/token`
address the same value. Symlinks/reparse points (including ancestors), UNC paths,
foreign ownership and relaxed permissions are rejected, never silently repaired.
Updates use protected staging files and atomic replacement. Keep the private
directory under an administrator-controlled, non-writable-by-others parent.
DPAPI values cannot be copied to another account/machine as portable plaintext.

## Azure Key Vault

```text
Secrets:Provider=KeyVault
Secrets:Vaults:production:VaultUri=https://<approved-vault>.vault.azure.net/
Secrets:Vaults:production:ManagedIdentityClientId=<optional user-assigned identity GUID>
```

Reference: `secret://production/my-token`. Every alias explicitly maps to one
configured HTTPS vault origin. Vault paths, credentials, query/fragment,
nondefault ports, unknown aliases and mixed Local/KeyVault configuration fail
explicitly. Vault names must follow Key Vault's 1–127 alphanumeric/hyphen rules;
local names retain the existing broader safe-name model.

Authentication uses **`ManagedIdentityCredential` only**: system-assigned by
default, or the configured user-assigned client ID. No
`DefaultAzureCredential`, Azure CLI/developer/browser credential or local-store
fallback. The Azure-hosted agent must have that managed identity attached.
Grant only secret-read access for the required secrets (e.g. Key Vault Secrets
User at an appropriate scope); no list/write/admin role is required by the code.
The SDK performs HTTPS requests and challenge validation. Retries are bounded
(two retries, 5-second network timeout; 20-second overall secret resolution),
and attempt cancellation/deadlines propagate. SDK content/credential diagnostics
are disabled. Authentication, missing/forbidden secrets, exhausted retries and
resolution timeout expose only the safe reference.

References (official):
- https://www.nuget.org/packages/Azure.Identity/1.21.0
- https://www.nuget.org/packages/Azure.Security.KeyVault.Secrets/4.11.2
- https://learn.microsoft.com/en-us/dotnet/api/azure.identity.managedidentitycredentialoptions
- https://learn.microsoft.com/en-us/azure/key-vault/secrets/quick-create-net
- https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide
- https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
