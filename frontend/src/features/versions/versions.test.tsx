import { Blob as NodeBlob } from "node:buffer"
import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { ReportRenderer } from "@/renderers/ReportRenderer"
import { getVersionDiff, getVersions, type VersionDiff } from "./api"
import { VersionsTab } from "./VersionsTab"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getVersions: vi.fn(),
  getVersionDiff: vi.fn(),
}))
vi.mock("@/renderers/ReportRenderer", () => ({
  ReportRenderer: vi.fn(({ title }: { title: string }) => (
    <div aria-label={title} />
  )),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
  }: {
    to: string
    params: Record<string, string>
    children: ReactNode
  }) => (
    <a
      href={Object.entries(params).reduce(
        (path, [key, value]) =>
          path.replace(`$${key}`, encodeURIComponent(value)),
        to
      )}
    >
      {children}
    </a>
  ),
}))
const workflowId = "team/type/workflow"
const from = "019ed132-2f44-7000-8000-000000000001"
const to = "019ed132-2f44-7000-8000-000000000002"
const registration = {
  gitCommit: "a".repeat(40),
  gitAuthor: "<script>Author</script>",
  registeredBy: "registering principal",
  changeNote: "Release <img src=x>",
  sdkVersion: "1.0.0",
  registeredAt: "2026-10-16T12:00:00Z",
}
const page = {
  items: [
    { workflowId, versionId: to, version: "2.0.0", registration },
    {
      workflowId,
      versionId: from,
      version: "1.0.0",
      registration: { ...registration, changeNote: null },
    },
  ],
  nextCursor: null,
  activeVersionId: from,
  latestVersionId: to,
}
const diff: VersionDiff = {
  workflowId,
  fromVersionId: from,
  toVersionId: to,
  fromVersion: "1.0.0",
  toVersion: "2.0.0",
  manifest: {
    nodes: [
      {
        key: "work",
        kind: "Changed",
        before: { timeout: "01:00:00" },
        after: { timeout: "02:00:00" },
      },
    ],
    variables: [
      {
        key: "added",
        kind: "Added",
        before: null,
        after: { type: "Long", default: "literal <script>value</script>" },
      },
    ],
    policies: [],
    settings: [],
  },
  source: {
    path: "workflow.cs",
    before: "original\r\n",
    after: "modified\n",
    changed: true,
  },
}
function show(pinnedVersionId?: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={client}>
      <VersionsTab workflowId={workflowId} pinnedVersionId={pinnedVersionId} />
    </QueryClientProvider>
  )
  return { client, ...view }
}
const denied = () =>
  new ApiError(new Response(null, { status: 403 }), {
    title: "Viewer access revoked",
  })

