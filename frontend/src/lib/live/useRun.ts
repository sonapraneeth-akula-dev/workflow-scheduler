import { useMemo, useSyncExternalStore } from "react"
import { RunStore } from "./runStore"

export function useRun(runId: string) {
  const store = useMemo(() => new RunStore(runId), [runId])
  const snapshot = useSyncExternalStore(
    store.subscribe,
    store.getSnapshot,
    store.getSnapshot
  )
  return { ...snapshot, retry: store.retry }
}
