# Shared blob storage

`Storage:Provider` defaults to `FileSystem`, retaining `Storage:Root` and existing
local behavior. Separate API/controller instances must use the **same** durable
blob namespace and `Storage:UploadHmacKey`; per-pod filesystems are not shared
storage. Keep that HMAC key stable across replicas and rolling updates, in a
secret store, never in images or source control.

For Azure, configure every API/controller:

```text
Storage__Provider=AzureBlob
Storage__Azure__ServiceUri=https://<account>.blob.core.windows.net
Storage__Azure__Container=kardia-blobs
```

Provision the private container externally. Assign each service's managed
identity `Storage Blob Data Contributor` on that container. Reads, immutable
verified writes, ephemeral log/resource maintenance, and retention deletion use
the existing `AzureBlobStore`. The service does not create containers or change
storage account policy. Keep public access and account-key authentication
disabled in production, use private endpoints/network restrictions, and choose
storage redundancy appropriate to the required availability and recovery goals.

System-assigned managed identity is the default. For a user-assigned identity,
set `Storage__Azure__ManagedIdentityClientId` to its non-empty client UUID.
Authentication uses `ManagedIdentityCredential` directly, not a developer-login
or environment-secret fallback chain. Host failures remain visible: Azure access
errors never fall back to a different local filesystem.

Resource-series blob reads and immutable writes run outside database
transactions, so storage latency cannot hold the allocation lock or exhaust the
engine's one-second fenced transaction budget. Publishing the series pointer
rechecks the captured leader epoch, agent session, reservation, attempt state,
and prior pointer under the existing locks. Concurrent identical samples commit
once; conflicting replays fail. A rejected publication may leave an unreferenced
immutable chunk, but cannot advance the summary or acknowledge an invalid write.

An explicitly **Development** host may instead set
`Storage__Azure__EmulatorConnectionString` for a local Azurite lab. Do not also
set the managed-identity URI/client ID. Remote emulators require HTTPS and a
trusted certificate; plaintext is accepted only for a loopback emulator. This
setting is rejected in Production, Staging, or when no host environment was
supplied. Connection-string parsing errors are sanitized rather than echoed.
Emulator credentials belong in the lab's protected local state/Kubernetes
Secrets, not committed YAML.

This wiring does not authenticate the application API, secure PostgreSQL, make
the lab emulator highly available, or provide PostgreSQL backup. Those are
separate deployment responsibilities. The application still requires Entra
authentication and correctly configured TLS listeners for production.

References: Microsoft Learn's `ManagedIdentityCredential`, `ManagedIdentityId`,
and Azure Blob Storage built-in data roles documentation.
