import { createElement, StrictMode, type ReactNode } from "react"
import { act, renderHook, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { LiveTransport } from "./connection"
import {
  parseListDelta,
  parseReportAdded,
  parseRunDelta,
  resourceSchema,
} from "./messages"
import { LiveSubscriptions } from "./subscriptions"
import { useLive } from "./useLive"

const run = "0199e706-5bd0-7000-8000-000000000001"
const other = "0199e706-5bd0-7000-8000-000000000002"
const target = { scope: "run" as const, id: run }
const at = "2026-10-15T18:00:00.0000000+00:00"
const frames = new Map<number, FrameRequestCallback>()
let nextFrame = 0

class TestTransport implements LiveTransport {
  readonly handlers = new Map<string, (message: unknown) => unknown>()
  reconnecting: (() => void) | undefined
  reconnected: (() => void) | undefined
  closed: ((error?: Error) => void) | undefined
  start = vi.fn(async () => {})
  stop = vi.fn(async () => {})
  invoke = vi.fn<LiveTransport["invoke"]>(async () => null)
  on(method: string, callback: (message: unknown) => unknown) {
    this.handlers.set(method, callback)
  }
  onclose(callback: (error?: Error) => void) {
    this.closed = callback
  }
  onreconnecting(callback: () => void) {
    this.reconnecting = callback
  }
  onreconnected(callback: () => void) {
    this.reconnected = callback
  }
  emit(method: string, payload: unknown) {
    const handler = this.handlers.get(method)
    if (!handler) throw new Error("No handler for " + method)
    return handler(payload)
  }
}

function frame() {
  const pending = [...frames.values()]
  frames.clear()
  for (const callback of pending) callback(performance.now())
}

function deferred<T>() {
  let resolve: (value: T) => void = () => {
    throw new Error("Promise not initialized.")
  }
  let reject: (error: Error) => void = () => {
    throw new Error("Promise not initialized.")
  }
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept
    reject = fail
  })
  return { promise, resolve, reject }
}

function delta(seq = 1) {
  return {
    runId: run,
    events: [{ seq, at, entity: 0, key: null, state: 1, abort: null }],
  }
}

function summary(lastSeq = 1) {
  return {
    runId: run,
    workflowId: "team/type/workflow",
    versionId: other,
    seriesId: null,
    status: 1,
    lastSeq,
    createdAt: at,
    endedAt: null,
  }
}

function setup() {
  const transport = new TestTransport()
  const factory = vi.fn(async () => transport)
  return { transport, factory, manager: new LiveSubscriptions(factory) }
}

beforeEach(() => {
  frames.clear()
  nextFrame = 0
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    const id = ++nextFrame
    frames.set(id, callback)
    return id
  })
})

afterEach(() => vi.unstubAllGlobals())

