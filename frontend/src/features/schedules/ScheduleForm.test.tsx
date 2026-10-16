import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { getPermissions, previewSchedule, saveSchedule } from "@/lib/api/client"
import { ScheduleForm } from "./ScheduleForm"

vi.mock("@/lib/api/client", async () => ({
  ...(await vi.importActual("@/lib/api/client")),
  getPermissions: vi.fn(),
  previewSchedule: vi.fn(),
  saveSchedule: vi.fn(),
}))
const permissions = vi.mocked(getPermissions)
const read = {
  scope: "type" as const,
  id: "team/release",
  viewer: true,
  author: false,
  operator: false,
  admin: false,
}

function setup() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <ScheduleForm workflowId="team/release/workflow" onSaved={vi.fn()} />
    </QueryClientProvider>
  )
  return client
}

describe("schedule action permissions", () => {
  beforeEach(() => vi.resetAllMocks())

  it("never previews or saves for a Viewer, including native form submission", async () => {
    permissions.mockResolvedValue(read)
    setup()
    expect(
      await screen.findByText(/Read-only: Operator permission/)
    ).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("Schedule name", { exact: true }), {
      target: { value: "Viewer draft" },
    })
    expect(
      screen.queryByRole("button", { name: "Create schedule" })
    ).not.toBeInTheDocument()
    fireEvent.submit(screen.getByRole("form", { name: "Schedule" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Operator permission is required"
    )
    expect(previewSchedule).not.toHaveBeenCalled()
    expect(saveSchedule).not.toHaveBeenCalled()
  })

  it("preserves unsaved draft fields through permission refresh and revocation", async () => {
    permissions.mockResolvedValue({ ...read, operator: true })
    vi.mocked(previewSchedule).mockResolvedValue(
      Array.from({ length: 5 }, (_, index) => `2026-10-${16 + index}T09:00:00Z`)
    )
    const client = setup()
    await waitFor(() =>
      expect(
        client.getQueryState(["permissions", "type", read.id])?.status
      ).toBe("success")
    )
    fireEvent.change(screen.getByLabelText("Schedule name", { exact: true }), {
      target: { value: "Keep my unsaved draft" },
    })
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Create schedule" })
      ).toBeEnabled()
    )
    permissions.mockRejectedValueOnce(new Error("Permissions unavailable."))
    await act(() => client.invalidateQueries({ queryKey: ["permissions"] }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Permissions unavailable."
    )
    expect(screen.getByLabelText("Schedule name", { exact: true })).toHaveValue(
      "Keep my unsaved draft"
    )
    expect(
      screen.queryByRole("button", { name: "Create schedule" })
    ).not.toBeInTheDocument()
    permissions.mockResolvedValue(read)
    await act(() => client.invalidateQueries({ queryKey: ["permissions"] }))
    expect(
      await screen.findByText(/Read-only: Operator permission/)
    ).toBeInTheDocument()
    expect(screen.getByLabelText("Schedule name", { exact: true })).toHaveValue(
      "Keep my unsaved draft"
    )
    expect(saveSchedule).not.toHaveBeenCalled()
  })
})
