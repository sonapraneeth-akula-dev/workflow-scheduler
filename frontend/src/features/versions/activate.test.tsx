import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError, getPermissions } from "@/lib/api/client"
import { activateVersion, getVersions } from "./api"
import { VersionsTab } from "./VersionsTab"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  activateVersion: vi.fn(),
  getVersions: vi.fn(),
}))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: ReactNode }) => (
    <a href="#pinned">{children}</a>
  ),
}))
const workflowId = "team/type/workflow"
const target = "019ed132-2f44-7000-8000-000000000001"
const latest = "019ed132-2f44-7000-8000-000000000002"
const registration = {
  gitCommit: "a".repeat(40),
  gitAuthor: "author",
  registeredBy: "registrar",
  changeNote: null,
  sdkVersion: "1.0.0",
  registeredAt: "2026-10-16T12:00:00Z",
}
const history = {
  items: [
    { workflowId, versionId: latest, version: "2.0.0", registration },
    { workflowId, versionId: target, version: "1.0.0", registration },
  ],
  nextCursor: null,
  activeVersionId: latest,
  latestVersionId: latest,
}
const permissions = {
  scope: "type" as const,
  id: "team/type",
  viewer: true,
  author: false,
  operator: false,
  admin: true,
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <VersionsTab workflowId={workflowId} pinnedVersionId={latest} />
    </QueryClientProvider>
  )
  return client
}
async function open() {
  const trigger = await screen.findByRole("button", {
    name: `Activate version 1.0.0 (${target})`,
  })
  fireEvent.click(trigger)
  return { trigger, dialog: screen.getByRole("alertdialog") }
}
describe("Admin-only immutable version activation", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getPermissions).mockResolvedValue(permissions)
    vi.mocked(getVersions).mockResolvedValue(history)
    vi.mocked(activateVersion).mockResolvedValue({
      workflowId,
      activeVersionId: target,
      latestVersionId: latest,
    })
  })
  it("requires an explicit exact-version confirmation and restores focus on cancel without mutation", async () => {
    show()
    const { trigger, dialog } = await open()
    expect(dialog).toHaveTextContent(workflowId)
    expect(dialog).toHaveTextContent(`Version 1.0.0 (${target})`)
    expect(dialog).toHaveTextContent(
      "Existing runs and pinned schedules keep their versions"
    )
    expect(dialog).toHaveTextContent(
      "Revert code-defined configuration through source control"
    )
    expect(activateVersion).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull())
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(activateVersion).not.toHaveBeenCalled()
    expect(
      screen.getByRole("button", {
        name: `Activate version 2.0.0 (${latest})`,
      })
    ).toBeDisabled()
  })
  it("sends one exact UUID, prevents duplicate clicks and closing while pending, then refreshes history and catalogue without changing pinned links", async () => {
    let finish!: (value: Awaited<ReturnType<typeof activateVersion>>) => void
    vi.mocked(activateVersion).mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        })
    )
    const client = show()
    const invalidate = vi.spyOn(client, "invalidateQueries")
    const { dialog } = await open()
    const confirm = within(dialog).getByRole("button", {
      name: "Confirm activation",
    })
    fireEvent.click(confirm)
    fireEvent.click(confirm)
    await waitFor(() =>
      expect(activateVersion).toHaveBeenCalledExactlyOnceWith(
        workflowId,
        target
      )
    )
    expect(
      within(dialog).getByRole("button", { name: "Activating..." })
    ).toBeDisabled()
    expect(
      within(dialog).getByRole("button", { name: "Cancel" })
    ).toBeDisabled()
    fireEvent.keyDown(dialog, { key: "Escape" })
    expect(screen.getByRole("alertdialog")).toBeInTheDocument()
    vi.mocked(getVersions).mockResolvedValue({
      ...history,
      activeVersionId: target,
    })
    await act(async () =>
      finish({ workflowId, activeVersionId: target, latestVersionId: latest })
    )
    await within(dialog).findByText(
      "Version activated. Existing runs remain pinned."
    )
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ["workflow-versions", workflowId],
    })
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ["catalog", "team", "team"],
    })
    expect(
      within(dialog).getByRole("button", { name: "Confirm activation" })
    ).toBeDisabled()
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }))
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull())
    expect(screen.getByText(`Active version: ${target}.`)).toBeInTheDocument()
    expect(screen.getByText(`Latest version: ${latest}.`)).toBeInTheDocument()
    expect(
      screen.getByRole("button", { name: `Clone version ${latest}` })
    ).toBeInTheDocument()
    expect(
      screen.getAllByRole("link").map((link) => link.getAttribute("href"))
    ).toEqual(["#pinned", "#pinned"])
  })
  it.each([403, 404, 409, 503])(
    "surfaces HTTP %s, retains the target and requires explicit retry",
    async (status) => {
      vi.mocked(activateVersion).mockRejectedValueOnce(
        new ApiError(new Response(null, { status }), {
          title:
            "Existing schedules are incompatible with this workflow version",
        })
      )
      show()
      const { dialog } = await open()
      fireEvent.click(
        within(dialog).getByRole("button", { name: "Confirm activation" })
      )
      expect(await within(dialog).findByRole("alert")).toHaveTextContent(
        "Existing schedules are incompatible"
      )
      await waitFor(() =>
        expect(
          within(dialog).getByRole("button", { name: "Confirm activation" })
        ).toBeEnabled()
      )
      expect(activateVersion).toHaveBeenCalledOnce()
      fireEvent.click(
        within(dialog).getByRole("button", { name: "Confirm activation" })
      )
      await within(dialog).findByText(
        "Version activated. Existing runs remain pinned."
      )
      expect(activateVersion).toHaveBeenNthCalledWith(2, workflowId, target)
    }
  )
  it.each(["Viewer", "Author", "Operator", "foreign"])(
    "hides mutation controls for %s",
    async (role) => {
      vi.mocked(getPermissions).mockResolvedValue({
        ...permissions,
        admin: role === "foreign",
        id: role === "foreign" ? "other/type" : permissions.id,
        author: role === "Author",
        operator: role === "Operator",
      })
      show()
      await screen.findByRole("link", { name: "1.0.0" })
      expect(
        screen.queryByRole("button", { name: /^Activate version/ })
      ).toBeNull()
      expect(activateVersion).not.toHaveBeenCalled()
    }
  )
  it("denies unknown permissions and exposes failures instead of enabling activation", async () => {
    let reject!: (error: Error) => void
    vi.mocked(getPermissions).mockImplementation(
      () =>
        new Promise((_, fail) => {
          reject = fail
        })
    )
    show()
    await screen.findByRole("link", { name: "1.0.0" })
    expect(
      screen.queryByRole("button", { name: /^Activate version/ })
    ).toBeNull()
    await act(async () => reject(new Error("Permissions unavailable")))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Permissions unavailable"
    )
    expect(
      screen.queryByRole("button", { name: /^Activate version/ })
    ).toBeNull()
    expect(activateVersion).not.toHaveBeenCalled()
  })
  it("disables an already-open confirmation while permissions refresh and after revocation", async () => {
    const client = show()
    const { dialog } = await open()
    let finish!: (value: typeof permissions) => void
    vi.mocked(getPermissions).mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        })
    )
    let refresh!: Promise<void>
    act(() => {
      refresh = client.invalidateQueries({ queryKey: ["permissions"] })
    })
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Confirm activation" })
      ).toBeDisabled()
    )
    await act(async () => {
      finish({ ...permissions, admin: false })
      await refresh
    })
    expect(
      within(dialog).getByRole("button", { name: "Confirm activation" })
    ).toBeDisabled()
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Confirm activation" })
    )
    expect(activateVersion).not.toHaveBeenCalled()
  })
  it("refreshes actual permissions after a server-side revocation and prevents retry with stale Admin UI", async () => {
    show()
    const { dialog } = await open()
    vi.mocked(getPermissions).mockResolvedValue({
      ...permissions,
      admin: false,
    })
    vi.mocked(activateVersion).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Admin revoked",
      })
    )
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Confirm activation" })
    )
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "Admin revoked"
    )
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Confirm activation" })
      ).toBeDisabled()
    )
    expect(getPermissions).toHaveBeenCalledWith(
      { scope: "type", id: "team/type" },
      expect.any(AbortSignal)
    )
    expect(activateVersion).toHaveBeenCalledOnce()
  })
})
