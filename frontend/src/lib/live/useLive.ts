import { useMemo, useSyncExternalStore } from "react"
import type { LiveResource } from "./messages"
import { liveSubscriptions, type LiveSubscriptions } from "./subscriptions"

export function useLive(
  resource?: LiveResource,
  manager: LiveSubscriptions = liveSubscriptions
) {
  const scope = resource?.scope
  const id = resource?.id
  const store = useMemo(
    () =>
      manager.observe(
        scope === undefined ? undefined : { scope, id: id ?? "" }
      ),
    [manager, scope, id]
  )
  const snapshot = useSyncExternalStore(
    store.subscribe,
    store.getSnapshot,
    store.getSnapshot
  )
  return { snapshot, retry: store.retry }
}
