import { useState } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, expect, it, vi } from "vitest"
import type { RunPlacement } from "@/lib/live/runApi"
import { PlacementFields } from "./PlacementFields"
import { rerunPlacementOptions, triggerPlacementOptions } from "./placement-api"

vi.mock("./placement-api", () => ({
  triggerPlacementOptions: vi.fn(),
  rerunPlacementOptions: vi.fn(),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const request = {
  workflowId: "team/type/workflow",
  version: runId,
  variables: {},
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(triggerPlacementOptions).mockResolvedValue([
    { id: runId, name: "eligible" },
  ])
  vi.mocked(rerunPlacementOptions).mockResolvedValue([
    { id: runId, name: "rerun-eligible" },
  ])
})

function show(rerun = false, disabled = false, valid = true) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  function Form() {
    const [placement, setPlacement] = useState<RunPlacement>()
    return (
      <>
        <PlacementFields
          value={placement}
          onChange={setPlacement}
          source={rerun ? { runId } : request}
          disabled={disabled}
          valid={valid}
        />
        <output aria-label="Selected placement">
          {JSON.stringify(placement)}
        </output>
      </>
    )
  }
  return render(
    <QueryClientProvider client={client}>
      <Form />
    </QueryClientProvider>
  )
}

it("loads only server-eligible machines after opt-in and preserves auto/named/disabled selection", async () => {
  show()
  expect(triggerPlacementOptions).not.toHaveBeenCalled()
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Run on a single machine" })
  )
  await screen.findByRole("option", { name: "eligible" })
  expect(triggerPlacementOptions).toHaveBeenCalledWith(
    request,
    expect.any(AbortSignal)
  )
  fireEvent.change(screen.getByLabelText("Machine", { exact: true }), {
    target: { value: "eligible" },
  })
  expect(screen.getByLabelText("Selected placement")).toHaveTextContent(
    '"machine":"eligible"'
  )
  fireEvent.click(screen.getByRole("checkbox"))
  expect(screen.getByLabelText("Selected placement")).toHaveTextContent(
    '{"singleMachine":false}'
  )
})

it("uses the source run's pinned variables for rerun eligibility rather than current workflow defaults", async () => {
  show(true)
  fireEvent.click(screen.getByRole("checkbox"))
  await screen.findByRole("option", { name: "rerun-eligible" })
  expect(rerunPlacementOptions).toHaveBeenCalledWith(
    runId,
    expect.any(AbortSignal)
  )
  expect(triggerPlacementOptions).not.toHaveBeenCalled()
})

it("does not inspect machine inventory for invalid variables or disabled controls", async () => {
  show(false, false, false)
  fireEvent.click(screen.getByRole("checkbox"))
  expect(
    screen.getByText("Complete valid variables to check eligible machines.")
  ).toBeInTheDocument()
  expect(screen.getByRole("combobox")).toBeDisabled()
  await waitFor(() => expect(triggerPlacementOptions).not.toHaveBeenCalled())
})

it("surfaces unavailable eligibility instead of manufacturing a fallback machine list", async () => {
  vi.mocked(triggerPlacementOptions).mockRejectedValue(
    new Error("Eligibility unavailable")
  )
  show()
  fireEvent.click(screen.getByRole("checkbox"))
  await screen.findByText("Eligibility unavailable")
  expect(screen.getByRole("combobox")).toBeDisabled()
  expect(
    screen.queryByRole("option", { name: "eligible" })
  ).not.toBeInTheDocument()
})
