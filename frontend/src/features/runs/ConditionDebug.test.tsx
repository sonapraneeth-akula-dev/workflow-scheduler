import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { ConditionDebug } from "./ConditionDebug"
import { getRunConditions, type RunConditions } from "./conditions-api"

vi.mock("./conditions-api", () => ({ getRunConditions: vi.fn() }))
const runId = "019ed132-2f44-7000-8000-000000000001"
const versionId = "019ed132-2f44-7000-8000-000000000002"
const item: RunConditions["conditions"][number] = {
  entityKey: "test",
  variableKey: "runFinalTest",
  nodeKey: null,
  outputKey: null,
  expected: true,
  actual: false,
  outcome: "NotMatched",
  evaluatedAt: "2026-10-20T03:00:00Z",
  evaluatedSequence: "32",
  sourceKind: "RunVariable",
  declaredDefault: true,
  sourceRunId: runId,
  producerTaskKey: null,
  producerAttempt: null,
  producedAt: null,
}
const result: RunConditions = {
  runId,
  versionId,
  lastSeq: "33",
  conditions: [item],
}
function show(
  props: { entityKey?: string; blockedByNode?: string; revision?: string } = {}
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const view = (revision: string) => (
    <QueryClientProvider client={client}>
      <ConditionDebug
        runId={runId}
        versionId={versionId}
        entityKey={props.entityKey ?? "test"}
        blockedByNode={props.blockedByNode}
        revision={revision}
      />
    </QueryClientProvider>
  )
  return { ...render(view(props.revision ?? "initial")), view }
}
describe("ConditionDebug", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getRunConditions).mockResolvedValue(result)
  })
  it("loads only on disclosure, supports keyboard and shows exact variable evidence without guessing origin", async () => {
    const user = userEvent.setup()
    show()
    expect(getRunConditions).not.toHaveBeenCalled()
    const button = screen.getByRole("button", {
      name: "Show debug details for test",
    })
    button.focus()
    await user.keyboard("{Enter}")
    expect(button).toHaveAttribute("aria-expanded", "true")
    const panel = await screen.findByRole("region", {
      name: "Condition debug details for test",
    })
    await waitFor(() =>
      expect(panel).toHaveTextContent('variables["runFinalTest"] === true')
    )
    expect(button).toHaveAttribute("aria-controls", panel.id)
    expect(panel).toHaveTextContent("Actual valuefalse")
    expect(panel).toHaveTextContent("Declared defaulttrue")
    expect(panel).toHaveTextContent("Captured run variable: runFinalTest")
    expect(panel).toHaveTextContent(
      "Original default/override source was not recorded"
    )
    expect(panel).toHaveTextContent(item.evaluatedAt!)
    fireEvent.click(button)
    expect(
      screen.queryByRole("region", { name: "Condition debug details for test" })
    ).not.toBeInTheDocument()
  })
  it("shows exact committing task, attempt, output reference and reused source", async () => {
    vi.mocked(getRunConditions).mockResolvedValue({
      ...result,
      conditions: [
        {
          ...item,
          variableKey: null,
          nodeKey: "dataset",
          outputKey: "cacheMissing",
          sourceKind: "NodeOutput",
          declaredDefault: null,
          sourceRunId: "019ed132-2f44-7000-8000-000000000003",
          producerTaskKey: "dataset/check-cache",
          producerAttempt: 2,
          producedAt: "2026-10-20T02:59:59Z",
        },
      ],
    })
    show()
    fireEvent.click(
      screen.getByRole("button", { name: "Show debug details for test" })
    )
    expect(
      await screen.findByText("dataset/check-cache, attempt 2")
    ).toBeVisible()
    expect(
      screen.getByText('outputs["dataset"]["cacheMissing"] === true')
    ).toBeVisible()
    expect(screen.getByText(/reused node outputs/)).toBeVisible()
  })
  it("explains inherited node skip without inventing a child decision", async () => {
    show({ entityKey: "test/evaluate", blockedByNode: "test" })
    fireEvent.click(
      screen.getByRole("button", {
        name: "Show debug details for test/evaluate",
      })
    )
    expect(
      await screen.findByText(/Blocked by node condition: test/)
    ).toBeVisible()
    expect(await screen.findByText("Condition on test")).toBeVisible()
    expect(
      screen.queryByText("Condition on test/evaluate")
    ).not.toBeInTheDocument()
  })
  it.each([
    [null, "Not recorded; no value is inferred"],
    ["MissingValue", "Missing value"],
    ["InvalidType", "Non-Boolean value (not disclosed)"],
  ] as const)(
    "shows unavailable value for outcome %s, never substitutes a default",
    async (outcome, text) => {
      vi.mocked(getRunConditions).mockResolvedValue({
        ...result,
        conditions: [
          {
            ...item,
            outcome,
            actual: null,
            evaluatedAt: null,
            evaluatedSequence: null,
          },
        ],
      })
      show()
      fireEvent.click(
        screen.getByRole("button", { name: "Show debug details for test" })
      )
      expect(await screen.findByText(text, { exact: false })).toBeVisible()
      expect(screen.getAllByText("Not recorded")).toHaveLength(2)
    }
  )
  it.each([401, 403, 503])(
    "hides stale evidence immediately during refresh and after HTTP %i",
    async (status) => {
      let reject: ((error: Error) => void) | undefined
      show()
      fireEvent.click(
        screen.getByRole("button", { name: "Show debug details for test" })
      )
      await screen.findByText("Captured run variable: runFinalTest")
      vi.mocked(getRunConditions).mockImplementation(
        () =>
          new Promise((_, fail) => {
            reject = fail
          })
      )
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh debug details" })
      )
      await waitFor(() => expect(reject).toBeDefined())
      expect(
        screen.queryByText("Captured run variable: runFinalTest")
      ).not.toBeInTheDocument()
      await act(async () =>
        reject!(
          new ApiError(new Response(null, { status }), {
            title: "Debug access failed",
          })
        )
      )
      expect(await screen.findByText("Debug access failed")).toBeVisible()
      if (status !== 503)
        expect(
          screen.getByText(/Previously loaded condition details are hidden/)
        ).toBeVisible()
    }
  )
  it("refetches on a new durable condition revision", async () => {
    vi.mocked(getRunConditions)
      .mockResolvedValueOnce({
        ...result,
        conditions: [
          {
            ...item,
            actual: null,
            outcome: null,
            evaluatedAt: null,
            evaluatedSequence: null,
          },
        ],
      })
      .mockResolvedValue(result)
    const { rerender, view } = show()
    fireEvent.click(
      screen.getByRole("button", { name: "Show debug details for test" })
    )
    await screen.findByText(/Not recorded; no value is inferred/)
    rerender(view("NotMatched"))
    await waitFor(() =>
      expect(
        screen.getByRole("region", { name: "Condition debug details for test" })
      ).toHaveTextContent("Actual valuefalse")
    )
    expect(getRunConditions).toHaveBeenCalledTimes(2)
  })
})