describe("shared reference-counted live scopes", () => {
  it("batches report notifications separately without fabricating lifecycle sequence or resync", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    frame()
    const before = scope.getSnapshot()
    const listener = vi.fn()
    scope.subscribe(listener)
    for (let index = 0; index < 25; index++)
      transport.emit("ReportAdded", { runId: run, reportId: other })
    expect(before.reportVersion).toBe(0)
    expect(listener).not.toHaveBeenCalled()
    frame()
    expect(listener).toHaveBeenCalledOnce()
    expect(scope.getSnapshot().reportVersion).toBe(25)
    expect(scope.getSnapshot().resyncVersion).toBe(0)
    expect(scope.getSnapshot().delta).toBeNull()
    await manager.reset()
  })
  it("ignores foreign-run report notifications and retains report refresh across reconnect", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.emit("ReportAdded", { runId: other, reportId: run })
    expect(scope.getSnapshot().reportVersion).toBe(0)
    transport.emit("ReportAdded", { runId: run, reportId: other })
    transport.reconnecting?.()
    transport.reconnected?.()
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(scope.getSnapshot().reportVersion).toBe(1)
    expect(scope.getSnapshot().resyncVersion).toBe(1)
    await manager.reset()
  })
  it("surfaces malformed report messages instead of hiding protocol errors", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.emit("ReportAdded", { runId: run, reportId: "invalid" })
    expect(scope.getSnapshot().status).toBe("error")
    expect(scope.getSnapshot().error?.message).toContain("invalid live message")
    expect(scope.getSnapshot().reportVersion).toBe(0)
    await manager.reset()
  })
  it("starts lazily and shares one canonical subscription for two views", async () => {
    const { manager, transport, factory } = setup()
    expect(factory).not.toHaveBeenCalled()
    const first = manager.retain({ ...target, id: run.toUpperCase() })
    const second = manager.retain(target)
    await vi.waitFor(() => expect(second.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(factory).toHaveBeenCalledOnce()
    expect(transport.start).toHaveBeenCalledOnce()
    expect(transport.invoke).toHaveBeenCalledExactlyOnceWith(
      "Subscribe",
      "run",
      run
    )
    first.release()
    await Promise.resolve()
    expect(transport.stop).not.toHaveBeenCalled()
    second.release()
    second.release()
    await vi.waitFor(() => expect(transport.stop).toHaveBeenCalledOnce(), {
      interval: 1,
    })
  })

  it("unsubscribes the final view of one scope without interrupting other scopes", async () => {
    const { manager, transport } = setup()
    const first = manager.retain(target)
    const second = manager.retain({ scope: "type", id: "team/type" })
    await vi.waitFor(() => expect(second.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    first.release()
    await vi.waitFor(
      () =>
        expect(transport.invoke).toHaveBeenCalledWith(
          "Unsubscribe",
          "run",
          run
        ),
      { interval: 1 }
    )
    expect(second.getSnapshot().status).toBe("live")
    expect(transport.stop).not.toHaveBeenCalled()
    await manager.reset()
  })

  it("does not start a connection for a scope released before the queued start", async () => {
    const { manager, factory } = setup()
    manager.retain(target).release()
    await manager.reset()
    expect(factory).not.toHaveBeenCalled()
  })

  it("stops a factory-created transport if all references disappear during lazy loading", async () => {
    const pending = deferred<LiveTransport>()
    const transport = new TestTransport()
    const factory = vi.fn(() => pending.promise)
    const manager = new LiveSubscriptions(factory)
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(factory).toHaveBeenCalledOnce(), {
      interval: 1,
    })
    scope.release()
    pending.resolve(transport)
    await vi.waitFor(() => expect(transport.stop).toHaveBeenCalledOnce(), {
      interval: 1,
    })
    expect(transport.start).not.toHaveBeenCalled()
  })

  it("remembers and removes a successful Subscribe released while its acknowledgement was pending", async () => {
    const { manager, transport } = setup()
    const pending = deferred<unknown>()
    transport.invoke.mockImplementationOnce(() => pending.promise)
    const runScope = manager.retain(target)
    const typeScope = manager.retain({ scope: "type", id: "team/type" })
    await vi.waitFor(
      () =>
        expect(transport.invoke).toHaveBeenCalledWith("Subscribe", "run", run),
      { interval: 1 }
    )
    runScope.release()
    pending.resolve(null)
    await vi.waitFor(
      () =>
        expect(transport.invoke).toHaveBeenCalledWith(
          "Unsubscribe",
          "run",
          run
        ),
      { interval: 1 }
    )
    expect(typeScope.getSnapshot().status).toBe("live")
    await manager.reset()
  })

  it("batches 1000 run events into one immutable frame snapshot without losing sequence", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    frame()
    const listener = vi.fn()
    scope.subscribe(listener)
    const before = scope.getSnapshot()
    for (let seq = 1; seq <= 1000; seq++) transport.emit("RunDelta", delta(seq))
    expect(listener).not.toHaveBeenCalled()
    expect(scope.getSnapshot()).toBe(before)
    expect(frames.size).toBe(1)
    frame()
    expect(listener).toHaveBeenCalledOnce()
    const first = scope.getSnapshot()
    if (!first.delta || !("events" in first.delta))
      throw new Error("Missing run delta.")
    expect(first.delta.events.map((change) => change.seq)).toEqual(
      Array.from({ length: 1000 }, (_, index) => index + 1)
    )
    transport.emit("RunDelta", delta(1001))
    frame()
    expect(first.delta.events).toHaveLength(1000)
    const next = scope.getSnapshot().delta
    if (!next || !("events" in next)) throw new Error("Missing next run delta.")
    expect(next.events.map((change) => change.seq)).toEqual([1001])
    await manager.reset()
  })

  it("keeps scoped stores separate and coalesces list items by run without carrying previous frames forward", async () => {
    const { manager, transport } = setup()
    const runScope = manager.retain(target)
    const typeScope = manager.retain({ scope: "type", id: "team/type" })
    await vi.waitFor(
      () => expect(typeScope.getSnapshot().status).toBe("live"),
      { interval: 1 }
    )
    frame()
    const list = (lastSeq: number) => ({
      scope: "type",
      id: "team/type",
      items: [summary(lastSeq)],
    })
    transport.emit("ListDelta", list(1))
    transport.emit("ListDelta", list(2))
    transport.emit("RunDelta", { ...delta(), runId: other })
    frame()
    expect(runScope.getSnapshot().delta).toBeNull()
    expect(typeScope.getSnapshot().delta).toEqual(list(2))
    transport.emit("RunDelta", delta())
    frame()
    expect(runScope.getSnapshot().delta).toEqual(delta())
    expect(typeScope.getSnapshot().delta).toEqual(list(2))
    await manager.reset()
  })

  it("reauthorizes all retained scopes after reconnect and marks snapshot resynchronization explicitly", async () => {
    const { manager, transport, factory } = setup()
    const first = manager.retain(target)
    const second = manager.retain({ scope: "type", id: "team/type" })
    await vi.waitFor(() => expect(second.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.reconnecting?.()
    expect(first.getSnapshot().status).toBe("reconnecting")
    transport.reconnected?.()
    await vi.waitFor(() => expect(second.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(
      transport.invoke.mock.calls.filter((call) => call[0] === "Subscribe")
    ).toHaveLength(4)
    expect(first.getSnapshot().resyncVersion).toBe(1)
    expect(factory).toHaveBeenCalledOnce()
    await manager.reset()
  })

  it("drain hint preserves retained scopes but discards pending deltas until reconnect resynchronizes", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.emit("RunDelta", delta())
    expect(transport.emit("Reconnect", { reason: "server_draining" })).toBe(
      true
    )
    frame()
    expect(scope.getSnapshot().status).toBe("reconnecting")
    expect(scope.getSnapshot().delta).toBeNull()
    expect(scope.getSnapshot().resyncVersion).toBe(1)
    transport.reconnecting?.()
    transport.reconnected?.()
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(scope.getSnapshot().resyncVersion).toBe(2)
    expect(transport.invoke.mock.calls).toEqual([
      ["Subscribe", "run", run],
      ["Subscribe", "run", run],
    ])
    await manager.reset()
  })

  it("recreates a gracefully closed draining connection and ignores late callbacks from its old generation", async () => {
    const first = new TestTransport()
    const second = new TestTransport()
    const factory = vi
      .fn()
      .mockResolvedValueOnce(first)
      .mockResolvedValueOnce(second)
    const manager = new LiveSubscriptions(factory)
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    first.emit("Reconnect", { reason: "server_draining" })
    first.closed?.()
    await vi.waitFor(
      () => expect(second.invoke).toHaveBeenCalledWith("Subscribe", "run", run),
      { interval: 1 }
    )
    expect(scope.getSnapshot().status).toBe("live")
    expect(scope.getSnapshot().error).toBeNull()
    first.reconnecting?.()
    first.closed?.(new Error("stale"))
    expect(scope.getSnapshot().status).toBe("live")
    await manager.reset()
  })

  it.each([null, {}, { reason: "unexpected" }, { reason: 3 }])(
    "rejects a malformed drain hint rather than hiding a transport failure: %j",
    async (hint) => {
      const { manager, transport } = setup()
      const scope = manager.retain(target)
      await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
        interval: 1,
      })
      expect(transport.emit("Reconnect", hint)).toBe(false)
      expect(scope.getSnapshot().status).toBe("error")
      expect(scope.getSnapshot().error?.message).toContain(
        "invalid live message"
      )
      await manager.reset()
    }
  )

  it("clears previously received data on same-token revocation without reconnecting or resubscribing manually", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.emit("RunDelta", delta())
    frame()
    expect(scope.getSnapshot().delta).not.toBeNull()
    transport.invoke.mockResolvedValueOnce({
      status: 403,
      title: "Forbidden",
      detail: "Role revoked",
      reason: "insufficient_scope_role",
    })
    transport.emit("Resync", target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("error"), {
      interval: 1,
    })
    expect(scope.getSnapshot().error?.message).toContain("HTTP 403")
    expect(scope.getSnapshot().delta).toBeNull()
    transport.emit("RunDelta", delta(2))
    frame()
    expect(scope.getSnapshot().delta).toBeNull()
    transport.reconnecting?.()
    transport.reconnected?.()
    expect(scope.getSnapshot().status).toBe("error")
    await manager.reset()
  })

  it("requires explicit retry after a denied Subscribe and then rechecks permission", async () => {
    const { manager, transport } = setup()
    transport.invoke.mockResolvedValueOnce({
      status: 403,
      title: "Forbidden",
      detail: "No grant",
    })
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("error"), {
      interval: 1,
    })
    scope.retry()
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(transport.invoke).toHaveBeenCalledTimes(2)
    await manager.reset()
  })

  it("reports start failure without URL/token leakage, and retry creates a fresh transport", async () => {
    const first = new TestTransport()
    const next = new TestTransport()
    first.start.mockRejectedValueOnce(
      new Error("wss://host/hubs?access_token=secret")
    )
    const factory = vi
      .fn<() => Promise<LiveTransport>>()
      .mockResolvedValueOnce(first)
      .mockResolvedValueOnce(next)
    const manager = new LiveSubscriptions(factory)
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("error"), {
      interval: 1,
    })
    expect(scope.getSnapshot().error?.message).not.toContain("secret")
    scope.retry()
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(first.stop).toHaveBeenCalledOnce()
    expect(next.start).toHaveBeenCalledOnce()
    await manager.reset()
  })

  it("aborts a pending start on identity reset and never restores old scopes from late callbacks", async () => {
    const { manager, transport } = setup()
    const pending = deferred<void>()
    transport.start.mockImplementationOnce(() => pending.promise)
    transport.stop.mockImplementationOnce(async () =>
      pending.reject(new Error("Stopped"))
    )
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(transport.start).toHaveBeenCalledOnce(), {
      interval: 1,
    })
    await manager.reset()
    expect(scope.getSnapshot().status).toBe("disconnected")
    expect(scope.getSnapshot().delta).toBeNull()
    transport.reconnected?.()
    transport.emit("RunDelta", delta())
    frame()
    expect(scope.getSnapshot().delta).toBeNull()
    expect(transport.invoke).not.toHaveBeenCalled()
    scope.release()
  })

  it("signals resync instead of silently growing an unbounded hidden-tab event queue", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    frame()
    for (let seq = 1; seq <= 5001; seq++) transport.emit("RunDelta", delta(seq))
    frame()
    expect(scope.getSnapshot().resyncVersion).toBe(1)
    expect(scope.getSnapshot().delta).toBeNull()
    await manager.reset()
  })

  it("fails visibly on malformed wire data and does not apply subsequent messages until retried", async () => {
    const { manager, transport } = setup()
    const scope = manager.retain(target)
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    transport.emit("RunDelta", {
      ...delta(),
      events: [{ ...delta().events[0], seq: Number.MAX_SAFE_INTEGER + 1 }],
    })
    expect(scope.getSnapshot().status).toBe("error")
    expect(scope.getSnapshot().error?.message).toContain("invalid live message")
    transport.emit("RunDelta", delta())
    frame()
    expect(scope.getSnapshot().delta).toBeNull()
    scope.retry()
    await vi.waitFor(() => expect(scope.getSnapshot().status).toBe("live"), {
      interval: 1,
    })
    expect(
      transport.invoke.mock.calls.filter((call) => call[0] === "Subscribe")
    ).toHaveLength(2)
    await manager.reset()
  })
})

