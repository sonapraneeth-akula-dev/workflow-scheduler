import { useEffect } from "react"
import { useQueryClient } from "@tanstack/react-query"
import { useLive } from "@/lib/live/useLive"

export function useCatalogLive(team: string, type: string) {
  const { snapshot, retry } = useLive({ scope: "type", id: `${team}/${type}` })
  const client = useQueryClient()
  const delta = snapshot?.delta
  const resync = snapshot?.resyncVersion ?? 0
  const error = snapshot?.error
  useEffect(() => {
    if (!delta && !resync && !error) return
    void client.invalidateQueries({
      queryKey: ["catalog", "team", team],
      predicate: (query) =>
        query.queryKey[3] === "types" ||
        (query.queryKey[3] === "workflows" && query.queryKey[4] === type),
    })
  }, [client, team, type, delta, resync, error])
  return { snapshot, retry }
}
