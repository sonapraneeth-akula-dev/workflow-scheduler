import type { PublicClientApplication } from "@azure/msal-browser"
import { z } from "zod"

export interface Session {
  readonly displayName: string
  readonly isDevelopment: boolean
}

const configuration = z.object({
  tenant: z.uuid(),
  client: z.uuid(),
  scope: z.string().trim().min(1),
})

function isDevelopmentAuth(): boolean {
  if (import.meta.env.VITE_AUTH_MODE !== "development") {
    return false
  }
  if (
    !import.meta.env.DEV ||
    !["localhost", "127.0.0.1", "[::1]"].includes(location.hostname)
  ) {
    throw new Error(
      "Development authentication requires a loopback Vite development server."
    )
  }
  return true
}

let clientPromise: Promise<PublicClientApplication> | undefined
function client(): Promise<PublicClientApplication> {
  clientPromise ??= (async () => {
    const settings = configuration.parse({
      tenant: import.meta.env.VITE_ENTRA_TENANT_ID,
      client: import.meta.env.VITE_ENTRA_SPA_CLIENT_ID,
      scope: import.meta.env.VITE_ENTRA_API_SCOPE,
    })
    const { PublicClientApplication } = await import("@azure/msal-browser")
    const application = new PublicClientApplication({
      auth: {
        clientId: settings.client,
        authority: `https://login.microsoftonline.com/${settings.tenant}`,
        redirectUri: location.origin,
      },
      cache: { cacheLocation: "sessionStorage" },
    })
    await application.initialize()
    await application.handleRedirectPromise()
    return application
  })()
  return clientPromise
}

export async function getSession(): Promise<Session | null> {
  if (isDevelopmentAuth()) {
    return { displayName: "Local developer", isDevelopment: true }
  }
  const application = await client()
  const account =
    application.getActiveAccount() ?? application.getAllAccounts()[0]
  if (!account) {
    return null
  }
  application.setActiveAccount(account)
  return { displayName: account.name ?? account.username, isDevelopment: false }
}

export async function signIn(): Promise<void> {
  const application = await client()
  const response = await application.loginPopup({
    scopes: [import.meta.env.VITE_ENTRA_API_SCOPE],
  })
  await (await import("@/lib/live/subscriptions")).liveSubscriptions.reset()
  application.setActiveAccount(response.account)
}

export async function signOut(): Promise<void> {
  await (await import("@/lib/live/subscriptions")).liveSubscriptions.reset()
  await (await client()).logoutPopup()
}

export async function accessToken(): Promise<string | null> {
  if (isDevelopmentAuth()) {
    return null
  }
  const application = await client()
  const account = application.getActiveAccount()
  if (!account) {
    throw new Error("Sign in before calling the API.")
  }
  try {
    const response = await application.acquireTokenSilent({
      account,
      scopes: [import.meta.env.VITE_ENTRA_API_SCOPE],
    })
    return response.accessToken
  } catch (error) {
    const { InteractionRequiredAuthError } = await import("@azure/msal-browser")
    if (error instanceof InteractionRequiredAuthError) {
      throw new Error(
        "Your sign-in needs attention. Sign out and sign in again.",
        { cause: error }
      )
    }
    throw error
  }
}
