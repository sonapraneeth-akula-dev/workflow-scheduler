import {
  ApiError,
  getPermissions,
  type PermissionResource,
} from "@/lib/api/client"
import { getTeams, getTypes } from "@/features/catalog/api"
import type { PermissionRole } from "./usePermissions"

export interface ProfileScope {
  readonly kind: "Team" | "Type"
  readonly id: string
  readonly roles: readonly PermissionRole[]
}
export interface ProfileAccess {
  readonly systemAdmin: boolean
  readonly scopes: readonly ProfileScope[]
}

async function permitted(resource: PermissionResource, signal: AbortSignal) {
  try {
    return await getPermissions(resource, signal)
  } catch (error) {
    if (error instanceof ApiError && error.status === 403) return null
    throw error
  }
}

export async function getProfileAccess(
  signal: AbortSignal
): Promise<ProfileAccess> {
  const global = await permitted({ scope: "machines", id: "*" }, signal)
  if (global?.admin) return { systemAdmin: true, scopes: [] }
  const teams = await getTeams(signal)
  const scopes = await Promise.all(
    teams.map(async ({ team }) => {
      const [permissions, types] = await Promise.all([
        permitted({ scope: "team", id: team }, signal),
        getTypes(team, signal),
      ])
      const reads = [
        ...(permissions
          ? [{ kind: "Team" as const, id: team, permissions }]
          : []),
        ...(await Promise.all(
          types.map(async (type) => ({
            kind: "Type" as const,
            id: `${team}/${type.name}`,
            permissions: await permitted(
              { scope: "type", id: `${team}/${type.name}` },
              signal
            ),
          }))
        )),
      ]
      return reads.flatMap(({ kind, id, permissions }) => {
        if (!permissions) return []
        const roles: PermissionRole[] = []
        if (permissions.viewer) roles.push("Viewer")
        if (permissions.author) roles.push("Author")
        if (permissions.operator) roles.push("Operator")
        if (permissions.admin) roles.push("Admin")
        return [{ kind, id, roles }]
      })
    })
  )
  return { systemAdmin: false, scopes: scopes.flat() }
}