describe("React external live store integration", () => {
  it("mounts multiple StrictMode views on one connection and releases all references on unmount", async () => {
    const { manager, transport, factory } = setup()
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(StrictMode, null, children)
    const first = renderHook(() => useLive(target, manager), { wrapper })
    const second = renderHook(() => useLive(target, manager), { wrapper })
    await act(async () => {
      await vi.waitFor(
        () =>
          expect(transport.invoke).toHaveBeenCalledWith(
            "Subscribe",
            "run",
            run
          ),
        { interval: 1 }
      )
      frame()
    })
    expect(first.result.current.snapshot?.status).toBe("live")
    expect(second.result.current.snapshot).toBe(first.result.current.snapshot)
    expect(factory).toHaveBeenCalledOnce()
    first.unmount()
    expect(transport.stop).not.toHaveBeenCalled()
    second.unmount()
    await vi.waitFor(() => expect(transport.stop).toHaveBeenCalledOnce(), {
      interval: 1,
    })
  })

  it("does not render a prior scope's data when route identity changes", async () => {
    const { manager, transport } = setup()
    const hook = renderHook(
      ({ id }) => useLive({ scope: "run", id }, manager),
      { initialProps: { id: run } }
    )
    await act(async () => {
      await vi.waitFor(
        () =>
          expect(transport.invoke).toHaveBeenCalledWith(
            "Subscribe",
            "run",
            run
          ),
        { interval: 1 }
      )
      transport.emit("RunDelta", delta())
      frame()
    })
    expect(hook.result.current.snapshot?.delta).not.toBeNull()
    hook.rerender({ id: other })
    expect(hook.result.current.snapshot?.resource.id).toBe(other)
    expect(hook.result.current.snapshot?.delta).toBeNull()
    await act(async () => {
      await vi.waitFor(
        () =>
          expect(transport.invoke).toHaveBeenCalledWith(
            "Subscribe",
            "run",
            other
          ),
        { interval: 1 }
      )
      frame()
    })
    expect(hook.result.current.snapshot?.status).toBe("live")
    hook.unmount()
    await manager.reset()
  })

  it("never opens a transport without a resource and clears mounted data on identity reset", async () => {
    const { manager, transport, factory } = setup()
    const absent = renderHook(() => useLive(undefined, manager))
    expect(absent.result.current.snapshot).toBeNull()
    expect(factory).not.toHaveBeenCalled()
    const active = renderHook(() => useLive(target, manager))
    await act(async () => {
      await vi.waitFor(() => expect(transport.invoke).toHaveBeenCalled(), {
        interval: 1,
      })
      transport.emit("RunDelta", delta())
      frame()
    })
    await act(async () => {
      await manager.reset()
      frame()
    })
    await waitFor(() =>
      expect(active.result.current.snapshot?.status).toBe("disconnected")
    )
    expect(active.result.current.snapshot?.delta).toBeNull()
    active.unmount()
    absent.unmount()
  })
})

it("validates IDs, noncontiguous event batches, nullable abort records and summaries against generated contracts", () => {
  expect(parseReportAdded({ runId: run, reportId: other })).toEqual({
    runId: run,
    reportId: other,
  })
  expect(() => parseReportAdded({ runId: run })).toThrow()
  expect(() =>
    parseReportAdded({ runId: "invalid", reportId: other })
  ).toThrow()
  expect(() => resourceSchema.parse({ scope: "run", id: "invalid" })).toThrow()
  expect(() => resourceSchema.parse({ scope: "team", id: "x\n" })).toThrow()
  expect(() =>
    parseRunDelta({
      runId: run,
      events: [...delta(1).events, ...delta(3).events],
    })
  ).toThrow()
  expect(() =>
    parseRunDelta({
      ...delta(),
      events: [{ ...delta().events[0], entity: 0, state: 12 }],
    })
  ).toThrow()
  expect(parseRunDelta(delta()).events[0]?.abort).toBeNull()
  expect(
    parseListDelta({ scope: "type", id: "team/type", items: [summary()] })
      .items[0]?.lastSeq
  ).toBe(1)
})
