import { useId, useRef, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { VariablesForm } from "@/components/VariablesForm"
import { Button } from "@/components/ui/button"
import type { RunPlacement } from "@/lib/live/runApi"
import { PlacementFields } from "./PlacementFields"
import { usePermissions } from "@/lib/auth/usePermissions"
import { ApiError } from "@/lib/api/client"
import {
  getTriggerSchema,
  maximumTriggerBytes,
  triggerRequestBytes,
  triggerRun,
  type TriggerRequest,
} from "./trigger-api"
import {
  validateVariables,
  variablesDraft,
  type TriggerSchema,
} from "./variables"

export function TriggerDialog({
  workflowId,
  versionId,
}: {
  workflowId: string
  versionId?: string
}) {
  const [open, setOpen] = useState(false)
  const dialog = useRef<HTMLDialogElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const title = useId()
  const permissions = usePermissions({
    scope: "type",
    id: workflowId.split("/").slice(0, 2).join("/"),
  })
  const canTrigger = permissions.can("Operator")
  const query = useQuery({
    queryKey: ["trigger-schema", workflowId, versionId],
    queryFn: ({ signal }) => getTriggerSchema(workflowId, versionId, signal),
    enabled: open && canTrigger,
    retry: false,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
  return (
    <>
      {canTrigger ? (
        <Button
          ref={trigger}
          aria-haspopup="dialog"
          onClick={() => {
            setOpen(true)
            dialog.current?.showModal()
          }}
        >
          Trigger run
        </Button>
      ) : null}
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      <dialog
        ref={dialog}
        aria-labelledby={title}
        aria-describedby={`${title}-help`}
        onClose={() => {
          setOpen(false)
          trigger.current?.focus()
        }}
        className="m-auto max-h-[90dvh] w-[min(40rem,calc(100vw-2rem))] overflow-y-auto rounded-lg border bg-background p-5 text-foreground shadow-xl backdrop:bg-black/50"
      >
        <div className="space-y-5">
          <h2 id={title} className="text-xl font-semibold">
            Trigger workflow run
          </h2>
          <p id={`${title}-help`} className="break-all">
            {workflowId}. Manual runs do not change any schedule.
          </p>
          {!canTrigger ? (
            <p role="alert">
              Operator permission is required to trigger this workflow.
            </p>
          ) : null}
          {query.isFetching ? (
            <p role="status">Loading variable schema...</p>
          ) : null}
          {query.error ? (
            <>
              <ErrorNotice error={query.error} />
              <Button
                variant="outline"
                disabled={!canTrigger || query.isFetching}
                onClick={() => void query.refetch()}
              >
                Retry variable schema
              </Button>
            </>
          ) : null}
          {query.data && !query.error ? (
            <div hidden={!canTrigger}>
              <TriggerForm
                key={query.data.versionId}
                schema={query.data}
                canTrigger={canTrigger}
              />
            </div>
          ) : null}
          <Button variant="outline" onClick={() => dialog.current?.close()}>
            Close
          </Button>
        </div>
      </dialog>
    </>
  )
}

function TriggerForm({
  schema,
  canTrigger,
}: {
  schema: TriggerSchema
  canTrigger: boolean
}) {
  const [draft, setDraft] = useState(() => variablesDraft(schema.variables))
  const [placement, setPlacement] = useState<RunPlacement>()
  const [attempt, setAttempt] = useState<{
    request: TriggerRequest
    key: string
  } | null>(null)
  const client = useQueryClient()
  const validation = validateVariables(schema.variables, draft)
  const request: TriggerRequest = {
    workflowId: schema.workflowId,
    version: schema.versionId,
    variables: validation.variables,
    ...(placement ? { placement } : {}),
  }
  const tooLarge = triggerRequestBytes(request) > maximumTriggerBytes
  const mutation = useMutation({
    retry: false,
    mutationFn: (submission: { request: TriggerRequest; key: string }) => {
      if (!canTrigger)
        throw new Error(
          "Operator permission is required to trigger this workflow."
        )
      return triggerRun(submission.request, submission.key)
    },
    onSuccess: () => {
      setAttempt(null)
      void client.invalidateQueries({ queryKey: ["run-history"] })
      void client.invalidateQueries({ queryKey: ["runs"] })
      void client.invalidateQueries({ queryKey: ["catalog"] })
    },
    onError: (error) => {
      if (
        error instanceof ApiError &&
        [400, 403, 404, 413, 415, 422].includes(error.status)
      )
        setAttempt(null)
    },
  })
  const errors = new Map(validation.errors)
  if (mutation.error instanceof ApiError) {
    const prefix = "$.variables["
    for (const [path, message] of mutation.error.fields) {
      if (!path.startsWith(prefix) || !path.endsWith("]")) continue
      try {
        const key: unknown = JSON.parse(path.slice(prefix.length, -1))
        if (
          typeof key === "string" &&
          schema.variables.some((variable) => variable.key === key)
        )
          errors.set(key, message)
      } catch (error) {
        if (!(error instanceof SyntaxError)) throw error
      }
    }
  }
  const frozen = mutation.isPending || attempt !== null || mutation.isSuccess
  return (
    <form
      noValidate
      aria-label="Trigger run"
      className="space-y-5"
      onSubmit={(event) => {
        event.preventDefault()
        if (
          !canTrigger ||
          errors.size ||
          tooLarge ||
          mutation.isPending ||
          mutation.isSuccess
        )
          return
        const submission = attempt ?? { request, key: crypto.randomUUID() }
        setAttempt(submission)
        mutation.mutate(submission)
      }}
    >
      <p className="break-all">
        Pinned version: {schema.version} ({schema.versionId}). This captured
        immutable version will run even if the active version changes.
      </p>
      <VariablesForm
        definitions={schema.variables}
        draft={draft}
        errors={errors}
        disabled={!canTrigger || frozen}
        onChange={(next) => {
          setDraft(next)
          mutation.reset()
        }}
      />
      <PlacementFields
        value={placement}
        onChange={(value) => {
          setPlacement(value)
          mutation.reset()
        }}
        source={request}
        disabled={!canTrigger || frozen}
        valid={errors.size === 0}
      />
      {tooLarge ? (
        <p role="alert">
          The run request exceeds 1 MiB. Reduce its variable payload.
        </p>
      ) : null}
      {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
      {attempt && mutation.isError ? (
        <p>
          Acceptance is uncertain. Retry reuses the exact variables, pinned
          version and request key. Closing and reopening this dialog preserves
          that retry; do not start a separate run.
        </p>
      ) : null}
      {!mutation.isSuccess ? (
        <Button
          type="submit"
          disabled={
            !canTrigger || errors.size > 0 || tooLarge || mutation.isPending
          }
        >
          {mutation.isPending
            ? "Accepting run..."
            : attempt && mutation.isError
              ? "Retry accept run"
              : "Accept run"}
        </Button>
      ) : null}
      {mutation.data ? (
        <div role="status" className="space-y-2">
          <p>Manual run accepted. The engine creates the run asynchronously.</p>
          <Link
            className="block font-mono break-all"
            to="/runs/$id"
            params={{ id: mutation.data.runId }}
          >
            {mutation.data.runId}
          </Link>
          <Button
            variant="outline"
            onClick={() => {
              mutation.reset()
              setDraft(variablesDraft(schema.variables))
            }}
          >
            Prepare another run
          </Button>
        </div>
      ) : null}
    </form>
  )
}
