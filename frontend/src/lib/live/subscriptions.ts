import { createConnection, type LiveTransport } from "./connection"
import {
  parseListDelta,
  parseReportAdded,
  parseReconnect,
  parseResync,
  parseRunDelta,
  parseSubscription,
  resourceKey,
  resourceSchema,
  type LiveDelta,
  type LiveResource,
  type ListDelta,
  type RunDelta,
} from "./messages"

export interface LiveSnapshot {
  readonly resource: LiveResource
  readonly status:
    "connecting" | "live" | "reconnecting" | "disconnected" | "error"
  readonly error: Error | null
  readonly delta: LiveDelta | null
  readonly resyncVersion: number
  readonly reportVersion: number
}

export interface LiveScope {
  getSnapshot(): LiveSnapshot
  subscribe(listener: () => void): () => void
  retry(): void
  release(): void
}

interface Entry {
  refs: number
  blocked: boolean
  snapshot: LiveSnapshot
  listeners: Set<() => void>
  runEvents: RunDelta["events"]
  items: Map<string, ListDelta["items"][number]>
  pendingDelta: LiveDelta | undefined
}

export class LiveSubscriptions {
  private readonly entries = new Map<string, Entry>()
  private readonly subscribed = new Map<string, Entry>()
  private readonly dirty = new Set<Entry>()
  private connection: LiveTransport | undefined
  private state: "disconnected" | "connecting" | "connected" | "reconnecting" =
    "disconnected"
  private queue: Promise<void> = Promise.resolve()
  private frame: number | undefined
  private generation = 0
  private readonly factory: () => Promise<LiveTransport>

  constructor(factory: () => Promise<LiveTransport> = createConnection) {
    this.factory = factory
  }

  observe(input?: LiveResource) {
    const resource = input ? resourceSchema.parse(input) : undefined
    let current: LiveScope | undefined
    const initial: LiveSnapshot | null = resource
      ? {
          resource,
          status: "connecting",
          delta: null,
          error: null,
          resyncVersion: 0,
          reportVersion: 0,
        }
      : null
    return {
      getSnapshot: () => current?.getSnapshot() ?? initial,
      subscribe: (listener: () => void) => {
        if (!resource) return () => undefined
        const retained = this.retain(resource)
        current = retained
        const unsubscribe = retained.subscribe(listener)
        listener()
        return () => {
          unsubscribe()
          retained.release()
          if (current === retained) current = undefined
        }
      },
      retry: () => current?.retry(),
    }
  }

  retain(input: LiveResource): LiveScope {
    const resource = resourceSchema.parse(input)
    const key = resourceKey(resource)
    let entry = this.entries.get(key)
    if (!entry) {
      entry = {
        refs: 0,
        blocked: false,
        snapshot: {
          resource,
          status: "connecting",
          error: null,
          delta: null,
          resyncVersion: 0,
          reportVersion: 0,
        },
        listeners: new Set(),
        runEvents: [],
        items: new Map(),
        pendingDelta: undefined,
      }
      this.entries.set(key, entry)
    }
    const current = entry
    current.refs++
    const listeners = new Set<() => void>()
    let released = false
    this.schedule()
    return {
      getSnapshot: () => current.snapshot,
      subscribe: (listener) => {
        if (released)
          throw new Error("The live scope has already been released.")
        listeners.add(listener)
        current.listeners.add(listener)
        return () => {
          listeners.delete(listener)
          current.listeners.delete(listener)
        }
      },
      retry: () => {
        if (released || this.entries.get(key) !== current) return
        current.blocked = false
        this.subscribed.delete(key)
        this.update(current, { status: "connecting", error: null })
        this.schedule()
      },
      release: () => {
        if (released) return
        released = true
        for (const listener of listeners) current.listeners.delete(listener)
        listeners.clear()
        if (--current.refs === 0 && this.entries.get(key) === current) {
          this.entries.delete(key)
          this.dirty.delete(current)
          this.schedule()
        }
      },
    }
  }

