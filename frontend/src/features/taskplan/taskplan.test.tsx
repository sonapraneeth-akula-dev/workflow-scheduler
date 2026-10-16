import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getVersionPlan, type VersionPlan } from "./api"
import { describePlacement, TaskPlanPage } from "./TaskPlanPage"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))

const workflowId = "team/type/workflow"
const versionId = "019ed132-2f44-7000-8000-000000000001"
const plan: VersionPlan = {
  versionId,
  workflowId,
  version: "1.0.0",
  runTimeout: "01:00:00",
  stages: [],
  placement: null,
  nodes: [
    {
      key: "work",
      dependencies: [],
      mode: "Parallel",
      maxParallel: 2,
      failure: "Soft",
      timeout: "00:01:00",
      placement: null,
      tasks: [
        {
          key: "sdk",
          action: {
            kind: "SdkFunction",
            declaration: "Example.Workflow.Execute",
          },
          secretReference: null,
          requestTimeout: null,
          failure: "Hard",
          placement: null,
          policy: {
            attemptTimeout: "00:00:12",
            autoRetry: true,
            retry: {
              retries: 2,
              on: ["Timeout"],
              backoff: {
                kind: "Exponential",
                delay: "00:00:01",
                maxDelay: "00:00:08",
              },
            },
          },
        },
      ],
    },
  ],
}
const respond = (data: unknown) =>
  get.mockResolvedValue({ data, response: new Response() })
function show(taskKey?: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <TaskPlanPage
        workflowId={workflowId}
        versionId={versionId}
        taskKey={taskKey}
      />
    </QueryClientProvider>
  )
  return client
}

describe("immutable task plan boundaries and states", () => {
  beforeEach(() => vi.resetAllMocks())

  it("requests the exact pinned UUID and strips unexpected private manifest fields", async () => {
    respond({
      ...plan,
      variables: { password: "private-value" },
      source: "private-source",
    })
    const signal = new AbortController().signal
    expect(await getVersionPlan(workflowId, versionId, signal)).toEqual(plan)
    expect(get).toHaveBeenCalledWith(
      "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}",
      {
        params: {
          path: {
            team: "team",
            type: "type",
            workflow: "workflow",
            version: versionId,
          },
        },
        signal,
      }
    )
  })

  it.each([
    { ...plan, versionId: "019ed132-2f44-7000-8000-000000000002" },
    { ...plan, workflowId: "other/type/workflow" },
    { ...plan, nodes: [plan.nodes[0], plan.nodes[0]] },
    { ...plan, stages: ["absent"] },
    {
      ...plan,
      nodes: [
        {
          ...plan.nodes[0],
          dependencies: [{ node: "absent", when: "OnSuccess" }],
        },
      ],
    },
    {
      ...plan,
      nodes: [
        {
          ...plan.nodes[0],
          tasks: [plan.nodes[0]!.tasks[0], plan.nodes[0]!.tasks[0]],
        },
      ],
    },
    {
      ...plan,
      placement: {
        selector: {
          os: { literal: null, variable: null },
          pool: null,
          labels: [],
          machines: [],
        },
        affinity: null,
      },
    },
  ])(
    "rejects mismatched identities and inconsistent graph/placement data",
    async (body) => {
      respond(body)
      await expect(getVersionPlan(workflowId, versionId)).rejects.toThrow()
    }
  )

  it.each(["team/type", "team/type/name/extra", "team/type/invalid\\name"])(
    "rejects invalid workflow identity before sending %s",
    async (workflow) => {
      await expect(getVersionPlan(workflow, versionId)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )

  it("announces loading then shows ordered actions, effective policies and selected task", async () => {
    respond(plan)
    show("work/sdk")
    expect(screen.getByRole("status")).toHaveTextContent(
      "Loading historical task plan"
    )
    expect(
      await screen.findByText("SdkFunction: Example.Workflow.Execute")
    ).toBeInTheDocument()
    expect(screen.getByRole("region", { name: "Node work" })).toHaveTextContent(
      "Parallel task order; concurrency: 2"
    )
    expect(screen.getByRole("listitem")).toHaveAttribute("aria-current", "true")
    expect(screen.getByRole("listitem")).toHaveTextContent("retry budget: 2")
    expect(screen.getByRole("listitem")).toHaveTextContent(
      "Attempt timeout: 00:00:12"
    )
  })

  it("does not invent a function declaration for older manifests or an absent selected task", async () => {
    respond({
      ...plan,
      nodes: [
        {
          ...plan.nodes[0],
          tasks: [{ ...plan.nodes[0]!.tasks[0], action: null }],
        },
      ],
    })
    show("work/missing")
    expect(
      await screen.findByText(
        "Declaration unavailable in this historical manifest."
      )
    ).toBeInTheDocument()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "This task is not part of the pinned version"
    )
    expect(
      screen.queryByText("SdkFunction: Example.Workflow.Execute")
    ).not.toBeInTheDocument()
  })

  it("surfaces an authorized read failure without claiming an empty plan", async () => {
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      error: { title: "Forbidden" },
    })
    await expect(getVersionPlan(workflowId, versionId)).rejects.toBeInstanceOf(
      ApiError
    )
    show()
    expect(await screen.findByRole("alert")).toHaveTextContent("Forbidden")
    expect(screen.queryByText("No declared nodes.")).not.toBeInTheDocument()
  })

  it("removes a previously loaded plan when authorization is revoked on refetch", async () => {
    respond(plan)
    const client = show()
    await screen.findByText("SdkFunction: Example.Workflow.Execute")
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      error: { title: "Access revoked" },
    })
    await act(async () =>
      client.invalidateQueries({
        queryKey: ["version-plan", workflowId, versionId],
      })
    )
    await waitFor(() =>
      expect(
        screen.queryByText("SdkFunction: Example.Workflow.Execute")
      ).not.toBeInTheDocument()
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Access revoked")
  })

  it("describes literals, unbound variable references, labels, machine list and affinity without resolving values", () => {
    expect(
      describePlacement({
        selector: {
          os: { literal: "linux", variable: null },
          pool: { literal: null, variable: "pool" },
          labels: [
            { key: "gpu", value: null },
            { key: "region", value: { literal: "west", variable: null } },
          ],
          machines: [{ literal: null, variable: "target" }],
        },
        affinity: { kind: "SameMachineAs", nodeKey: "prepare" },
      })
    ).toBe(
      "OS: linux; Pool: variable(pool); Label: gpu; Label: region=west; Machines: variable(target); Affinity: SameMachineAs (prepare)"
    )
  })
})
