import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { getPermissions } from "@/lib/api/client"
import { runNow } from "./api"
import { RunNowButton } from "./RunNowButton"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./api", () => ({ runNow: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
  }: {
    params: { id: string }
    children: ReactNode
  }) => <a href={`/runs/${params.id}`}>{children}</a>,
}))
const scheduleId = "019ed132-2f44-7000-8000-000000000001"
const seriesId = "019ed132-2f44-7000-8000-000000000002"
const runId = "019ed132-2f44-7000-8000-000000000003"
const grant = {
  scope: "series" as const,
  id: seriesId,
  viewer: true,
  author: false,
  operator: true,
  admin: false,
}
function show(disabled = false) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: 3, retryDelay: 0 },
    },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <RunNowButton
          scheduleId={scheduleId}
          seriesId={seriesId}
          disabled={disabled}
        />
      </QueryClientProvider>
    ),
    client,
  }
}
describe("Trigger now action", () => {
  beforeEach(() => vi.resetAllMocks())
  it.each([false, true])(
    "hides actions for a Viewer/Author without Operator (author=%s)",
    async (author) => {
      vi.mocked(getPermissions).mockResolvedValue({
        ...grant,
        operator: false,
        author,
      })
      const { client } = show()
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "series", seriesId])?.status
        ).toBe("success")
      )
      expect(screen.queryByRole("button")).not.toBeInTheDocument()
      expect(runNow).not.toHaveBeenCalled()
    }
  )
  it("fails closed while permission reads are pending or denied", async () => {
    vi.mocked(getPermissions).mockRejectedValue(
      new Error("Permissions unavailable.")
    )
    show()
    expect(screen.queryByRole("button")).not.toBeInTheDocument()
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Permissions unavailable."
    )
    expect(screen.queryByRole("button")).not.toBeInTheDocument()
  })
  it("disables actions during a history/schedule refresh", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    show(true)
    expect(
      await screen.findByRole("button", { name: "Trigger now" })
    ).toBeDisabled()
    expect(runNow).not.toHaveBeenCalled()
  })
  it("accepts once via keyboard, invalidates run views and links the receipt without pretending it is committed", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(runNow).mockResolvedValue({ runId })
    const { client } = show()
    const invalidate = vi.spyOn(client, "invalidateQueries")
    const user = userEvent.setup()
    ;(await screen.findByRole("button", { name: "Trigger now" })).focus()
    await user.keyboard("{Enter}")
    expect(await screen.findByRole("status")).toHaveTextContent("Manual")
    expect(screen.getByRole("status")).toHaveTextContent(
      "engine creates the run asynchronously"
    )
    expect(screen.getByRole("link")).toHaveAttribute("href", `/runs/${runId}`)
    expect(runNow).toHaveBeenCalledWith(scheduleId, expect.any(String))
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["run-history"] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["runs"] })
  })
  it("reuses the same key after an ambiguous failure, never retries automatically and generates a fresh key only after acceptance", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(runNow)
      .mockRejectedValueOnce(new Error("Response lost."))
      .mockResolvedValue({ runId })
    show()
    fireEvent.click(await screen.findByRole("button", { name: "Trigger now" }))
    expect(await screen.findByRole("alert")).toHaveTextContent("Response lost.")
    expect(screen.getByText(/Retry reuses the same request key/)).toBeVisible()
    expect(runNow).toHaveBeenCalledTimes(1)
    const firstKey = vi.mocked(runNow).mock.calls[0]![1]
    fireEvent.click(screen.getByRole("button", { name: "Retry trigger" }))
    await screen.findByRole("status")
    expect(runNow).toHaveBeenLastCalledWith(scheduleId, firstKey)
    fireEvent.click(screen.getByRole("button", { name: "Trigger now" }))
    await waitFor(() => expect(runNow).toHaveBeenCalledTimes(3))
    expect(vi.mocked(runNow).mock.calls[2]![1]).not.toBe(firstKey)
  })
  it("prevents double submission while acceptance is pending", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(runNow).mockReturnValue(new Promise(() => {}))
    show()
    const user = userEvent.setup()
    await user.dblClick(
      await screen.findByRole("button", { name: "Trigger now" })
    )
    expect(runNow).toHaveBeenCalledTimes(1)
    expect(
      screen.getByRole("button", { name: "Accepting run..." })
    ).toBeDisabled()
  })
  it("removes the action after permission revocation", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    const { client } = show()
    await screen.findByRole("button", { name: "Trigger now" })
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
    await client.invalidateQueries({ queryKey: ["permissions"] })
    await waitFor(() =>
      expect(screen.queryByRole("button")).not.toBeInTheDocument()
    )
    expect(runNow).not.toHaveBeenCalled()
  })
})
