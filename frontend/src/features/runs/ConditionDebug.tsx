import { useId, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ApiError } from "@/lib/api/client"
import { getRunConditions, type ConditionDebugDetails } from "./conditions-api"

interface DebugProps {
  readonly runId: string
  readonly versionId: string
  readonly entityKey: string
  readonly revision: string
  readonly blockedByNode?: string
}

export function ConditionDebug(props: DebugProps) {
  const [open, setOpen] = useState(false)
  const id = useId()
  return (
    <div className="mt-2 min-w-0">
      <Button
        type="button"
        variant="outline"
        size="sm"
        aria-label={`${open ? "Hide" : "Show"} debug details for ${props.entityKey}`}
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((value) => !value)}
      >
        {open ? "Hide debug details" : "Show debug details"}
      </Button>
      {open ? <ConditionDebugPanel {...props} id={id} /> : null}
    </div>
  )
}

function ConditionDebugPanel({
  id,
  ...props
}: DebugProps & { readonly id: string }) {
  const query = useQuery({
    queryKey: ["run-conditions", props.runId, props.versionId, props.revision],
    queryFn: ({ signal }) =>
      getRunConditions(props.runId, props.versionId, signal),
    retry: false,
    gcTime: 0,
  })
  const details = !query.error && !query.isFetching ? query.data : undefined
  const own = details?.conditions.find(
    (item) => item.entityKey === props.entityKey
  )
  const parent = details?.conditions.find(
    (item) => item.entityKey === props.blockedByNode
  )
  const denied =
    query.error instanceof ApiError && [401, 403].includes(query.error.status)
  return (
    <section
      id={id}
      aria-label={`Condition debug details for ${props.entityKey}`}
      className="mt-2 flex min-w-0 flex-col gap-2 border p-3 [overflow-wrap:anywhere]"
    >
      <p className="font-semibold">
        Condition debug details: {props.entityKey}
      </p>
      <Button
        type="button"
        variant="outline"
        size="sm"
        disabled={query.isFetching}
        onClick={() => void query.refetch()}
      >
        Refresh debug details
      </Button>
      {query.isFetching ? (
        <p role="status">Loading condition evidence...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {denied ? (
        <p>
          Previously loaded condition details are hidden because access is no
          longer authorized.
        </p>
      ) : null}
      {details ? (
        <>
          <p>
            Pinned workflow version: <code>{details.versionId}</code>. Recorded
            through event {details.lastSeq}.
          </p>
          {props.blockedByNode ? (
            <p>
              Blocked by node condition: {props.blockedByNode}. No task
              condition decision recorded.
            </p>
          ) : null}
          {parent ? (
            <ConditionEvidence details={parent} currentRunId={props.runId} />
          ) : null}
          {own ? (
            <ConditionEvidence details={own} currentRunId={props.runId} />
          ) : null}
          {!own && !parent ? (
            <p>
              No authored condition evidence is available for this entity. A
              dependency skip or legacy run may not have a condition decision.
            </p>
          ) : null}
        </>
      ) : null}
    </section>
  )
}

function ConditionEvidence({
  details,
  currentRunId,
}: {
  readonly details: ConditionDebugDetails
  readonly currentRunId: string
}) {
  const reference =
    details.sourceKind === "RunVariable"
      ? `variables[${JSON.stringify(details.variableKey)}]`
      : `outputs[${JSON.stringify(details.nodeKey)}][${JSON.stringify(details.outputKey)}]`
  const actual =
    details.actual !== null
      ? String(details.actual)
      : details.outcome === "MissingValue"
        ? "Missing value"
        : details.outcome === "InvalidType"
          ? "Non-Boolean value (not disclosed)"
          : "Not recorded; no value is inferred from current data"
  return (
    <div className="flex min-w-0 flex-col gap-2">
      <p className="font-medium">Condition on {details.entityKey}</p>
      <dl className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1">
        <dt>Expression</dt>
        <dd>
          <code>
            {reference} === {String(details.expected)}
          </code>
        </dd>
        <dt>Expected value</dt>
        <dd>{String(details.expected)}</dd>
        <dt>Actual value</dt>
        <dd>{actual}</dd>
        <dt>Decision</dt>
        <dd>
          {details.outcome ??
            "No condition decision recorded; not evaluated or legacy evidence unavailable"}
        </dd>
        <dt>Evaluated at</dt>
        <dd>
          {details.evaluatedAt ? (
            <time dateTime={details.evaluatedAt}>{details.evaluatedAt}</time>
          ) : (
            "Not recorded"
          )}
        </dd>
        <dt>Decision event</dt>
        <dd>{details.evaluatedSequence ?? "Not recorded"}</dd>
        <dt>Value source</dt>
        <dd>
          {details.sourceKind === "RunVariable"
            ? `Captured run variable: ${details.variableKey}`
            : `Committed node output: ${details.nodeKey}/${details.outputKey}`}
        </dd>
        {details.sourceKind === "RunVariable" ? (
          <>
            <dt>Declared default</dt>
            <dd>
              {details.declaredDefault === null
                ? "No Boolean default recorded"
                : String(details.declaredDefault)}
            </dd>
          </>
        ) : (
          <>
            <dt>Committed by</dt>
            <dd>
              {details.producerTaskKey
                ? `${details.producerTaskKey}, attempt ${details.producerAttempt}`
                : "Producer task/attempt not recorded or unavailable"}
            </dd>
            <dt>Committed at</dt>
            <dd>
              {details.producedAt ? (
                <time dateTime={details.producedAt}>{details.producedAt}</time>
              ) : (
                "Not recorded"
              )}
            </dd>
          </>
        )}
        <dt>Source run</dt>
        <dd>
          <code>{details.sourceRunId}</code>
          {details.sourceRunId !== currentRunId
            ? " (reused node outputs)"
            : " (this run)"}
        </dd>
      </dl>
      <p className="text-xs text-muted-foreground">
        {details.sourceKind === "RunVariable"
          ? "Value captured for this run. Original default/override source was not recorded; the declared default does not establish how the value was supplied."
          : "Producer means the successful task attempt that committed this output key before the decision, not proof of where arbitrary task code originally calculated it. Later values are not substituted."}
      </p>
    </div>
  )
}
