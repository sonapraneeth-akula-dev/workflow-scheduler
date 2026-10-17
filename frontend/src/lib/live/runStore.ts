import { ApiError } from "@/lib/api/client"
import { resourceSchema, type RunDelta } from "./messages"
import { readRunEvents, readRunSnapshot, type RunSnapshot } from "./runApi"
import {
  liveSubscriptions,
  type LiveSnapshot,
  type LiveSubscriptions,
} from "./subscriptions"

export interface RunStoreSnapshot {
  readonly status: "loading" | "live" | "reconnecting" | "error"
  readonly run: RunSnapshot | null
  readonly error: Error | null
}

const api = { snapshot: readRunSnapshot, events: readRunEvents }
type Change = RunDelta["events"][number]

export class RunStore {
  private readonly runId: string
  private readonly manager: Pick<LiveSubscriptions, "observe">
  private readonly reads: typeof api
  private readonly listeners = new Set<() => void>()
  private state: RunStoreSnapshot = {
    status: "loading",
    run: null,
    error: null,
  }
  private stop: (() => void) | undefined
  private retryLive: (() => void) | undefined
  private live: LiveSnapshot | null = null
  private delta: LiveSnapshot["delta"] = null
  private resyncVersion = 0
  private pending = new Map<number, Change>()
  private requested: "snapshot" | "tail" | undefined
  private controller: AbortController | undefined
  private generation = 0

  constructor(
    runId: string,
    manager: Pick<LiveSubscriptions, "observe"> = liveSubscriptions,
    reads: typeof api = api
  ) {
    this.runId = resourceSchema.parse({ scope: "run", id: runId }).id
    this.manager = manager
    this.reads = reads
  }

