import { useId, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select"
import { ApiError } from "@/lib/api/client"
import { getTimeline } from "./timeline-api"
import { getTaskOutputs } from "./outputs-api"

const identity = (attempt: { taskKey: string; attempt: number }) =>
  JSON.stringify([attempt.taskKey, attempt.attempt])

export function TaskOutputs({ runId }: { runId: string }) {
  const id = useId()
  const [selection, setSelection] = useState<string | null>(null)
  const [refresh, setRefresh] = useState(0)
  const timeline = useQuery({
    queryKey: ["task-output-attempts", runId, refresh],
    queryFn: ({ signal }) => getTimeline(runId, signal),
    retry: false,
    gcTime: 0,
  })
  const selected =
    timeline.data?.attempts.find(
      (attempt) => identity(attempt) === selection
    ) ?? timeline.data?.attempts[0]
  const outputs = useQuery({
    queryKey: [
      "task-outputs",
      runId,
      selected?.taskKey,
      selected?.attempt,
      refresh,
    ],
    enabled: timeline.isSuccess && selected !== undefined,
    queryFn: ({ signal }) => {
      if (!selected)
        throw new Error("Select an attempt before reading outputs.")
      return getTaskOutputs(runId, selected, signal)
    },
    retry: false,
    gcTime: 0,
  })
  const error = timeline.error ?? outputs.error
  const denied = error instanceof ApiError && [401, 403].includes(error.status)
  const result =
    !error && !timeline.isFetching && !outputs.isFetching
      ? outputs.data
      : undefined
  return (
    <section aria-label="Task outputs" className="flex min-w-0 flex-col gap-4">
      <h2 className="text-xl font-semibold">Task outputs</h2>
      <p>
        Structured JSON recorded for each completed attempt, including failed
        attempts. Available while the run continues and after it ends. Refresh
        to read newly committed outputs; no outputs from other attempts are
        substituted.
      </p>
      <Button
        variant="outline"
        disabled={timeline.isFetching || outputs.isFetching}
        onClick={() => setRefresh((value) => value + 1)}
      >
        Refresh task outputs
      </Button>
      {timeline.isFetching || outputs.isFetching ? (
        <p role="status">Loading task outputs...</p>
      ) : null}
      {error ? <ErrorNotice error={error} /> : null}
      {denied ? (
        <p>
          Previously loaded outputs are hidden because access is no longer
          authorized.
        </p>
      ) : null}
      {!timeline.error && !denied && timeline.data ? (
        timeline.data.attempts.length === 0 ? (
          <p>No dispatched attempts recorded.</p>
        ) : (
          <div className="flex min-w-0 flex-col gap-2">
            <label htmlFor={id}>Output attempt</label>
            <NativeSelect
              id={id}
              className="max-w-full"
              value={selected ? identity(selected) : ""}
              onChange={(event) => setSelection(event.target.value)}
            >
              {timeline.data.attempts.map((attempt) => (
                <NativeSelectOption
                  key={identity(attempt)}
                  value={identity(attempt)}
                >
                  {attempt.taskKey} - attempt {attempt.attempt} (
                  {attempt.terminalStatus ?? "In progress"})
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </div>
        )
      ) : null}
      {result ? (
        result.outputs === null ? (
          <p role="status">No structured outputs recorded for this attempt.</p>
        ) : (
          <pre
            aria-label="Attempt JSON outputs"
            tabIndex={0}
            className="max-h-[32rem] min-w-0 overflow-auto rounded border p-3 text-sm break-all whitespace-pre-wrap"
          >
            {JSON.stringify(result.outputs, null, 2)}
          </pre>
        )
      ) : null}
    </section>
  )
}
