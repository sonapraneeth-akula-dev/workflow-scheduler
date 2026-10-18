import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError, getPermissions } from "@/lib/api/client"
import { parseJson } from "@/lib/json"
import { getTriggerSchema, triggerRun } from "./trigger-api"
import { triggerSchema } from "./variables"
import { TriggerDialog } from "./TriggerDialog"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./trigger-api", async (original) => ({
  ...(await original<typeof import("./trigger-api")>()),
  getTriggerSchema: vi.fn(),
  triggerRun: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
  }: {
    params: { id: string }
    children: ReactNode
  }) => <a href={`/runs/${params.id}`}>{children}</a>,
}))

const workflowId = "team/type/workflow"
const versionId = "019ed132-2f44-7000-8000-000000000001"
const runId = "019ed132-2f44-7000-8000-000000000002"
const grant = {
  scope: "type" as const,
  id: "team/type",
  viewer: true,
  author: false,
  operator: true,
  admin: false,
}
const schema = triggerSchema.parse(
  parseJson(`{
  "workflowId":"${workflowId}","versionId":"${versionId}","version":"1.0.0",
  "variables":[
    {"key":"count","type":"Int","required":true,"default":3,"allowedValues":[]},
    {"key":"sequence","type":"Long","required":false,"default":9223372036854775807,"allowedValues":[]},
    {"key":"names","type":"StringList","required":false,"default":null,"allowedValues":[]},
    {"key":"mode","type":"Enum","required":true,"default":"","allowedValues":["","Production"]}
  ]
}`)
)
function show() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: 3, retryDelay: 0 },
    },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <TriggerDialog workflowId={workflowId} />
      </QueryClientProvider>
    ),
    client,
  }
}
async function open() {
  fireEvent.click(
    await screen.findByRole("button", { name: "Trigger run" })
  )
  return screen.findByRole("form", { name: "Trigger run" })
}