describe("version history, comparison and read-only states", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.stubGlobal("Blob", NodeBlob)
    vi.mocked(getVersions).mockResolvedValue(page)
    vi.mocked(getVersionDiff).mockResolvedValue(diff)
  })
  afterEach(() => vi.unstubAllGlobals())
  it("renders immutable provenance and exact UUID links, without loading Monaco or source before comparison", async () => {
    show()
    expect(screen.getByRole("status")).toHaveTextContent(
      "Loading version history"
    )
    const link = await screen.findByRole("link", { name: "2.0.0" })
    expect(link).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${to}`
    )
    expect(screen.getByRole("link", { name: "1.0.0" })).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${from}`
    )
    expect(screen.getByText(`Active version: ${from}.`)).toBeInTheDocument()
    expect(screen.getByText(`Latest version: ${to}.`)).toBeInTheDocument()
    expect(screen.getByText("No change note.")).toBeInTheDocument()
    expect(screen.getAllByText("<script>Author</script>")).toHaveLength(2)
    expect(document.querySelector("script, img")).toBeNull()
    expect(getVersionDiff).not.toHaveBeenCalled()
    expect(ReportRenderer).not.toHaveBeenCalled()
    expect(screen.getByLabelText("Compare from")).toHaveValue(from)
    expect(screen.getByLabelText("Compare to")).toHaveValue(to)
  })
  it("requests a comparison only on explicit submit and reuses the lazy report diff with exact source bytes", async () => {
    show()
    fireEvent.click(
      await screen.findByRole("button", {
        name: "Compare versions",
      })
    )
    await waitFor(() => expect(ReportRenderer).toHaveBeenCalled())
    expect(getVersionDiff).toHaveBeenCalledWith(
      workflowId,
      from,
      to,
      expect.any(AbortSignal)
    )
    const props = vi.mocked(ReportRenderer).mock.calls.at(-1)![0]
    expect(props.kind).toBe("diff")
    expect(JSON.parse(await props.content.text())).toEqual({
      original: "original\r\n",
      modified: "modified\n",
    })
    expect(screen.getByText("Changed: work")).toBeInTheDocument()
    expect(screen.getByText("Added: added")).toBeInTheDocument()
    expect(screen.getByText("No policies changes.")).toBeInTheDocument()
    fireEvent.click(screen.getByText("Added: added"))
    expect(
      screen.getByText(
        '{\n  "type": "Long",\n  "default": "literal <script>value</script>"\n}',
        { normalizer: (text) => text }
      )
    ).toBeInTheDocument()
    expect(document.querySelector("script")).toBeNull()
  })
  it("allows reverse and same-version comparisons, and retains a pinned version outside the first loaded page", async () => {
    const pinned = "019ed132-2f44-7000-8000-000000000099"
    show(pinned)
    await screen.findByRole("link", { name: "2.0.0" })
    expect(screen.getByLabelText("Compare from")).toHaveValue(pinned)
    fireEvent.change(screen.getByLabelText("Compare from"), {
      target: { value: to },
    })
    fireEvent.change(screen.getByLabelText("Compare to"), {
      target: { value: from },
    })
    fireEvent.click(screen.getByRole("button", { name: "Compare versions" }))
    await waitFor(() =>
      expect(getVersionDiff).toHaveBeenLastCalledWith(
        workflowId,
        to,
        from,
        expect.any(AbortSignal)
      )
    )
    fireEvent.change(screen.getByLabelText("Compare to"), {
      target: { value: to },
    })
    fireEvent.click(screen.getByRole("button", { name: "Compare versions" }))
    await waitFor(() =>
      expect(getVersionDiff).toHaveBeenLastCalledWith(
        workflowId,
        to,
        to,
        expect.any(AbortSignal)
      )
    )
  })
  it("loads the next bounded cursor page and updates active/latest pointers without changing immutable links", async () => {
    const first = {
      ...page,
      items: Array.from({ length: 50 }, (_, index) => ({
        ...page.items[0]!,
        versionId: `019ed132-2f44-7000-8000-${String(index + 3).padStart(12, "0")}`,
        version: `3.0.${index}`,
      })),
      nextCursor: "opaque-cursor",
    }
    vi.mocked(getVersions)
      .mockResolvedValueOnce(first)
      .mockResolvedValueOnce({
        ...page,
        items: [page.items[1]!],
        activeVersionId: to,
        latestVersionId: first.items[0]!.versionId,
      })
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: "Load more versions" })
    )
    await screen.findByRole("link", { name: "1.0.0" })
    expect(getVersions).toHaveBeenLastCalledWith(
      workflowId,
      "opaque-cursor",
      expect.any(AbortSignal)
    )
    expect(screen.getAllByRole("row")).toHaveLength(52)
    expect(screen.getByText(`Active version: ${to}.`)).toBeInTheDocument()
    expect(
      screen.queryByRole("button", { name: "Load more versions" })
    ).not.toBeInTheDocument()
  })
  it("keeps an explicitly selected UUID visible when a refreshed page no longer contains that version", async () => {
    const { client } = show()
    await screen.findByRole("link", { name: "1.0.0" })
    fireEvent.change(screen.getByLabelText("Compare from"), {
      target: { value: to },
    })
    fireEvent.change(screen.getByLabelText("Compare from"), {
      target: { value: from },
    })
    vi.mocked(getVersions).mockResolvedValue({
      ...page,
      items: [page.items[0]!],
    })
    await act(() =>
      client.refetchQueries({ queryKey: ["workflow-versions", workflowId] })
    )
    await waitFor(() =>
      expect(
        screen.queryByRole("link", { name: "1.0.0" })
      ).not.toBeInTheDocument()
    )
    expect(screen.getByLabelText("Compare from")).toHaveValue(from)
    expect(
      screen.getByRole("option", { name: `Selected version ${from}` })
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole("button", { name: "Compare versions" }))
    await waitFor(() =>
      expect(getVersionDiff).toHaveBeenCalledWith(
        workflowId,
        from,
        to,
        expect.any(AbortSignal)
      )
    )
  })
  it("hides a cached list and source comparison after fresh history denial instead of retaining revoked data", async () => {
    const { client } = show()
    fireEvent.click(
      await screen.findByRole("button", {
        name: "Compare versions",
      })
    )
    await waitFor(() => expect(ReportRenderer).toHaveBeenCalled())
    vi.mocked(getVersions).mockRejectedValue(denied())
    await act(() =>
      client.refetchQueries({ queryKey: ["workflow-versions", workflowId] })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Viewer access revoked"
    )
    expect(
      screen.queryByRole("link", { name: "2.0.0" })
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole("region", { name: "Version comparison" })
    ).not.toBeInTheDocument()
    vi.mocked(getVersions).mockResolvedValue(page)
    fireEvent.click(screen.getByRole("button", { name: "Refresh versions" }))
    expect(
      await screen.findByRole("link", { name: "2.0.0" })
    ).toBeInTheDocument()
  })
  it("hides cached comparison data after denial and retries without claiming no changes", async () => {
    show()
    fireEvent.click(
      await screen.findByRole("button", {
        name: "Compare versions",
      })
    )
    await screen.findByText("Changed: work")
    vi.mocked(getVersionDiff).mockRejectedValue(denied())
    fireEvent.click(screen.getByRole("button", { name: "Refresh comparison" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Viewer access revoked"
    )
    expect(screen.queryByText("Changed: work")).not.toBeInTheDocument()
    expect(screen.queryByText("No source changes.")).not.toBeInTheDocument()
    vi.mocked(getVersionDiff).mockResolvedValue(diff)
    fireEvent.click(screen.getByRole("button", { name: "Refresh comparison" }))
    expect(await screen.findByText("Changed: work")).toBeInTheDocument()
  })
  it("distinguishes empty history, read failure and duplicated cross-page identities", async () => {
    vi.mocked(getVersions).mockResolvedValue({
      ...page,
      items: [],
      activeVersionId: null,
      latestVersionId: null,
    })
    const { client } = show()
    expect(
      await screen.findByText("No registered versions.")
    ).toBeInTheDocument()
    expect(
      screen.queryByRole("button", { name: "Compare versions" })
    ).not.toBeInTheDocument()
    vi.mocked(getVersions).mockResolvedValue({
      ...page,
      items: [page.items[0]!, page.items[0]!],
    })
    await act(() => client.refetchQueries())
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "repeated an immutable version"
    )
    expect(
      screen.queryByRole("link", { name: "2.0.0" })
    ).not.toBeInTheDocument()
    vi.mocked(getVersions).mockRejectedValue(
      new Error("Retained history storage unavailable")
    )
    await act(() => client.refetchQueries())
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        "Retained history storage unavailable"
      )
    )
    expect(
      screen.queryByText("No registered versions.")
    ).not.toBeInTheDocument()
  })
  it("aborts an old comparison when the version pair changes and ignores its later result", async () => {
    let finish: ((value: VersionDiff) => void) | undefined
    vi.mocked(getVersionDiff).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve
        })
    )
    show()
    fireEvent.click(
      await screen.findByRole("button", {
        name: "Compare versions",
      })
    )
    await waitFor(() => expect(getVersionDiff).toHaveBeenCalledOnce())
    const signal = vi.mocked(getVersionDiff).mock.calls[0]![3]!
    fireEvent.change(screen.getByLabelText("Compare from"), {
      target: { value: to },
    })
    fireEvent.click(screen.getByRole("button", { name: "Compare versions" }))
    await waitFor(() => expect(signal.aborted).toBe(true))
    await act(async () =>
      finish?.({ ...diff, source: { ...diff.source, before: "stale source" } })
    )
    await waitFor(() => expect(ReportRenderer).toHaveBeenCalled())
    const props = vi.mocked(ReportRenderer).mock.calls.at(-1)![0]
    expect(await props.content.text()).not.toContain("stale source")
  })
})
