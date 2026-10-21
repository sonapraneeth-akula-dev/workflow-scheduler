import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getReportView, type ReportViewData } from "./api"
import { ReportView } from "./ReportView"

vi.mock("./api", () => ({ getReportView: vi.fn() }))
vi.mock("@/features/runs/SignOffPanel", () => ({
  SignOffPanel: ({ runId, reportId }: { runId: string; reportId: string }) => (
    <section aria-label="Report sign-off">
      {runId}/{reportId}
    </section>
  ),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: React.ReactNode }) => (
    <a href="/runs/fixture">{children}</a>
  ),
}))
vi.mock("@/renderers/ReportRenderer", () => ({
  ReportRenderer: ({
    title,
    kind,
    content,
  }: {
    title: string
    kind: string
    content: Blob
  }) => (
    <p role="status">
      Renderer {kind}: {title} ({content.size} bytes)
    </p>
  ),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const reportId = "019ed132-2f44-7000-8000-000000000002"
const data: ReportViewData = {
  report: {
    id: reportId,
    runId,
    sourceKey: "$post/summary",
    attempt: 2,
    kind: "text",
    title: "Committed title",
    tags: ["review"],
    sizeBytes: 7,
    createdAt: "2026-10-16T08:00:00+00:00",
  },
  content: new Blob(["payload"]),
}
function show(embedded = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <ReportView runId={runId} reportId={reportId} embedded={embedded} />
    </QueryClientProvider>
  )
}
describe("authenticated report viewer", () => {
  beforeEach(() => vi.resetAllMocks())
  it("shows committed identity and passes the Blob and metadata to the renderer", async () => {
    vi.mocked(getReportView).mockResolvedValue(data)
    show()
    expect(
      await screen.findByRole("heading", { name: data.report.title })
    ).toBeVisible()
    expect(
      screen.getByText("text | $post/summary | Attempt 2 | 7 bytes")
    ).toBeVisible()
    expect(screen.getByRole("status")).toHaveTextContent(
      "Renderer text: Committed title (7 bytes)"
    )
    expect(getReportView).toHaveBeenCalledWith(
      runId,
      reportId,
      expect.any(AbortSignal)
    )
    expect(screen.getByRole("link", { name: "Back to run" })).toBeVisible()
    expect(
      screen.getByRole("region", { name: "Report sign-off" })
    ).toHaveTextContent(`${runId}/${reportId}`)
  })
  it.each([401, 403, 503])(
    "hides stale content after refused refresh HTTP %i",
    async (status) => {
      vi.mocked(getReportView).mockResolvedValue(data)
      show()
      await screen.findByRole("heading")
      vi.mocked(getReportView).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Report unavailable.",
        })
      )
      fireEvent.click(screen.getByRole("button", { name: "Refresh report" }))
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Report unavailable."
      )
      expect(screen.queryByRole("heading")).not.toBeInTheDocument()
      expect(screen.queryByRole("status")).not.toBeInTheDocument()
    }
  )
  it("embeds the same authorized renderer without standalone navigation or a page heading", async () => {
    vi.mocked(getReportView).mockResolvedValue(data)
    show(true)
    expect(
      await screen.findByRole("heading", { level: 3, name: data.report.title })
    ).toBeVisible()
    expect(screen.queryByRole("heading", { level: 1 })).not.toBeInTheDocument()
    expect(
      screen.queryByRole("link", { name: "Back to run" })
    ).not.toBeInTheDocument()
    expect(screen.getByText(/Tags: review/)).toHaveTextContent("Published")
    expect(screen.getByRole("status")).toHaveTextContent(
      "Renderer text: Committed title"
    )
  })
  it("shows loading without stale or fabricated report metadata", () => {
    vi.mocked(getReportView).mockImplementation(() => new Promise(() => {}))
    show()
    expect(screen.getByRole("status")).toHaveTextContent("Loading report...")
    expect(
      screen.getByRole("button", { name: "Refresh report" })
    ).toBeDisabled()
    expect(screen.queryByRole("heading")).not.toBeInTheDocument()
  })
})