describe("workflow trigger dialog", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    Object.defineProperties(HTMLDialogElement.prototype, {
      showModal: {
        configurable: true,
        value: function (this: HTMLDialogElement) {
          this.setAttribute("open", "")
        },
      },
      close: {
        configurable: true,
        value: function (this: HTMLDialogElement) {
          this.removeAttribute("open")
          this.dispatchEvent(new Event("close"))
        },
      },
    })
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(getTriggerSchema).mockResolvedValue(schema)
    vi.mocked(triggerRun).mockResolvedValue({ runId })
  })
  it.each([false, true])(
    "does not fetch defaults or show trigger for Viewer/Author (author=%s)",
    async (author) => {
      vi.mocked(getPermissions).mockResolvedValue({
        ...grant,
        author,
        operator: false,
      })
      const { client } = show()
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "type", "team/type"])?.status
        ).toBe("success")
      )
      expect(
        screen.queryByRole("button", { name: "Trigger run" })
      ).not.toBeInTheDocument()
      expect(getTriggerSchema).not.toHaveBeenCalled()
    }
  )
  it("prefills defaults, validates inline and preserves exact Int64 request and captured version", async () => {
    const { client } = show()
    const invalidate = vi.spyOn(client, "invalidateQueries")
    const form = await open()
    expect(within(form).getByLabelText("count", { exact: true })).toHaveValue(
      "3"
    )
    expect(
      within(form).getByLabelText("sequence", { exact: true })
    ).toHaveValue("9223372036854775807")
    expect(within(form).getByLabelText("mode", { exact: true })).toHaveValue(
      "0"
    )
    fireEvent.change(within(form).getByLabelText("count", { exact: true }), {
      target: { value: "2147483648" },
    })
    expect(
      within(form).getByRole("button", { name: "Accept run" })
    ).toBeDisabled()
    expect(
      within(form).getByLabelText("count", { exact: true })
    ).toHaveAttribute("aria-invalid", "true")
    expect(
      within(form).getByText(/from -2147483648 to 2147483647/)
    ).toBeVisible()
    fireEvent.change(within(form).getByLabelText("count", { exact: true }), {
      target: { value: "7" },
    })
    fireEvent.click(within(form).getByRole("checkbox", { name: "Set names" }))
    fireEvent.change(within(form).getByLabelText("names", { exact: true }), {
      target: { value: '["a",""]' },
    })
    const user = userEvent.setup()
    within(form).getByRole("button", { name: "Accept run" }).focus()
    await user.keyboard("{Enter}")
    const receipt = await screen.findByRole("status")
    expect(receipt).toHaveTextContent("accepted")
    expect(screen.getByRole("link", { name: runId })).toHaveAttribute(
      "href",
      `/runs/${runId}`
    )
    const call = vi.mocked(triggerRun).mock.calls[0]!
    expect(JSON.stringify(call[0])).toBe(
      `{"workflowId":"${workflowId}","version":"${versionId}","variables":{"count":7,"sequence":9223372036854775807,"names":["a",""],"mode":""}}`
    )
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["run-history"] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["catalog"] })
  })
  it("freezes ambiguous requests, preserves exact key/payload across close and retry and never auto-retries", async () => {
    vi.mocked(triggerRun)
      .mockRejectedValueOnce(new TypeError("Response lost."))
      .mockResolvedValue({ runId })
    show()
    await open()
    fireEvent.click(screen.getByRole("button", { name: "Accept run" }))
    expect(await screen.findByRole("alert")).toHaveTextContent("Response lost.")
    expect(triggerRun).toHaveBeenCalledTimes(1)
    const first = vi.mocked(triggerRun).mock.calls[0]!
    expect(screen.getByLabelText("count", { exact: true })).toBeDisabled()
    fireEvent.click(screen.getByRole("button", { name: "Close" }))
    await open()
    fireEvent.click(screen.getByRole("button", { name: "Retry accept run" }))
    await screen.findByRole("status")
    expect(vi.mocked(triggerRun).mock.calls[1]).toEqual(first)
    fireEvent.click(screen.getByRole("button", { name: "Prepare another run" }))
    fireEvent.click(screen.getByRole("button", { name: "Accept run" }))
    await waitFor(() => expect(triggerRun).toHaveBeenCalledTimes(3))
    expect(vi.mocked(triggerRun).mock.calls[2]![1]).not.toBe(first[1])
  })
  it("shows authoritative bracket-path field errors, permits corrections and uses a new key after definite rejection", async () => {
    vi.mocked(triggerRun).mockRejectedValueOnce(
      new ApiError(new Response(null, { status: 400 }), {
        title: "Run validation failed",
        errors: [{ path: '$.variables["count"]', message: "Correct count." }],
      })
    )
    show()
    await open()
    fireEvent.click(screen.getByRole("button", { name: "Accept run" }))
    await waitFor(() =>
      expect(screen.getByLabelText("count", { exact: true })).toHaveAttribute(
        "aria-invalid",
        "true"
      )
    )
    expect(screen.getByLabelText("count", { exact: true })).not.toBeDisabled()
    fireEvent.change(screen.getByLabelText("count", { exact: true }), {
      target: { value: "5" },
    })
    fireEvent.click(screen.getByRole("button", { name: "Accept run" }))
    await screen.findByRole("status")
    expect(vi.mocked(triggerRun).mock.calls[1]![1]).not.toBe(
      vi.mocked(triggerRun).mock.calls[0]![1]
    )
  })
  it("blocks duplicate acceptance and fails closed after permission revocation without losing the draft", async () => {
    vi.mocked(triggerRun).mockReturnValue(new Promise(() => {}))
    const { client } = show()
    await open()
    await userEvent
      .setup()
      .dblClick(screen.getByRole("button", { name: "Accept run" }))
    expect(triggerRun).toHaveBeenCalledTimes(1)
    expect(
      screen.getByRole("button", { name: "Accepting run..." })
    ).toBeDisabled()
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
    await client.invalidateQueries({ queryKey: ["permissions"] })
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Operator permission"
    )
    expect(screen.getByLabelText("count", { exact: true })).toHaveValue("3")
    expect(screen.getByLabelText("count", { exact: true })).not.toBeVisible()
  })
  it("reports failed schema reads without a partial form and supports explicit retry", async () => {
    vi.mocked(getTriggerSchema)
      .mockRejectedValueOnce(new Error("Schema unavailable."))
      .mockResolvedValue(schema)
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: "Trigger run" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Schema unavailable."
    )
    expect(screen.queryByRole("form")).not.toBeInTheDocument()
    fireEvent.click(
      screen.getByRole("button", { name: "Retry variable schema" })
    )
    expect(await screen.findByRole("form")).toBeVisible()
  })
})
