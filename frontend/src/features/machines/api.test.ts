import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import {
  formatLabels,
  getMachines,
  machineMetadataSchema,
  machineSearchSchema,
  parseLabels,
  patchMachine,
  type Machine,
} from "./api"

const { get, patch } = vi.hoisted(() => ({ get: vi.fn(), patch: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get, PATCH: patch },
}))
const machine: Machine = {
  id: "12345678-1234-4234-9234-123456789abc",
  name: "worker <script>",
  os: "linux",
  arch: "arm64",
  cores: 8,
  ramMb: 16384,
  maxTasks: 4,
  slotsUsed: 2,
  pool: "production",
  labels: { region: "eu", kind: "gpu" },
  status: "Healthy",
  lastHeartbeatAt: "2026-10-17T00:00:00Z",
  version: "1.2.3",
  cpuPercent: 42,
  ramUsedMb: 1024,
  diskFreeMb: 4096,
  currentReservation: {
    id: "22345678-1234-4234-9234-123456789abc",
    runId: "32345678-1234-4234-9234-123456789abc",
    nodeKey: "work",
    epoch: 3,
    expiresAt: "2026-10-16T00:00:00Z",
    releasePending: true,
  },
}
function respond(items: unknown[] = [machine]) {
  get.mockResolvedValue({
    response: new Response(),
    data: JSON.stringify({ items }),
  })
}
describe("machine API boundaries", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses public generated GET, exact repeated AND labels, cancellation and precision-safe JSON", async () => {
    respond()
    const signal = new AbortController().signal
    const filters = {
      pool: "production",
      os: "linux" as const,
      status: "Healthy" as const,
      label: ["region=eu", "kind=gpu"],
    }
    expect(await getMachines(filters, signal)).toEqual([machine])
    expect(get).toHaveBeenCalledWith("/api/v1/machines", {
      params: { query: filters },
      signal,
      parseAs: "text",
    })
  })
  it.each([
    { pool: "" },
    { pool: " " },
    { pool: "x\n" },
    { pool: "x".repeat(201) },
    { os: "Linux" },
    { status: "0" },
    { label: ["missing"] },
    { label: ["x=1", "x=2"] },
    { label: ["x="] },
    { label: ["=v"] },
    { label: Array.from({ length: 101 }, (_, i) => `${i}=v`) },
  ])("rejects invalid search %j", (input) => {
    expect(machineSearchSchema.safeParse(input).success).toBe(false)
  })
  it.each([
    { id: "bad" },
    { id: "00000000-0000-0000-0000-000000000000" },
    { os: "mac" },
    { status: "unknown" },
    { cores: 0 },
    { ramMb: -1 },
    { maxTasks: 0 },
    { slotsUsed: -1 },
    { cpuPercent: -1 },
    { cpuPercent: 101 },
    { ramUsedMb: 9007199254740992 },
    { labels: { x: "" } },
    { lastHeartbeatAt: "yesterday" },
    {
      currentReservation: {
        ...machine.currentReservation,
        epoch: 9007199254740992,
      },
    },
  ])("rejects malformed server inventory %j", async (change) => {
    respond([{ ...machine, ...change }])
    await expect(getMachines({})).rejects.toThrow()
  })
  it.each([
    { pool: "other" },
    { os: "windows" as const },
    { status: "Draining" as const },
    { label: ["region=us"] },
  ])("rejects responses outside filters %j", async (filters) => {
    respond()
    await expect(getMachines(filters)).rejects.toThrow("inconsistent machine")
  })
  it("rejects duplicate IDs and missing responses; handles genuinely empty inventory", async () => {
    respond([machine, machine])
    await expect(getMachines({})).rejects.toThrow("inconsistent machine")
    get.mockResolvedValue({ response: new Response(), data: undefined })
    await expect(getMachines({})).rejects.toThrow("empty machine")
    respond([])
    expect(await getMachines({})).toEqual([])
  })
  it("preserves exact label values, sorts presentation and detects duplicates, blank keys and overlong fields", () => {
    expect(parseLabels("region=eu\r\nurl=a=b")).toEqual({
      region: "eu",
      url: "a=b",
    })
    expect(formatLabels({ z: "last", a: "first" })).toBe("a=first\nz=last")
    expect(parseLabels("")).toEqual({})
    for (const value of [
      "x=a\nx=b",
      "x=a\n",
      " =v",
      "key= ",
      "x\t=v",
      `${"x".repeat(101)}=v`,
    ])
      expect(() => parseLabels(value)).toThrow()
    expect(
      machineMetadataSchema.safeParse({
        pool: "pool",
        labels: { x: "v".repeat(201) },
      }).success
    ).toBe(false)
  })
  it("PATCH clears labels explicitly, omits unedited pool and requires canonical nonempty identity and metadata", async () => {
    patch.mockResolvedValue({ response: new Response(null, { status: 204 }) })
    await patchMachine(machine.id, { labels: {} })
    expect(patch).toHaveBeenCalledWith("/api/v1/machines/{id}", {
      params: { path: { id: machine.id } },
      body: { labels: {} },
    })
    patch.mockClear()
    await expect(patchMachine(machine.id, {})).rejects.toThrow()
    await expect(patchMachine("bad", { pool: "next" })).rejects.toThrow()
    await expect(patchMachine(machine.id, { pool: " " })).rejects.toThrow()
    expect(patch).not.toHaveBeenCalled()
  })
  it("accepts the server's valid multiline agent name and version without interpreting markup", async () => {
    const row = {
      ...machine,
      name: "worker\n<script>one</script>",
      version: "release\n1",
    }
    respond([row])
    expect(await getMachines({})).toEqual([row])
  })
  it.each([403, 404, 503])(
    "surfaces GET and PATCH failures: %s",
    async (status) => {
      const result = {
        response: new Response(null, { status }),
        error: { title: "Inventory unavailable" },
      }
      get.mockResolvedValue(result)
      patch.mockResolvedValue(result)
      await expect(getMachines({})).rejects.toBeInstanceOf(ApiError)
      await expect(
        patchMachine(machine.id, { pool: "next" })
      ).rejects.toMatchObject({ status })
    }
  )
  it("rejects success-shaped unexpected PATCH responses", async () => {
    patch.mockResolvedValue({ response: new Response() })
    await expect(patchMachine(machine.id, { pool: "next" })).rejects.toThrow(
      "did not confirm"
    )
  })
})