  async reset(): Promise<void> {
    this.generation++
    for (const entry of this.entries.values()) {
      entry.runEvents = []
      entry.items.clear()
      entry.pendingDelta = undefined
      this.update(entry, { status: "disconnected", error: null, delta: null })
    }
    this.entries.clear()
    const connection = this.connection
    this.connection = undefined
    this.state = "disconnected"
    this.subscribed.clear()
    try {
      await connection?.stop()
    } catch (cause) {
      throw new Error("The live connection could not be closed.", { cause })
    } finally {
      await this.queue
    }
  }

  private schedule(): void {
    const generation = this.generation
    this.queue = this.queue
      .then(async () => {
        if (generation === this.generation) await this.reconcile(generation)
      })
      .catch((cause: unknown) => {
        if (generation !== this.generation) return
        this.fail(
          new Error("The live connection failed. Retry or sign in again.", {
            cause,
          })
        )
      })
  }

  private async reconcile(generation: number): Promise<void> {
    if (this.entries.size === 0) {
      const connection = this.connection
      this.connection = undefined
      this.state = "disconnected"
      this.subscribed.clear()
      await connection?.stop()
      return
    }
    if (this.state === "reconnecting") return
    if (!this.connection) {
      const connection = await this.factory()
      if (generation !== this.generation || this.entries.size === 0) {
        await connection.stop()
        return
      }
      this.connection = connection
      this.state = "connecting"
      this.attach(connection)
      await connection.start()
      if (generation !== this.generation || this.connection !== connection)
        return
      this.state = "connected"
    }
    if (this.state !== "connected") {
      const connection = this.connection
      this.connection = undefined
      await connection.stop()
      this.schedule()
      return
    }
    const connection = this.connection
    for (const [key, previous] of this.subscribed) {
      if (this.entries.get(key) !== previous) {
        const { scope, id } = previous.snapshot.resource
        await connection.invoke("Unsubscribe", scope, id)
        this.subscribed.delete(key)
      }
    }
    for (const [key, entry] of this.entries) {
      if (entry.blocked || this.subscribed.get(key) === entry) continue
      const { scope, id } = entry.snapshot.resource
      const error = parseSubscription(
        await connection.invoke("Subscribe", scope, id)
      )
      if (generation !== this.generation) return
      if (!error) this.subscribed.set(key, entry)
      if (this.entries.get(key) !== entry) continue
      if (error) {
        entry.blocked = true
        entry.runEvents = []
        entry.items.clear()
        entry.pendingDelta = undefined
        this.update(entry, { status: "error", error, delta: null })
      } else {
        this.update(entry, { status: "live", error: null })
      }
    }
  }