  getSnapshot = (): RunStoreSnapshot => this.state

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    if (this.listeners.size === 1) {
      const scope = this.manager.observe({ scope: "run", id: this.runId })
      this.retryLive = scope.retry
      this.stop = scope.subscribe(() => this.receive(scope.getSnapshot()))
      this.receive(scope.getSnapshot())
    }
    return () => {
      this.listeners.delete(listener)
      if (this.listeners.size === 0) {
        this.cancel()
        this.stop?.()
        this.stop = undefined
        this.retryLive = undefined
        this.live = null
        this.delta = null
        this.resyncVersion = 0
        this.state = { status: "loading", run: null, error: null }
      }
    }
  }

  retry = (): void => {
    if (!this.listeners.size) return
    this.cancel()
    this.live = null
    this.set({ status: "loading", run: null, error: null })
    this.requested = "snapshot"
    this.retryLive?.()
  }

  private receive(snapshot: LiveSnapshot | null): void {
    this.live = snapshot
    if (!snapshot || snapshot.status === "disconnected") {
      this.cancel()
      this.set({ status: "reconnecting", run: null, error: null })
      return
    }
    if (snapshot.error) {
      this.fail(snapshot.error)
      return
    }
    if (snapshot.status !== "live") {
      this.set({ ...this.state, status: "reconnecting" })
      return
    }
    if (this.state.status === "error") return
    if (snapshot.delta !== this.delta) {
      this.delta = snapshot.delta
      if (snapshot.delta && "events" in snapshot.delta) {
        if (snapshot.delta.runId !== this.runId) {
          this.fail(new Error("Live updates returned a different run."))
          return
        }
        for (const event of snapshot.delta.events) {
          if (event.seq > (this.state.run?.lastSeq ?? 0))
            this.pending.set(event.seq, event)
        }
        if (this.pending.size > 5000) {
          this.pending.clear()
          this.requested = "snapshot"
        }
      }
    }
    if (snapshot.resyncVersion !== this.resyncVersion) {
      this.resyncVersion = snapshot.resyncVersion
      this.requested ??= this.state.run ? "tail" : "snapshot"
    }
    if (!this.state.run && !this.controller) this.requested = "snapshot"
    if (!this.controller && !this.requested) {
      const previous = this.state.run
      try {
        this.drain()
      } catch (error) {
        this.fail(error)
        return
      }
      if (this.state.run !== previous) this.set(this.state)
      if (this.pending.size) this.requested = "tail"
    }
    this.recover()
  }

  private recover(): void {
    if (
      this.controller ||
      !this.requested ||
      this.live?.status !== "live" ||
      !this.listeners.size
    )
      return
    const generation = this.generation
    const controller = new AbortController()
    this.controller = controller
    this.set({
      ...this.state,
      status: this.state.run ? "reconnecting" : "loading",
      error: null,
    })
    void this.restore(controller.signal)
      .then(() => {
        if (generation !== this.generation) return
        this.controller = undefined
        this.set({
          ...this.state,
          status: this.live?.status === "live" ? "live" : "reconnecting",
        })
        this.recover()
      })
      .catch((error: unknown) => {
        if (generation !== this.generation) return
        this.controller = undefined
        this.fail(error)
      })
  }

  private async restore(signal: AbortSignal): Promise<void> {
    while (this.requested) {
      const request = this.requested
      this.requested = undefined
      const before = this.state.run?.lastSeq ?? -1
      if (request === "snapshot" || !this.state.run) {
        const run = await this.reads.snapshot(this.runId, signal)
        if (signal.aborted) return
        this.set({ ...this.state, run })
      } else {
        try {
          const page = await this.reads.events(
            this.runId,
            this.state.run.lastSeq,
            signal
          )
          if (signal.aborted) return
          for (const event of page.events) this.apply(event)
        } catch (error) {
          if (signal.aborted) return
          if (!(error instanceof ApiError && error.status === 409)) throw error
          this.requested = "snapshot"
          continue
        }
      }
      this.drain()
      if (this.pending.size && !this.requested) {
        const sequence = this.state.run?.lastSeq
        if (sequence === undefined || sequence <= before)
          throw new Error(
            "Run resync did not include the pending committed changes."
          )
        this.requested = "tail"
      }
    }
  }

  private drain(): void {
    if (!this.state.run) return
    for (const seq of this.pending.keys())
      if (seq <= this.state.run.lastSeq) this.pending.delete(seq)
    let next: Change | undefined
    while ((next = this.pending.get(this.state.run.lastSeq + 1))) {
      this.pending.delete(next.seq)
      this.apply(next)
    }
  }

  private apply(event: Change): void {
    const run = this.state.run
    if (!run || event.seq !== run.lastSeq + 1)
      throw new Error("Run events are not contiguous.")
    if (event.entity === 0) {
      this.state = {
        ...this.state,
        run: {
          ...run,
          lastSeq: event.seq,
          status: event.state,
          abort: event.abort ?? run.abort,
          endedAt: event.state >= 3 ? event.at : null,
        },
      }
    } else {
      const field = event.entity === 1 ? "nodes" : "tasks"
      if (event.key === null || !Object.hasOwn(run[field], event.key))
        throw new Error("A run event names an unknown graph entity.")
      const taskWaitReasons = { ...run.taskWaitReasons }
      if (event.entity === 2) {
        if (event.waitingReason)
          taskWaitReasons[event.key] = event.waitingReason
        else delete taskWaitReasons[event.key]
      }
      this.state = {
        ...this.state,
        run: {
          ...run,
          lastSeq: event.seq,
          [field]: { ...run[field], [event.key]: event.state },
          taskWaitReasons,
          placementAgentId:
            run.placement?.singleMachine && event.agentId
              ? event.agentId
              : run.placementAgentId,
        },
      }
    }
  }

  private cancel(): void {
    this.generation++
    this.controller?.abort()
    this.controller = undefined
    this.pending.clear()
    this.requested = undefined
  }

  private fail(cause: unknown): void {
    this.cancel()
    this.set({
      status: "error",
      run: null,
      error:
        cause instanceof Error
          ? cause
          : new Error("Run resync failed.", { cause }),
    })
  }

  private set(state: RunStoreSnapshot): void {
    this.state = state
    for (const listener of this.listeners) listener()
  }
}
