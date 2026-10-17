import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError, getPermissions } from "@/lib/api/client"
import {
  createSubscription,
  deleteSubscription,
  getSubscriptions,
  type Subscription,
} from "./api"
import { SubscribeMenu } from "./SubscribeMenu"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  createSubscription: vi.fn(),
  deleteSubscription: vi.fn(),
  getSubscriptions: vi.fn(),
}))
const permissions = vi.mocked(getPermissions)
const list = vi.mocked(getSubscriptions)
const create = vi.mocked(createSubscription)
const remove = vi.mocked(deleteSubscription)
const viewer = {
  scope: "type" as const,
  id: "team/type",
  viewer: true,
  author: false,
  operator: false,
  admin: false,
}
const id = "019ed132-2f44-7000-8000-000000000002"
const row: Subscription = {
  id,
  scope: "workflow",
  scopeId: "team/type/workflow",
  events: ["Completed"],
  channel: "Email",
  target: "subscriber@example.test",
  secretRef: null,
  createdAt: "2026-10-17T10:00:00Z",
}
function setup() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={client}>
      <SubscribeMenu scope="workflow" scopeId={row.scopeId} />
    </QueryClientProvider>
  )
  return { client, ...view }
}
async function open() {
  fireEvent.click(screen.getByRole("button", { name: "Subscribe" }))
  await screen.findByRole("form", { name: "Create notification subscription" })
}
function fillEmail() {
  fireEvent.change(screen.getByLabelText("Email address"), {
    target: { value: row.target },
  })
}

