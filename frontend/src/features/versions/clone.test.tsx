import { Blob as NodeBlob } from "node:buffer"
import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError, getPermissions } from "@/lib/api/client"
import { cloneWorkflow, getVersions } from "./api"
import { CloneDialog } from "./CloneDialog"
import { VersionsTab } from "./VersionsTab"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  cloneWorkflow: vi.fn(),
  getVersions: vi.fn(),
}))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: ReactNode }) => (
    <a href="#version">{children}</a>
  ),
}))
const version = "019ed132-2f44-7000-8000-000000000001"
const latest = "019ed132-2f44-7000-8000-000000000002"
const workflowId = "team/type/source"
const allow = (id: string, author = true) => ({
  scope: "type" as const,
  id,
  viewer: true,
  author,
  operator: false,
  admin: false,
})
const createObjectURL = vi.fn(() => "blob:clone")
const revokeObjectURL = vi.fn()
function show(history = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      {history ? (
        <VersionsTab workflowId={workflowId} pinnedVersionId={version} />
      ) : (
        <CloneDialog workflowId={workflowId} versionId={version} />
      )}
    </QueryClientProvider>
  )
  return client
}
async function open() {
  fireEvent.click(
    await screen.findByRole("button", { name: `Clone version ${version}` })
  )
  fireEvent.change(screen.getByLabelText("New workflow name"), {
    target: { value: "experiment" },
  })
  fireEvent.change(screen.getByLabelText("Target team/type"), {
    target: { value: "other/release" },
  })
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Download scaffold" })
    ).toBeEnabled()
  )
}
describe("immutable clone dialog", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.stubGlobal("Blob", NodeBlob)
    Object.defineProperty(URL, "createObjectURL", {
      configurable: true,
      value: createObjectURL,
    })
    Object.defineProperty(URL, "revokeObjectURL", {
      configurable: true,
      value: revokeObjectURL,
    })
    createObjectURL.mockReturnValue("blob:clone")
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {})
    vi.mocked(getPermissions).mockImplementation(async ({ id }) => allow(id))
    vi.mocked(cloneWorkflow).mockResolvedValue(
      new NodeBlob(["PK archive"], { type: "application/zip" }) as Blob
    )
    vi.mocked(getVersions).mockResolvedValue({
      items: [
        {
          workflowId,
          versionId: latest,
          version: "2.0.0",
          registration: {
            gitCommit: "a".repeat(40),
            gitAuthor: "author",
            registeredBy: "registrar",
            changeNote: null,
            sdkVersion: "1.0.0",
            registeredAt: "2026-10-16T12:00:00Z",
          },
        },
      ],
      nextCursor: null,
      activeVersionId: latest,
      latestVersionId: latest,
    })
  })
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })
  it("downloads exact pinned history even when absent from the page and active/latest differ", async () => {
    show(true)
    await open()
    expect(screen.getByRole("alertdialog")).toHaveTextContent(
      `${workflowId}@${version}`
    )
    fireEvent.click(screen.getByRole("button", { name: "Download scaffold" }))
    await screen.findByText(/Scaffold downloaded/)
    expect(cloneWorkflow).toHaveBeenCalledExactlyOnceWith(workflowId, version, {
      newName: "experiment",
      targetType: "other/release",
    })
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalledOnce()
    expect(createObjectURL).toHaveBeenCalledOnce()
    await waitFor(() =>
      expect(revokeObjectURL).toHaveBeenCalledWith("blob:clone")
    )
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Download scaffold" })
      ).toBeDisabled()
    )
  })
  it("validates target drafts before sending or checking target permissions", async () => {
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: `Clone version ${version}` })
    )
    fireEvent.change(screen.getByLabelText("New workflow name"), {
      target: { value: "../escape" },
    })
    fireEvent.submit(
      screen.getByRole("form", { name: "Clone immutable workflow version" })
    )
    expect(screen.getByRole("alert")).toHaveTextContent("workflow leaf name")
    expect(screen.getByLabelText("New workflow name")).toHaveAttribute(
      "aria-invalid",
      "true"
    )
    expect(
      screen.getByRole("button", { name: "Download scaffold" })
    ).toBeDisabled()
    expect(cloneWorkflow).not.toHaveBeenCalled()
  })
  it.each([400, 403, 404, 409, 503])(
    "announces HTTP %i, preserves draft and does not download on failure",
    async (status) => {
      vi.mocked(cloneWorkflow).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: `Clone HTTP ${status}`,
        })
      )
      show()
      await open()
      fireEvent.click(screen.getByRole("button", { name: "Download scaffold" }))
      expect(await screen.findByRole("alert")).toHaveTextContent(
        `Clone HTTP ${status}`
      )
      expect(screen.getByLabelText("New workflow name")).toHaveValue(
        "experiment"
      )
      expect(screen.getByLabelText("Target team/type")).toHaveValue(
        "other/release"
      )
      expect(createObjectURL).not.toHaveBeenCalled()
      expect(
        screen.getByRole("button", { name: "Download scaffold" })
      ).toBeEnabled()
    }
  )
  it("fails closed on target Author revocation while preserving the open draft", async () => {
    const client = show()
    await open()
    vi.mocked(getPermissions).mockImplementation(async ({ id }) =>
      allow(id, false)
    )
    await act(() => client.invalidateQueries({ queryKey: ["permissions"] }))
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Download scaffold" })
      ).toBeDisabled()
    )
    expect(screen.getByLabelText("New workflow name")).toHaveValue("experiment")
    fireEvent.submit(
      screen.getByRole("form", { name: "Clone immutable workflow version" })
    )
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent("Author(target type)")
    )
    expect(cloneWorkflow).not.toHaveBeenCalled()
  })
  it("hides clone when source Viewer permission cannot be confirmed", async () => {
    vi.mocked(getPermissions).mockRejectedValue(
      new Error("Permission service unavailable")
    )
    const client = show()
    await waitFor(() =>
      expect(
        client.getQueryState(["permissions", "type", "team/type"])?.status
      ).toBe("error")
    )
    expect(
      screen.queryByRole("button", { name: /Clone version/ })
    ).not.toBeInTheDocument()
  })
  it("blocks duplicate submission and close during the in-flight download", async () => {
    let finish!: (value: Blob) => void
    vi.mocked(cloneWorkflow).mockReturnValue(
      new Promise((resolve) => {
        finish = resolve
      })
    )
    show()
    await open()
    const form = screen.getByRole("form", {
      name: "Clone immutable workflow version",
    })
    fireEvent.submit(form)
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Downloading scaffold..." })
      ).toBeDisabled()
    )
    fireEvent.submit(form)
    expect(screen.getByRole("button", { name: "Close" })).toBeDisabled()
    expect(cloneWorkflow).toHaveBeenCalledOnce()
    await act(async () => finish(new NodeBlob(["zip"]) as Blob))
    await screen.findByText(/Scaffold downloaded/)
  })
})
