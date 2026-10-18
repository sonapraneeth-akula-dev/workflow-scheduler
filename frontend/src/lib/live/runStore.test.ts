import { waitFor } from "@testing-library/react"
import { describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import type { RunDelta } from "./messages"
import type {
  readRunEvents,
  readRunSnapshot,
  RunEvents,
  RunSnapshot,
} from "./runApi"
import { RunStore } from "./runStore"
import type { LiveSnapshot } from "./subscriptions"

const runId = "019ed132-2f44-7000-8000-000000000001"
const snapshot: RunSnapshot = {
  runId,
  workflowId: "team/type/workflow",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  seriesId: null,
  status: 0,
  lastSeq: 0,
  nodes: { build: 0 },
  tasks: { "build/task": 0 },
  abort: null,
  endedAt: null,
  parentRunId: null,
  reusedNodes: {},
}
const change = (seq: number): RunDelta["events"][number] => ({
  seq,
  at: "2026-10-16T02:00:00Z",
  entity: 2,
  key: "build/task",
  state: 3,
  abort: null,
})
const page = (from: number, to: number): RunEvents => ({
  runId,
  lastSeq: to,
  events: Array.from({ length: to - from }, (_, index) =>
    change(from + index + 1)
  ),
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => {
    resolve = complete
  })
  return { promise, resolve }
}

function fixture(status: LiveSnapshot["status"] = "live") {
  let current: LiveSnapshot = {
    resource: { scope: "run", id: runId },
    status,
    delta: null,
    error: null,
    resyncVersion: 0,
    reportVersion: 0,
  }
  const callbacks = new Set<() => void>()
  const release = vi.fn()
  const retry = vi.fn()
  const manager = {
    observe: vi.fn(() => ({
      getSnapshot: () => current,
      subscribe: (listener: () => void) => {
        callbacks.add(listener)
        return () => {
          callbacks.delete(listener)
          release()
        }
      },
      retry,
    })),
  }
  const reads = {
    snapshot: vi.fn<typeof readRunSnapshot>().mockResolvedValue(snapshot),
    events: vi
      .fn<typeof readRunEvents>()
      .mockRejectedValue(new Error("Unexpected resync read.")),
  }
  const store = new RunStore(runId, manager, reads)
  const listener = vi.fn()
  const stop = store.subscribe(listener)
  const emit = (patch: Partial<LiveSnapshot>) => {
    current = { ...current, ...patch }
    for (const callback of callbacks) callback()
  }
  const emitEvents = (...events: RunDelta["events"]) =>
    emit({ delta: { runId, events } })
  return {
    store,
    reads,
    manager,
    listener,
    stop,
    retry,
    release,
    emit,
    emitEvents,
  }
}

const ready = async (store: RunStore) =>
  waitFor(() => expect(store.getSnapshot().status).toBe("live"))

describe("ordered authoritative run recovery", () => {
  it("advances progress sequence without changing graph references, waits, placement, status or timers", async () => {
    const f = fixture("connecting")
    f.reads.snapshot.mockResolvedValueOnce({
      ...snapshot,
      tasks: { "build/task": 3 },
      taskWaitReasons: {},
    })
    f.emit({ status: "live" })
    await ready(f.store)
    const before = f.store.getSnapshot().run!
    f.emitEvents({
      ...change(1),
      attempt: 2,
      progress: { sequence: 1, phase: 3, occurredAt: change(1).at },
    })
    const after = f.store.getSnapshot().run!
    expect(after).toEqual({ ...before, lastSeq: 1 })
    expect(after.tasks).toBe(before.tasks)
    expect(after.nodes).toBe(before.nodes)
    expect(after.taskWaitReasons).toBe(before.taskWaitReasons)
    expect(f.reads.events).not.toHaveBeenCalled()
    f.stop()
  })

  it("fails closed when progress names an inactive or unknown task", async () => {
    const f = fixture()
    await ready(f.store)
    f.emitEvents({
      ...change(1),
      attempt: 1,
      progress: { sequence: 1, phase: 3, occurredAt: change(1).at },
    })
    expect(f.store.getSnapshot().status).toBe("error")
    expect(f.store.getSnapshot().error?.message).toContain("active run task")
    f.stop()
  })

  it("hydrates durable placement waits, applies a same-state Ready reason and clears it on the pinned dispatch", async () => {
    const f = fixture("connecting")
    f.reads.snapshot.mockResolvedValueOnce({
      ...snapshot,
      placement: { singleMachine: true },
      taskWaitReasons: {},
      tasks: { "build/task": 1 },
    })
    f.emit({ status: "live" })
    await ready(f.store)
    f.emitEvents({ ...change(1), state: 1, waitingReason: "WaitingForMachine" })
    expect(f.store.getSnapshot().run?.taskWaitReasons).toEqual({
      "build/task": "WaitingForMachine",
    })
    const agentId = "019ed132-2f44-7000-8000-000000000003"
    f.emitEvents({ ...change(2), state: 2, agentId })
    expect(f.store.getSnapshot().run?.taskWaitReasons).toEqual({})
    expect(f.store.getSnapshot().run?.placementAgentId).toBe(agentId)
    f.stop()
  })

  it("preserves immutable parent and reused-source identities through live deltas and reconnect tails", async () => {
    const parent = "019ed132-2f44-7000-8000-000000000003"
    const f = fixture("connecting")
    const reusedNodes = { cached: parent }
    f.reads.snapshot.mockResolvedValueOnce({
      ...snapshot,
      parentRunId: parent,
      reusedNodes,
      nodes: { ...snapshot.nodes, cached: 11 },
      tasks: { ...snapshot.tasks, "cached/task": 11 },
    })
    f.emit({ status: "live" })
    await ready(f.store)
    f.emitEvents(change(1))
    f.emit({ status: "connecting" })
    f.reads.events.mockResolvedValueOnce(page(1, 3))
    f.emit({ status: "live", resyncVersion: 1 })
    await ready(f.store)
    expect(f.store.getSnapshot().run).toMatchObject({
      lastSeq: 3,
      parentRunId: parent,
      reusedNodes,
      nodes: { cached: 11 },
      tasks: { "cached/task": 11 },
    })
    expect(f.store.getSnapshot().run?.reusedNodes).toBe(reusedNodes)
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    f.stop()
  })
  it("does not refetch run state or fabricate lifecycle changes for report notifications", async () => {
    const f = fixture()
    await ready(f.store)
    const before = f.store.getSnapshot().run
    f.emit({ reportVersion: 1 })
    expect(f.store.getSnapshot().run).toBe(before)
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    expect(f.reads.events).not.toHaveBeenCalled()
    f.stop()
  })
  it("subscribes before reading the initial snapshot and waits for authorization acknowledgement", async () => {
    const f = fixture("connecting")
    expect(f.reads.snapshot).not.toHaveBeenCalled()
    f.emit({ status: "live" })
    await ready(f.store)
    expect(f.manager.observe).toHaveBeenCalledOnce()
    expect(f.reads.snapshot).toHaveBeenCalledWith(
      runId,
      expect.any(AbortSignal)
    )
    expect(f.store.getSnapshot().run).toEqual(snapshot)
    f.stop()
  })

  it("buffers racing initial deltas without repeatedly restarting snapshot reads", async () => {
    const f = fixture("connecting")
    const pending = deferred<RunSnapshot>()
    f.reads.snapshot.mockReturnValueOnce(pending.promise)
    f.emit({ status: "live" })
    f.emitEvents(change(1))
    f.emitEvents(change(2))
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    pending.resolve(snapshot)
    await ready(f.store)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(2)
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    expect(f.reads.events).not.toHaveBeenCalled()
    f.stop()
  })

  it("applies complete live frame batches immutably with one notification and no REST polling", async () => {
    const f = fixture()
    await ready(f.store)
    const original = f.store.getSnapshot()
    f.listener.mockClear()
    f.emitEvents(...page(0, 512).events)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(512)
    expect(f.store.getSnapshot().run?.tasks["build/task"]).toBe(3)
    expect(original.run?.tasks["build/task"]).toBe(0)
    expect(f.listener).toHaveBeenCalledOnce()
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    expect(f.reads.events).not.toHaveBeenCalled()
    f.stop()
  })

  it("fills an ordered gap from the exact applied sequence and deduplicates overlap", async () => {
    const f = fixture()
    await ready(f.store)
    f.emitEvents(change(1))
    f.reads.events.mockResolvedValueOnce(page(1, 4))
    f.emitEvents(change(4))
    await ready(f.store)
    expect(f.reads.events).toHaveBeenCalledWith(
      runId,
      1,
      expect.any(AbortSignal)
    )
    expect(f.store.getSnapshot().run?.lastSeq).toBe(4)
    f.emitEvents(change(2), change(3), change(4))
    expect(f.store.getSnapshot().run?.lastSeq).toBe(4)
    expect(f.reads.events).toHaveBeenCalledOnce()
    f.stop()
  })

  it("retains live changes received while a REST gap fill is in flight", async () => {
    const f = fixture()
    await ready(f.store)
    const pending = deferred<RunEvents>()
    f.reads.events.mockReturnValueOnce(pending.promise)
    f.emitEvents(change(3))
    f.emitEvents(change(5))
    pending.resolve(page(0, 4))
    await ready(f.store)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(5)
    expect(f.reads.events).toHaveBeenCalledOnce()
    f.stop()
  })

  it("reloads a new authoritative snapshot on 409 and then applies the buffered next event", async () => {
    const f = fixture()
    await ready(f.store)
    f.reads.events.mockRejectedValueOnce(
      new ApiError(new Response(null, { status: 409 }), {})
    )
    f.reads.snapshot.mockResolvedValueOnce({
      ...snapshot,
      lastSeq: 6000,
      status: 1,
    })
    f.emitEvents(change(6001))
    await ready(f.store)
    expect(f.reads.snapshot).toHaveBeenCalledTimes(2)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(6001)
    expect(f.store.getSnapshot().run?.status).toBe(1)
    f.stop()
  })

  it("resumes after reconnect even when no live event exposes the missing tail", async () => {
    const f = fixture()
    await ready(f.store)
    f.emitEvents(change(1))
    f.emit({ status: "reconnecting" })
    expect(f.store.getSnapshot().status).toBe("reconnecting")
    expect(f.store.getSnapshot().run?.lastSeq).toBe(1)
    f.reads.events.mockResolvedValueOnce(page(1, 3))
    f.emit({ status: "live", resyncVersion: 1 })
    await ready(f.store)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(3)
    f.emit({ status: "live", resyncVersion: 1 })
    expect(f.reads.events).toHaveBeenCalledOnce()
    f.stop()
  })

  it("replaces an overflowed pending buffer with a fresh snapshot, never silently dropping it", async () => {
    const f = fixture("connecting")
    const pending = deferred<RunSnapshot>()
    f.reads.snapshot
      .mockReturnValueOnce(pending.promise)
      .mockResolvedValueOnce({ ...snapshot, lastSeq: 6000 })
    f.emit({ status: "live" })
    f.emitEvents(...page(0, 3000).events)
    f.emitEvents(...page(3000, 6000).events)
    f.emitEvents(change(6001))
    pending.resolve(snapshot)
    await ready(f.store)
    expect(f.reads.snapshot).toHaveBeenCalledTimes(2)
    expect(f.store.getSnapshot().run?.lastSeq).toBe(6001)
    f.stop()
  })

  it("fails visibly instead of looping on a nonadvancing server tail", async () => {
    const f = fixture()
    await ready(f.store)
    f.reads.events.mockResolvedValueOnce(page(0, 0))
    f.emitEvents(change(3))
    await waitFor(() => expect(f.store.getSnapshot().status).toBe("error"))
    expect(f.store.getSnapshot().error?.message).toContain("pending committed")
    expect(f.store.getSnapshot().run).toBeNull()
    expect(f.reads.events).toHaveBeenCalledOnce()
    f.stop()
  })

  it("clears stale graphs on denied resync, and retry waits for fresh subscription authorization", async () => {
    const f = fixture()
    await ready(f.store)
    f.reads.events.mockRejectedValueOnce(
      new ApiError(new Response(null, { status: 403 }), { title: "Revoked" })
    )
    f.emit({ resyncVersion: 1 })
    await waitFor(() => expect(f.store.getSnapshot().status).toBe("error"))
    expect(f.store.getSnapshot().run).toBeNull()
    f.store.retry()
    expect(f.retry).toHaveBeenCalledOnce()
    expect(f.reads.snapshot).toHaveBeenCalledOnce()
    f.emit({ status: "connecting" })
    f.emit({ status: "live" })
    await ready(f.store)
    expect(f.reads.snapshot).toHaveBeenCalledTimes(2)
    f.stop()
  })

  it("discards an old in-flight response when authentication resets the live store", async () => {
    const f = fixture("connecting")
    const pending = deferred<RunSnapshot>()
    f.reads.snapshot.mockReturnValueOnce(pending.promise)
    f.emit({ status: "live" })
    const signal = f.reads.snapshot.mock.calls[0]?.[1]
    f.emit({ status: "disconnected", delta: null })
    expect(signal?.aborted).toBe(true)
    pending.resolve(snapshot)
    await Promise.resolve()
    expect(f.store.getSnapshot().run).toBeNull()
    expect(f.store.getSnapshot().status).toBe("reconnecting")
    f.stop()
  })

  it("releases only on the last subscriber and aborts its outstanding read", async () => {
    const f = fixture("connecting")
    const second = f.store.subscribe(vi.fn())
    const pending = deferred<RunSnapshot>()
    f.reads.snapshot.mockReturnValueOnce(pending.promise)
    f.emit({ status: "live" })
    f.stop()
    expect(f.release).not.toHaveBeenCalled()
    expect(f.reads.snapshot.mock.calls[0]?.[1].aborted).toBe(false)
    second()
    expect(f.release).toHaveBeenCalledOnce()
    expect(f.reads.snapshot.mock.calls[0]?.[1].aborted).toBe(true)
    pending.resolve(snapshot)
    await Promise.resolve()
    expect(f.store.getSnapshot().run).toBeNull()
  })

  it("retains the exact terminal abort identity and server timestamp", async () => {
    const f = fixture()
    await ready(f.store)
    const abort = {
      by: "operator",
      byName: "Release operator",
      at: "2026-10-16T02:00:00Z",
      reason: "Stop deployment",
      source: "UI",
    } as const
    f.emitEvents({ ...change(1), entity: 0, key: null, state: 5, abort })
    expect(f.store.getSnapshot().run).toMatchObject({
      lastSeq: 1,
      status: 5,
      endedAt: "2026-10-16T02:00:00Z",
      abort,
    })
    f.stop()
  })

  it("rejects unknown graph keys instead of inventing entities", async () => {
    const f = fixture()
    await ready(f.store)
    f.emitEvents({ ...change(1), key: "foreign/task" })
    expect(f.store.getSnapshot().status).toBe("error")
    expect(f.store.getSnapshot().error?.message).toContain(
      "unknown graph entity"
    )
    expect(f.store.getSnapshot().run).toBeNull()
    f.stop()
  })

  it("rejects a delta for another run", async () => {
    const f = fixture()
    await ready(f.store)
    f.emit({ delta: { runId: snapshot.versionId, events: [change(1)] } })
    expect(f.store.getSnapshot().status).toBe("error")
    expect(f.store.getSnapshot().run).toBeNull()
    f.stop()
  })
})