describe("scoped subscription controls", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    permissions.mockResolvedValue(viewer)
    list.mockResolvedValue({ items: [], nextCursor: null })
    create.mockResolvedValue(row)
    remove.mockResolvedValue(undefined)
  })
  it("loads personal subscriptions only when opened, with accessible expansion controls", async () => {
    setup()
    const trigger = screen.getByRole("button", {
      name: "Subscribe",
    })
    expect(trigger).toHaveAttribute("aria-expanded", "false")
    expect(list).not.toHaveBeenCalled()
    await open()
    expect(trigger).toHaveAttribute("aria-expanded", "true")
    expect(
      document.getElementById(trigger.getAttribute("aria-controls")!)
    ).toBe(screen.getByRole("region", { name: "Notification subscriptions" }))
    expect(list).toHaveBeenCalledWith(
      { scope: "workflow", scopeId: row.scopeId },
      null,
      expect.any(AbortSignal)
    )
    expect(
      screen.getByText("No subscriptions for this workflow.")
    ).toBeInTheDocument()
    fireEvent.click(trigger)
    expect(
      screen.queryByRole("region", { name: "Notification subscriptions" })
    ).not.toBeInTheDocument()
  })
  it("allows a Viewer without Operator or Admin to save and verifies the scoped event/channel payload", async () => {
    const { client } = setup()
    await open()
    fillEmail()
    fireEvent.change(screen.getByLabelText("Events"), {
      target: { value: "Failed" },
    })
    fireEvent.submit(
      screen.getByRole("form", { name: "Create notification subscription" })
    )
    await waitFor(() =>
      expect(create).toHaveBeenCalledWith({
        scope: "workflow",
        scopeId: row.scopeId,
        events: ["Failed"],
        channel: "Email",
        target: row.target,
        secretRef: null,
      })
    )
    expect(
      await screen.findByText(
        "Subscription saved. Delivery depends on operator configuration."
      )
    ).toBeInTheDocument()
    expect(
      client.getQueryState(["subscriptions", "workflow", row.scopeId])?.status
    ).toBe("success")
  })
  it("switches destination fields without carrying values or signing references between channels", async () => {
    setup()
    await open()
    fillEmail()
    fireEvent.change(screen.getByLabelText("Channel"), {
      target: { value: "Webhook" },
    })
    expect(screen.getByLabelText("Webhook URL")).toHaveValue("")
    fireEvent.change(screen.getByLabelText("Webhook URL"), {
      target: { value: "https://notify.example.test/hook" },
    })
    fireEvent.change(screen.getByLabelText("Signing secret reference"), {
      target: { value: "secret://notifications/signing" },
    })
    fireEvent.submit(
      screen.getByRole("form", { name: "Create notification subscription" })
    )
    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({
          channel: "Webhook",
          secretRef: "secret://notifications/signing",
        })
      )
    )
    await waitFor(() => expect(screen.getByLabelText("Channel")).toBeEnabled())
    fireEvent.change(screen.getByLabelText("Channel"), {
      target: { value: "Teams" },
    })
    expect(screen.getByLabelText("Teams destination reference")).toHaveValue("")
    expect(
      screen.queryByLabelText("Signing secret reference")
    ).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("Teams destination reference"), {
      target: { value: "secret://notifications/teams" },
    })
    fireEvent.submit(
      screen.getByRole("form", { name: "Create notification subscription" })
    )
    await waitFor(() =>
      expect(create).toHaveBeenLastCalledWith(
        expect.objectContaining({
          channel: "Teams",
          target: "secret://notifications/teams",
          secretRef: null,
        })
      )
    )
  })
  it("guards synchronous double-submit and exposes pending state without a second request", async () => {
    let complete!: (value: Subscription) => void
    create.mockReturnValue(
      new Promise((resolve) => {
        complete = resolve
      })
    )
    setup()
    await open()
    fillEmail()
    const form = screen.getByRole("form", {
      name: "Create notification subscription",
    })
    fireEvent.submit(form)
    fireEvent.submit(form)
    expect(
      await screen.findByRole("button", { name: "Saving subscription..." })
    ).toBeDisabled()
    expect(create).toHaveBeenCalledTimes(1)
    await act(async () => complete(row))
    await screen.findByText(
      "Subscription saved. Delivery depends on operator configuration."
    )
  })
  it.each([409, 503])(
    "keeps the draft and reports HTTP %i without automatic creation retries",
    async (status) => {
      create.mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Destination unavailable",
        })
      )
      setup()
      await open()
      fillEmail()
      fireEvent.submit(
        screen.getByRole("form", { name: "Create notification subscription" })
      )
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Destination unavailable"
      )
      expect(screen.getByLabelText("Email address")).toHaveValue(row.target)
      expect(create).toHaveBeenCalledTimes(1)
    }
  )
  it("does not read or expose create controls without current Viewer permission", async () => {
    permissions.mockResolvedValue({ ...viewer, viewer: false })
    setup()
    fireEvent.click(screen.getByRole("button", { name: "Subscribe" }))
    expect(
      await screen.findByText(
        "Current Viewer permission is required to view or create subscriptions."
      )
    ).toBeInTheDocument()
    expect(screen.queryByRole("form")).not.toBeInTheDocument()
    expect(list).not.toHaveBeenCalled()
    expect(create).not.toHaveBeenCalled()
  })
  it("hides cached destinations during a permission check, preserves the draft, and restores it only after access succeeds", async () => {
    list.mockResolvedValue({ items: [row], nextCursor: null })
    const { client } = setup()
    await open()
    fillEmail()
    let finish!: (value: typeof viewer) => void
    permissions.mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve
      })
    )
    let checking!: Promise<void>
    await act(async () => {
      checking = client.invalidateQueries({ queryKey: ["permissions"] })
    })
    await waitFor(() =>
      expect(screen.queryByText(row.target)).not.toBeInTheDocument()
    )
    expect(screen.queryByLabelText("Email address")).not.toBeInTheDocument()
    expect(
      screen.queryByRole("button", { name: "Unsubscribe Email" })
    ).not.toBeInTheDocument()
    await act(async () => {
      finish(viewer)
      await checking
    })
    expect(await screen.findByLabelText("Email address")).toHaveValue(
      row.target
    )
    expect(screen.getByText(row.target)).toBeInTheDocument()
    expect(list).toHaveBeenCalledTimes(1)
  })
  it("permits owner unsubscribe after revocation but shows only known IDs/channel, never cached destinations", async () => {
    list.mockResolvedValue({ items: [row], nextCursor: null })
    const { client } = setup()
    await open()
    permissions.mockResolvedValue({ ...viewer, viewer: false })
    await act(() => client.invalidateQueries({ queryKey: ["permissions"] }))
    await waitFor(() =>
      expect(screen.queryByText(row.target)).not.toBeInTheDocument()
    )
    expect(screen.queryByRole("form")).not.toBeInTheDocument()
    expect(screen.getByText(`Email subscription ${id}`)).toBeInTheDocument()
    fireEvent.click(screen.getByRole("button", { name: "Unsubscribe Email" }))
    await waitFor(() =>
      expect(remove).toHaveBeenCalledWith(id, expect.anything())
    )
    expect(
      await screen.findByText(
        "Unsubscribed. Pending notifications for this subscription were removed."
      )
    ).toBeInTheDocument()
    expect(
      screen.queryByText(`Email subscription ${id}`)
    ).not.toBeInTheDocument()
    expect(list).toHaveBeenCalledTimes(1)
  })
  it("does not re-enable a denied personal-list query in a retry loop and still permits explicit owner cleanup", async () => {
    list.mockResolvedValueOnce({ items: [row], nextCursor: null })
    const { client } = setup()
    await open()
    list.mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Subscription access denied",
      })
    )
    await act(() => client.invalidateQueries({ queryKey: ["subscriptions"] }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Subscription access denied"
    )
    expect(screen.queryByText(row.target)).not.toBeInTheDocument()
    expect(screen.queryByRole("form")).not.toBeInTheDocument()
    expect(screen.getByText(`Email subscription ${id}`)).toBeInTheDocument()
    expect(list).toHaveBeenCalledTimes(2)
  })
  it("keeps a failed unsubscribe visible for explicit retry and never claims success", async () => {
    list.mockResolvedValue({ items: [row], nextCursor: null })
    remove.mockRejectedValue(
      new ApiError(new Response(null, { status: 503 }), {
        title: "Unsubscribe unavailable",
      })
    )
    setup()
    await open()
    fireEvent.click(screen.getByRole("button", { name: "Unsubscribe Email" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Unsubscribe unavailable"
    )
    expect(screen.getByText(row.target)).toBeInTheDocument()
    expect(
      screen.queryByText(/Pending notifications.*removed/)
    ).not.toBeInTheDocument()
    expect(remove).toHaveBeenCalledTimes(1)
  })
  it("resets draft/receipt state on a different resource and never displays previous-scope destinations", async () => {
    list.mockImplementation(async (resource) => ({
      items: resource.scopeId === row.scopeId ? [row] : [],
      nextCursor: null,
    }))
    const { client, rerender } = setup()
    await open()
    fillEmail()
    rerender(
      <QueryClientProvider client={client}>
        <SubscribeMenu scope="type" scopeId="team/type" />
      </QueryClientProvider>
    )
    expect(screen.queryByRole("region")).not.toBeInTheDocument()
    await open()
    expect(screen.getByLabelText("Email address")).toHaveValue("")
    expect(screen.queryByText(row.target)).not.toBeInTheDocument()
    expect(list).toHaveBeenLastCalledWith(
      { scope: "type", scopeId: "team/type" },
      null,
      expect.any(AbortSignal)
    )
  })
  it("uses retained-series authorization for a schedule, not the schedule UUID as an auth scope", async () => {
    permissions.mockResolvedValue({ ...viewer, scope: "series", id })
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    render(
      <QueryClientProvider client={client}>
        <SubscribeMenu
          scope="schedule"
          scopeId="019ed132-2f44-7000-8000-000000000003"
          seriesId={id}
        />
      </QueryClientProvider>
    )
    await open()
    expect(permissions).toHaveBeenCalledWith(
      { scope: "series", id },
      expect.any(AbortSignal)
    )
    expect(list).toHaveBeenCalledWith(
      { scope: "schedule", scopeId: "019ed132-2f44-7000-8000-000000000003" },
      null,
      expect.any(AbortSignal)
    )
  })
})
