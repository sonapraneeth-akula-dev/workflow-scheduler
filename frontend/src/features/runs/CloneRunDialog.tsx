import { useId, useRef, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { z } from "zod"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Textarea } from "@/components/ui/textarea"
import { ApiError } from "@/lib/api/client"
import { usePermissions } from "@/lib/auth/usePermissions"
import {
  JsonNumberSupportError,
  jsonObjectSchema,
  parseJson,
  type JsonValue,
} from "@/lib/json"
import type { RunSnapshot } from "@/lib/live/runApi"
import {
  cloneRequestBytes,
  cloneRun,
  getCloneSource,
  type CloneRequest,
  type CloneSource,
} from "./clone-api"
import { maximumTriggerBytes } from "./trigger-api"

export function CloneRunDialog({
  runId,
  workflowId,
  versionId,
  status,
  live,
}: Pick<RunSnapshot, "runId" | "workflowId" | "versionId" | "status"> & {
  live: boolean
}) {
  const [open, setOpen] = useState(false)
  const dialog = useRef<HTMLDialogElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const controls = useRef<HTMLElement>(null)
  const title = useId()
  const permissions = usePermissions({ scope: "run", id: runId })
  const canClone = permissions.can("Operator")
  const eligible = live && status >= 3
  const query = useQuery({
    queryKey: ["clone-source", runId, versionId],
    queryFn: ({ signal }) =>
      getCloneSource({ runId, workflowId, versionId }, signal),
    enabled: open && canClone && eligible,
    retry: false,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
  return (
    <section
      ref={controls}
      tabIndex={-1}
      aria-label="Clone run controls"
      className="min-w-0 space-y-3"
    >
      {canClone && eligible ? (
        <Button
          ref={trigger}
          variant="outline"
          aria-haspopup="dialog"
          onClick={() => {
            setOpen(true)
            dialog.current?.showModal()
          }}
        >
          Clone run
        </Button>
      ) : null}
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      <dialog
        ref={dialog}
        aria-labelledby={title}
        aria-describedby={`${title}-help`}
        onClose={() => {
          setOpen(false)
          if (canClone && eligible) trigger.current?.focus()
          else controls.current?.focus()
        }}
        className="m-auto max-h-[90dvh] w-[min(48rem,calc(100vw-2rem))] overflow-y-auto border bg-background p-5 text-foreground shadow-xl backdrop:bg-black/50"
      >
        <div className="min-w-0 space-y-4">
          <h2 id={title} className="text-xl font-semibold">
            Clone run
          </h2>
          <p id={`${title}-help`}>
            Start a fresh full run of this run's exact pinned version with
            edited variables. Every node executes again; no successful outputs
            are reused. Source run, active version and saved schedules remain
            unchanged.
          </p>
          {!canClone || !eligible ? (
            <p role="status">
              Current Operator permission and a confirmed terminal source run
              are required.
            </p>
          ) : null}
          {query.isFetching ? (
            <p role="status">Loading source run variables...</p>
          ) : null}
          {query.error ? (
            <>
              <ErrorNotice error={query.error} />
              <Button
                variant="outline"
                disabled={!canClone || !eligible || query.isFetching}
                onClick={() => void query.refetch()}
              >
                Retry clone source
              </Button>
            </>
          ) : null}
          {query.data && !query.error ? (
            <div hidden={!canClone || !eligible}>
              <CloneForm
                key={query.data.runId}
                source={query.data}
                allowed={canClone && eligible}
              />
            </div>
          ) : null}
          <Button variant="outline" onClick={() => dialog.current?.close()}>
            Close
          </Button>
        </div>
      </dialog>
    </section>
  )
}

function CloneForm({
  source,
  allowed,
}: {
  source: CloneSource
  allowed: boolean
}) {
  const id = useId()
  const [draft, setDraft] = useState(() =>
    JSON.stringify(source.variables, null, 2)
  )
  const [attempt, setAttempt] = useState<{
    request: CloneRequest
    key: string
  } | null>(null)
  const pending = useRef(false)
  const client = useQueryClient()
  let variables: Record<string, JsonValue> | undefined
  let error: string | undefined
  try {
    variables = jsonObjectSchema.parse(parseJson(draft))
  } catch (caught) {
    if (caught instanceof JsonNumberSupportError) error = caught.message
    else if (caught instanceof SyntaxError)
      error = "Enter valid JSON for the variables object."
    else if (caught instanceof z.ZodError)
      error = "Variables must be a JSON object, not an array or primitive."
    else throw caught
  }
  const tooLarge =
    variables !== undefined &&
    cloneRequestBytes({ variables }) > maximumTriggerBytes
  const mutation = useMutation({
    retry: false,
    mutationFn: (submission: { request: CloneRequest; key: string }) => {
      if (!allowed)
        throw new Error(
          "Current Operator permission and a confirmed terminal run are required."
        )
      return cloneRun(source.runId, submission.request, submission.key)
    },
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["run-history"] })
      void client.invalidateQueries({ queryKey: ["runs"] })
      void client.invalidateQueries({ queryKey: ["catalog"] })
    },
    onError: (caught) => {
      if (
        caught instanceof ApiError &&
        [400, 403, 404, 413, 415, 422].includes(caught.status)
      )
        setAttempt(null)
      if (caught instanceof ApiError && caught.status === 403)
        void client.invalidateQueries({ queryKey: ["permissions"] })
    },
    onSettled: () => {
      pending.current = false
    },
  })
  const frozen = attempt !== null || mutation.isPending || mutation.isSuccess
  return (
    <form
      aria-label="Clone run with edited variables"
      noValidate
      className="min-w-0 space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (
          !allowed ||
          !variables ||
          error ||
          tooLarge ||
          pending.current ||
          mutation.isSuccess
        )
          return
        const submission = attempt ?? {
          request: { variables },
          key: crypto.randomUUID(),
        }
        setAttempt(submission)
        pending.current = true
        mutation.mutate(submission)
      }}
    >
      <p className="break-all">Source run: {source.runId}</p>
      <p className="break-all">Workflow: {source.workflowId}</p>
      <p className="break-all">Pinned version: {source.versionId}</p>
      <p>
        Placement preserved:{" "}
        {source.placement?.singleMachine
          ? `single machine (${source.placement.machine ?? "auto-picked"})`
          : "workflow defaults"}
        . Parent lineage and schedule series are retained.
      </p>
      <div className="min-w-0 space-y-2">
        <label htmlFor={id} className="font-semibold">
          Variables (JSON)
        </label>
        <p id={`${id}-help`} className="text-sm text-muted-foreground">
          Edit the complete variable object. Objects stay nested; arrays,
          booleans and numbers keep their JSON types. Secret references remain
          names, never resolved values. The pinned workflow validates all edits
          before acceptance.
        </p>
        <Textarea
          id={id}
          value={draft}
          className="min-h-64 font-mono text-sm"
          autoComplete="off"
          spellCheck={false}
          disabled={!allowed || frozen}
          aria-invalid={Boolean(error || tooLarge)}
          aria-describedby={`${id}-help${error || tooLarge ? ` ${id}-error` : ""}`}
          onChange={(event) => {
            setDraft(event.target.value)
            mutation.reset()
          }}
        />
        {error || tooLarge ? (
          <p
            id={`${id}-error`}
            role="alert"
            className="text-sm text-destructive"
          >
            {error ??
              "The clone request exceeds 1 MiB. Reduce its variable payload."}
          </p>
        ) : null}
        <Button
          type="button"
          variant="outline"
          disabled={!allowed || frozen || !variables || tooLarge}
          onClick={() => {
            setDraft(JSON.stringify(variables, null, 2))
            mutation.reset()
          }}
        >
          Format JSON
        </Button>
      </div>
      {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
      {attempt && mutation.isError ? (
        <p>
          Acceptance is uncertain. Retry uses the same pinned source, exact
          variables and request key to avoid a duplicate. Closing and reopening
          this dialog preserves the retry.
        </p>
      ) : null}
      {mutation.data ? (
        <div role="status" className="space-y-2">
          <p>
            Clone accepted. The engine creates the fresh run asynchronously.
          </p>
          <Link
            className="block font-mono break-all"
            to="/runs/$id"
            params={{ id: mutation.data.runId }}
          >
            Open cloned run {mutation.data.runId}
          </Link>
        </div>
      ) : null}
      <Button
        type="submit"
        disabled={
          !allowed ||
          !variables ||
          Boolean(error) ||
          tooLarge ||
          mutation.isPending ||
          mutation.isSuccess
        }
      >
        {mutation.isPending
          ? "Accepting clone..."
          : mutation.isError && attempt
            ? "Retry clone"
            : "Confirm clone"}
      </Button>
    </form>
  )
}
