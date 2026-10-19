import { queryOptions } from "@tanstack/react-query"
import type { RunSnapshot } from "@/lib/live/runApi"
import { getTimeline } from "./timeline-api"

export function completedTimelineQuery(
  run: Pick<RunSnapshot, "runId" | "status" | "lastSeq">
) {
  return queryOptions({
    queryKey: [
      "completed-run-timeline",
      run.runId,
      run.status >= 3 ? "terminal" : "active",
    ],
    enabled: run.status >= 3,
    queryFn: async ({ signal }) => {
      const timeline = await getTimeline(run.runId, signal)
      if (
        run.status >= 3 &&
        (timeline.lastSeq < run.lastSeq ||
          !["Succeeded", "Failed", "Aborted", "TimedOut"].includes(
            timeline.status
          ))
      )
        throw new Error(
          "The committed attempt history has not reached the terminal run state."
        )
      return timeline
    },
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  })
}
