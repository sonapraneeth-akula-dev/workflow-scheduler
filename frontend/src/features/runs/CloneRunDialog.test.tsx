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
import { beforeEach, expect, it, vi } from "vitest"
import { ApiError, getPermissions } from "@/lib/api/client"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import { CloneRunDialog } from "./CloneRunDialog"
import { cloneRun, getCloneSource } from "./clone-api"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./clone-api", async (original) => ({
  ...(await original<typeof import("./clone-api")>()),
  cloneRun: vi.fn(),
  getCloneSource: vi.fn(),
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
const source = {
  runId: "019ed132-2f44-7000-8000-000000000001",
  workflowId: "team/regression/linear",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  status: 3,
  live: true,
}
const inputs = {
  ...source,
  placement: null,
  variables: jsonObjectSchema.parse(
    parseJson(
      '{"experiment":{"epochs":50,"regions":["us","eu"]},"secret":{"Name":"api/token"},"limit":9223372036854775807,"enabled":true}'
    )
  ),
}
const receipt = { runId: "019ed132-2f44-7000-8000-000000000003" }
const grant = {
  scope: "run" as const,
  id: source.runId,
  viewer: true,
  author: false,
  operator: true,
  admin: false,
}
function show(props = source) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: 3, retryDelay: 0 },
    },
  })
  const tree = (next = props) => (
    <QueryClientProvider client={client}>
      <CloneRunDialog {...next} />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return {
    client,
    update: (next: typeof source) => rendered.rerender(tree(next)),
  }
}
async function open() {
  fireEvent.click(await screen.findByRole("button", { name: "Clone run" }))
  const dialog = await screen.findByRole("dialog", { name: "Clone run" })
  await within(dialog).findByRole("textbox", { name: "Variables (JSON)" })
  return dialog
}
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
  vi.mocked(getCloneSource).mockResolvedValue(inputs)
  vi.mocked(cloneRun).mockResolvedValue(receipt)
})

it.each([false, true])(
  "hides cloning from Viewer/Author without reading source variables (author=%s)",
  async (author) => {
    vi.mocked(getPermissions).mockResolvedValue({
      ...grant,
      author,
      operator: false,
    })
    const { client } = show()
    await waitFor(() => expect(client.isFetching()).toBe(0))
    expect(
      screen.queryByRole("button", { name: "Clone run" })
    ).not.toBeInTheDocument()
    expect(getCloneSource).not.toHaveBeenCalled()
    expect(cloneRun).not.toHaveBeenCalled()
  }
)

it.each([0, 1, 2])(
  "does not clone nonterminal run status %i",
  async (status) => {
    const { client } = show({ ...source, status })
    await waitFor(() => expect(client.isFetching()).toBe(0))
    expect(
      screen.queryByRole("button", { name: "Clone run" })
    ).not.toBeInTheDocument()
    expect(getCloneSource).not.toHaveBeenCalled()
  }
)

it.each([3, 4, 5, 6])(
  "offers a fresh clone for every terminal status %i",
  async (status) => {
    show({ ...source, status })
    expect(
      await screen.findByRole("button", { name: "Clone run" })
    ).toBeVisible()
  }
)

it("prefills pretty nested JSON without flattening or rounding and submits edited variables only after confirmation", async () => {
  const { client } = show()
  const invalidate = vi.spyOn(client, "invalidateQueries")
  expect(getCloneSource).not.toHaveBeenCalled()
  const dialog = await open()
  const editor = within(dialog).getByRole("textbox", {
    name: "Variables (JSON)",
  })
  expect(editor).toHaveValue(JSON.stringify(inputs.variables, null, 2))
  expect(dialog).toHaveTextContent(source.versionId)
  expect(dialog).toHaveTextContent("no successful outputs are reused")
  expect(cloneRun).not.toHaveBeenCalled()
  const edited =
    '{"experiment":{"epochs":100,"regions":["eu"]},"secret":{"Name":"api/token"},"limit":9223372036854775807,"enabled":false}'
  fireEvent.change(editor, { target: { value: edited } })
  await userEvent.click(
    within(dialog).getByRole("button", { name: "Format JSON" })
  )
  expect(editor).toHaveValue(JSON.stringify(parseJson(edited), null, 2))
  within(dialog).getByRole("button", { name: "Confirm clone" }).focus()
  await userEvent.keyboard("{Enter}")
  expect(await within(dialog).findByRole("status")).toHaveTextContent(
    "Clone accepted"
  )
  expect(cloneRun).toHaveBeenCalledExactlyOnceWith(
    source.runId,
    { variables: parseJson(edited) },
    expect.any(String)
  )
  expect(
    within(dialog).getByRole("link", {
      name: `Open cloned run ${receipt.runId}`,
    })
  ).toHaveAttribute("href", `/runs/${receipt.runId}`)
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ["run-history"] })
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ["runs"] })
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ["catalog"] })
  expect(editor).toBeDisabled()
  expect(
    within(dialog).getByRole("button", { name: "Confirm clone" })
  ).toBeDisabled()
})

