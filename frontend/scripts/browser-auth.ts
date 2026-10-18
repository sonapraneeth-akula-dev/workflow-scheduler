import { PublicClientApplication } from "@azure/msal-browser"
import { z } from "zod"

export async function hydrateBrowserSession(token: string) {
  const payload = token.split(".")[1]
  if (!payload) throw new Error("The browser fixture requires a signed JWT.")
  const claims = z
    .object({ oid: z.uuid(), tid: z.uuid(), exp: z.number().int().positive() })
    .passthrough()
    .parse(JSON.parse(atob(payload.replace(/-/g, "+").replace(/_/g, "/"))))
  if (claims.tid !== import.meta.env.VITE_ENTRA_TENANT_ID)
    throw new Error("The browser fixture token belongs to a different tenant.")
  const authority = `https://login.microsoftonline.com/${claims.tid}`
  const application = new PublicClientApplication({
    auth: {
      clientId: import.meta.env.VITE_ENTRA_SPA_CLIENT_ID,
      authority,
      redirectUri: location.origin,
      authorityMetadata: JSON.stringify({
        authorization_endpoint: `${authority}/oauth2/v2.0/authorize`,
        token_endpoint: `${authority}/oauth2/v2.0/token`,
        end_session_endpoint: `${authority}/oauth2/v2.0/logout`,
        issuer: `${authority}/v2.0`,
        jwks_uri: `${authority}/discovery/v2.0/keys`,
      }),
      cloudDiscoveryMetadata: JSON.stringify({
        metadata: [
          {
            preferred_network: "login.microsoftonline.com",
            preferred_cache: "login.windows.net",
            aliases: ["login.microsoftonline.com", "login.windows.net"],
          },
        ],
      }),
    },
    cache: { cacheLocation: "sessionStorage" },
  })
  await application.initialize()
  const account = {
    homeAccountId: `${claims.oid}.${claims.tid}`,
    localAccountId: claims.oid,
    tenantId: claims.tid,
    environment: "login.windows.net",
    username: "catalogue-viewer@example.test",
    name: "Catalogue viewer",
  }
  const scopes = [import.meta.env.VITE_ENTRA_API_SCOPE]
  await application.hydrateCache(
    {
      account,
      authority,
      uniqueId: claims.oid,
      tenantId: claims.tid,
      scopes,
      idToken: token,
      idTokenClaims: claims,
      accessToken: token,
      tokenType: "Bearer",
      fromCache: false,
      expiresOn: new Date(claims.exp * 1000),
      correlationId: crypto.randomUUID(),
    },
    { account, scopes }
  )
  application.setActiveAccount(account)
}