  private attach(connection: LiveTransport): void {
    let draining = false
    connection.on("Reconnect", (value) => {
      let acknowledged = false
      this.receive(connection, () => {
        parseReconnect(value)
        draining = true
        this.state = "reconnecting"
        this.subscribed.clear()
        for (const entry of this.entries.values()) {
          if (entry.blocked) continue
          entry.runEvents = []
          entry.items.clear()
          entry.pendingDelta = undefined
          this.update(entry, {
            status: "reconnecting",
            delta: null,
            resyncVersion: entry.snapshot.resyncVersion + 1,
          })
        }
        acknowledged = true
      })
      return acknowledged
    })
    connection.on("ReportAdded", (value) =>
      this.receive(connection, () => {
        const added = parseReportAdded(value)
        const entry = this.entries.get(`run:${added.runId.toLowerCase()}`)
        if (!entry || entry.blocked || entry.snapshot.status === "error") return
        this.update(entry, {
          reportVersion: entry.snapshot.reportVersion + 1,
        })
      })
    )
    connection.on("RunDelta", (value) =>
      this.receive(connection, () => {
        const delta = parseRunDelta(value)
        const entry = this.entries.get(`run:${delta.runId.toLowerCase()}`)
        if (!entry || entry.blocked || entry.snapshot.status === "error") return
        if (entry.runEvents.length + delta.events.length > 5000) {
          entry.runEvents = []
          entry.pendingDelta = undefined
          this.update(entry, {
            delta: null,
            resyncVersion: entry.snapshot.resyncVersion + 1,
          })
          return
        }
        entry.runEvents.push(...delta.events)
        entry.pendingDelta = delta
        this.markDirty(entry)
      })
    )
    connection.on("ListDelta", (value) =>
      this.receive(connection, () => {
        const delta = parseListDelta(value)
        const entry = this.entries.get(resourceKey(delta))
        if (!entry || entry.blocked || entry.snapshot.status === "error") return
        for (const item of delta.items) entry.items.set(item.runId, item)
        if (entry.items.size > 10000) {
          entry.items.clear()
          entry.pendingDelta = undefined
          this.update(entry, {
            delta: null,
            resyncVersion: entry.snapshot.resyncVersion + 1,
          })
          return
        }
        entry.pendingDelta = delta
        this.markDirty(entry)
      })
    )
    connection.on("Resync", (value) =>
      this.receive(connection, () => {
        const resource = parseResync(value)
        const key = resourceKey(resource)
        const entry = this.entries.get(key)
        if (!entry) return
        this.subscribed.delete(key)
        entry.blocked = false
        entry.runEvents = []
        entry.items.clear()
        entry.pendingDelta = undefined
        this.update(entry, {
          delta: null,
          resyncVersion: entry.snapshot.resyncVersion + 1,
        })
        this.schedule()
      })
    )
    connection.onreconnecting(() => {
      if (this.connection !== connection) return
      this.state = "reconnecting"
      this.subscribed.clear()
      for (const entry of this.entries.values()) {
        if (!entry.blocked) this.update(entry, { status: "reconnecting" })
      }
    })
    connection.onreconnected(() => {
      if (this.connection !== connection) return
      this.state = "connected"
      draining = false
      this.subscribed.clear()
      for (const entry of this.entries.values()) {
        if (entry.blocked) continue
        if (!entry.blocked)
          this.update(entry, {
            resyncVersion: entry.snapshot.resyncVersion + 1,
          })
      }
      this.schedule()
    })
    connection.onclose((cause) => {
      if (this.connection !== connection) return
      this.state = "disconnected"
      this.subscribed.clear()
      if (draining) {
        this.connection = undefined
        this.schedule()
        return
      }
      this.fail(
        new Error("The live connection closed. Retry to reconnect.", { cause })
      )
    })
  }

  private receive(connection: LiveTransport, apply: () => void): void {
    if (this.connection !== connection) return
    try {
      apply()
    } catch (cause) {
      this.fail(
        new Error(
          "The server returned an invalid live message. Reload the view.",
          { cause }
        )
      )
    }
  }

  private fail(error: Error): void {
    for (const entry of this.entries.values()) {
      entry.runEvents = []
      entry.items.clear()
      entry.pendingDelta = undefined
      this.update(entry, { status: "error", error, delta: null })
    }
  }

  private update(
    entry: Entry,
    patch: Partial<Omit<LiveSnapshot, "resource">>
  ): void {
    entry.snapshot = { ...entry.snapshot, ...patch }
    this.markDirty(entry)
  }

  private markDirty(entry: Entry): void {
    this.dirty.add(entry)
    this.frame ??= requestAnimationFrame(() => {
      this.frame = undefined
      const pending = [...this.dirty]
      this.dirty.clear()
      for (const current of pending) {
        if (current.pendingDelta) {
          const delta = current.pendingDelta
          current.snapshot = {
            ...current.snapshot,
            delta:
              "events" in delta
                ? { ...delta, events: current.runEvents }
                : { ...delta, items: [...current.items.values()] },
          }
        }
        current.pendingDelta = undefined
        current.runEvents = []
        current.items.clear()
        for (const listener of current.listeners) listener()
      }
    })
  }
}

export const liveSubscriptions = new LiveSubscriptions()

if (import.meta.hot) {
  import.meta.hot.dispose(() => liveSubscriptions.reset())
}
