import { beforeEach, expect, it, vi } from "vitest"
import {
  ApiError,
  getPermissions,
  type PermissionResource,
} from "@/lib/api/client"
import { getTeams, getTypes } from "@/features/catalog/api"
import { getProfileAccess } from "./profile-access"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("@/features/catalog/api", () => ({
  getTeams: vi.fn(),
  getTypes: vi.fn(),
}))
const permissions = vi.mocked(getPermissions)
const teams = vi.mocked(getTeams)
const types = vi.mocked(getTypes)
const denied = () => new ApiError(new Response(null, { status: 403 }), {})
const flags = (
  scope: PermissionResource["scope"],
  id: string,
  admin = false
) => ({ scope, id, viewer: true, author: admin, operator: admin, admin })
const signal = new AbortController().signal

beforeEach(() => {
  vi.resetAllMocks()
  teams.mockResolvedValue([{ team: "demo", typeCount: 1, presentation: null }])
  types.mockResolvedValue([
    { team: "demo", name: "release", description: "", latestStatusCounts: {} },
  ])
})

it("identifies global administration only through the server's global permission scope", async () => {
  permissions.mockResolvedValue(flags("machines", "*", true))
  await expect(getProfileAccess(signal)).resolves.toEqual({
    systemAdmin: true,
    scopes: [],
  })
  expect(permissions).toHaveBeenCalledWith(
    { scope: "machines", id: "*" },
    signal
  )
  expect(teams).not.toHaveBeenCalled()
})

it("retains independent Operator and Viewer roles without inventing administrator access", async () => {
  permissions.mockImplementation(async (resource) => {
    if (resource.scope === "machines") throw denied()
    return {
      ...flags(resource.scope, resource.id),
      operator: resource.scope === "team",
    }
  })
  await expect(getProfileAccess(signal)).resolves.toEqual({
    systemAdmin: false,
    scopes: [
      { kind: "Team", id: "demo", roles: ["Viewer", "Operator"] },
      { kind: "Type", id: "demo/release", roles: ["Viewer"] },
    ],
  })
})

it("shows a type-only Admin without claiming team or system administration", async () => {
  permissions.mockImplementation(async (resource) => {
    if (resource.scope !== "type") throw denied()
    return flags(resource.scope, resource.id, true)
  })
  await expect(getProfileAccess(signal)).resolves.toEqual({
    systemAdmin: false,
    scopes: [
      {
        kind: "Type",
        id: "demo/release",
        roles: ["Viewer", "Author", "Operator", "Admin"],
      },
    ],
  })
})

it("preserves server-verified team Admin grants", async () => {
  permissions.mockImplementation(async (resource) => {
    if (resource.scope === "machines") throw denied()
    return flags(resource.scope, resource.id, true)
  })
  const access = await getProfileAccess(signal)
  expect(access.systemAdmin).toBe(false)
  expect(access.scopes[0]).toEqual({
    kind: "Team",
    id: "demo",
    roles: ["Viewer", "Author", "Operator", "Admin"],
  })
})

it.each([401, 500, 503])(
  "does not disguise HTTP %i as a lack of roles",
  async (status) => {
    const error = new ApiError(new Response(null, { status }), {})
    permissions.mockRejectedValue(error)
    await expect(getProfileAccess(signal)).rejects.toBe(error)
    expect(teams).not.toHaveBeenCalled()
  }
)