it.each(["{", "[]", "null", '"not an object"'])(
  "rejects invalid variable object %s before submission",
  async (text) => {
    show()
    const dialog = await open()
    fireEvent.change(
      within(dialog).getByRole("textbox", { name: "Variables (JSON)" }),
      { target: { value: text } }
    )
    expect(within(dialog).getByRole("alert")).toBeVisible()
    expect(
      within(dialog).getByRole("button", { name: "Confirm clone" })
    ).toBeDisabled()
    fireEvent.submit(within(dialog).getByRole("form"))
    expect(cloneRun).not.toHaveBeenCalled()
  }
)

it("rejects payloads over the UTF-8 byte limit and preserves edits after a server validation error", async () => {
  show()
  const dialog = await open()
  const editor = within(dialog).getByRole("textbox", {
    name: "Variables (JSON)",
  })
  fireEvent.change(editor, {
    target: { value: JSON.stringify({ text: "é".repeat(524_288) }) },
  })
  expect(within(dialog).getByRole("alert")).toHaveTextContent("1 MiB")
  expect(
    within(dialog).getByRole("button", { name: "Confirm clone" })
  ).toBeDisabled()
  fireEvent.change(editor, { target: { value: '{"experiment":{"epochs":0}}' } })
  vi.mocked(cloneRun).mockRejectedValueOnce(
    new ApiError(new Response(null, { status: 400 }), {
      title: "Clone variable validation failed",
    })
  )
  fireEvent.submit(within(dialog).getByRole("form"))
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "Clone variable validation failed"
  )
  expect(editor).toBeEnabled()
  expect(editor).toHaveValue('{"experiment":{"epochs":0}}')
  const firstKey = vi.mocked(cloneRun).mock.calls[0]![2]
  fireEvent.change(editor, {
    target: { value: '{"experiment":{"epochs":25}}' },
  })
  fireEvent.submit(within(dialog).getByRole("form"))
  expect(await within(dialog).findByRole("status")).toHaveTextContent(
    "Clone accepted"
  )
  expect(vi.mocked(cloneRun).mock.calls[1]![2]).not.toBe(firstKey)
})

it("freezes one exact request and key across an uncertain acceptance, close/reopen and explicit retry", async () => {
  vi.mocked(cloneRun).mockRejectedValueOnce(
    new Error("Connection lost after acceptance.")
  )
  show()
  let dialog = await open()
  fireEvent.submit(within(dialog).getByRole("form"))
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "Connection lost"
  )
  expect(cloneRun).toHaveBeenCalledTimes(1)
  const submission = vi.mocked(cloneRun).mock.calls[0]!
  expect(within(dialog).getByRole("textbox")).toBeDisabled()
  fireEvent.click(within(dialog).getByRole("button", { name: "Close" }))
  dialog = await open()
  await userEvent.click(
    within(dialog).getByRole("button", { name: "Retry clone" })
  )
  expect(await within(dialog).findByRole("status")).toHaveTextContent(
    "Clone accepted"
  )
  expect(vi.mocked(cloneRun).mock.calls).toEqual([submission, submission])
  expect(getCloneSource).toHaveBeenCalledTimes(1)
})

it("blocks double submissions before acceptance resolves", async () => {
  let complete!: (value: typeof receipt) => void
  vi.mocked(cloneRun).mockReturnValue(
    new Promise((resolve) => {
      complete = resolve
    })
  )
  show()
  const dialog = await open()
  fireEvent.submit(within(dialog).getByRole("form"))
  fireEvent.submit(within(dialog).getByRole("form"))
  expect(
    await within(dialog).findByRole("button", { name: "Accepting clone..." })
  ).toBeDisabled()
  expect(cloneRun).toHaveBeenCalledTimes(1)
  complete(receipt)
  expect(await within(dialog).findByRole("status")).toHaveTextContent(
    "Clone accepted"
  )
  fireEvent.submit(within(dialog).getByRole("form"))
  expect(cloneRun).toHaveBeenCalledTimes(1)
})

it("fails closed when the source read fails and retries without inventing defaults", async () => {
  vi.mocked(getCloneSource).mockRejectedValueOnce(
    new Error("Source unavailable")
  )
  show()
  fireEvent.click(await screen.findByRole("button", { name: "Clone run" }))
  const dialog = await screen.findByRole("dialog", { name: "Clone run" })
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "Source unavailable"
  )
  expect(within(dialog).queryByRole("textbox")).not.toBeInTheDocument()
  expect(cloneRun).not.toHaveBeenCalled()
  await userEvent.click(
    within(dialog).getByRole("button", { name: "Retry clone source" })
  )
  expect(await within(dialog).findByRole("textbox")).toHaveValue(
    JSON.stringify(inputs.variables, null, 2)
  )
})

it("blocks edited submissions after permission revocation or lost live confirmation", async () => {
  const shown = show()
  const dialog = await open()
  const form = within(dialog).getByRole("form")
  shown.update({ ...source, live: false })
  expect(within(dialog).queryByRole("textbox")).not.toBeInTheDocument()
  fireEvent.submit(form)
  expect(cloneRun).not.toHaveBeenCalled()
  shown.update(source)
  vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
  await shown.client.invalidateQueries({ queryKey: ["permissions"] })
  await waitFor(() =>
    expect(within(dialog).queryByRole("textbox")).not.toBeInTheDocument()
  )
  fireEvent.submit(form)
  expect(cloneRun).not.toHaveBeenCalled()
})
