# Azure agent authentication

Use the same agent image on Azure VMs, with a system-assigned or user-assigned
managed identity. Configure the private controller HTTPS origins, stable agent
UUID, persistent journal/cache paths, and:

```text
Agent__Authentication__Mode=ManagedIdentity
Agent__Authentication__Scope=api://<AzureAd API client UUID>/.default
```

For a user-assigned identity, also set
`Agent__Authentication__ManagedIdentityClientId=<identity client UUID>`.
Omit it for the VM's system-assigned identity. Do not use a principal/object ID
in that setting.

Every configured controller must use HTTPS with a certificate the agent and
SDK task hosts trust. Redirects are restricted to configured origins. Even
loopback HTTP and the isolated-container insecure-transport opt-in are rejected
for managed identity authentication. Never disable certificate validation.

The agent reuses `ManagedIdentityCredential` and requests its API-scoped token
before every gRPC connection, allowing the Azure SDK to cache and refresh it.
It does not cache a bearer string indefinitely, use Azure CLI/developer
credentials, or retry anonymously. Token acquisition is bounded to ten seconds
and respects shutdown cancellation. Identity availability failures produce a
sanitized error and reconnect retry; tokens and credential exception details
are not logged. Authentication is per gRPC call, not per message: an established
stream is not forcibly interrupted solely when its original token expires.
Connection loss/restart obtains current credentials before the next stream.

The default `None` mode preserves explicitly local deployments and adds no
authorization header. It cannot include identity scope/client settings.
Production deployment configuration must explicitly select `ManagedIdentity`;
this setting is not a server-side Development authentication bypass.

## Entra application setup

Configure every API/controller with `Authentication__Mode=Entra`,
`AzureAd__TenantId=<tenant UUID>`, and `AzureAd__ClientId=<API application UUID>`.
The API registration must expose `api://<API application UUID>` and an
application-only app role whose **value** is `Agent` and whose allowed member
type includes `Application`. Include the `idtyp` optional access-token claim.
Assign that role to each worker identity's service principal on the API
enterprise application. This is an **Entra app-role assignment**, not an Azure
subscription/resource RBAC assignment.

The gateway requires a valid token for the configured tenant/API, `roles=Agent`,
`idtyp=app`, and no delegated `scp` claim. A browser/user token, an ARM token, or
an identity having only blob/Key Vault permissions cannot connect as an agent.
Agent identity does not grant browser team-administration privileges.
Configure the separate SPA registration and its scopes for browser access.

Assign only the data permissions the role actually uses: API/controllers need
their shared blob namespace; workers may need selected Key Vault task secrets.
Do not give workers storage-account keys or API database passwords merely to
authenticate their gateway stream.

No Azure identity or role assignment is provisioned by application startup.
Before a cloud rollout, verify the real assigned identity can obtain the API
token and connect over the actual private HTTPS route. Offline credential and
authorization tests do not prove IMDS, Entra propagation, DNS, networking, or
tenant configuration in a real subscription.

References:

- Microsoft Learn: gRPC authentication and authorization in ASP.NET Core,
  `https://learn.microsoft.com/aspnet/core/grpc/authn-and-authz?view=aspnetcore-10.0`
- Microsoft Learn: `ManagedIdentityCredential`,
  `https://learn.microsoft.com/dotnet/api/azure.identity.managedidentitycredential`
- Microsoft Learn: assign an app role to a managed identity,
  `https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-to-assign-app-role-managed-identity-cli`
