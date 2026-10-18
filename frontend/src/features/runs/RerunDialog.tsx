import { useRef, useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { ApiError } from "@/lib/api/client"
import { usePermissions } from "@/lib/auth/usePermissions"
import type { RunPlacement, RunSnapshot } from "@/lib/live/runApi"
import { rerunRun } from "./rerun-api"
import { PlacementFields } from "./PlacementFields"

export function RerunDialog({
  runId,
  versionId,
  status,
  live,
  placement: originalPlacement,
}: Pick<RunSnapshot, "runId" | "versionId" | "status" | "placement"> & {
  live: boolean
}) {
  const [open, setOpen] = useState(false)
  const attempt = useRef<{ key: string; placement?: RunPlacement }>(undefined)
  const [changePlacement, setChangePlacement] = useState(false)
  const [placement, setPlacement] = useState<RunPlacement>(
    originalPlacement ?? { singleMachine: false }
  )
  const pending = useRef(false)
  const controls = useRef<HTMLElement>(null)
  const permissions = usePermissions({ scope: "run", id: runId })
  const canRerun = permissions.can("Operator")
  const eligible = live && status >= 4
  const client = useQueryClient()
  const mutation = useMutation({
    retry: false,
    mutationFn: (submission: { key: string; placement?: RunPlacement }) => {
      if (!canRerun) throw new Error("Current Operator permission is required.")
      if (!eligible)
        throw new Error("A confirmed terminal source run is required.")
      return submission.placement
        ? rerunRun(runId, submission.key, submission.placement)
        : rerunRun(runId, submission.key)
    },
    onSuccess: async () => {
      await Promise.all([
        client.invalidateQueries({ queryKey: ["run-history"] }),
        client.invalidateQueries({ queryKey: ["runs"] }),
        client.invalidateQueries({ queryKey: ["catalog"] }),
      ])
    },
    onError: async (error) => {
      if (error instanceof ApiError && error.status === 403)
        await client.invalidateQueries({ queryKey: ["permissions"] })
    },
    onSettled: () => {
      pending.current = false
    },
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!eligible || !canRerun || pending.current || mutation.isSuccess) return
    attempt.current ??= {
      key: crypto.randomUUID(),
      ...(changePlacement ? { placement } : {}),
    }
    pending.current = true
    mutation.mutate(attempt.current)
  }
  return (
    <section
      ref={controls}
      tabIndex={-1}
      aria-label="Rerun controls"
      className="min-w-0 space-y-3"
    >
      <AlertDialog
        open={open}
        onOpenChange={(next) => {
          if (!pending.current) setOpen(next)
        }}
      >
        {eligible && canRerun ? (
          <AlertDialogTrigger asChild>
            <Button variant="outline">Re-run from failed</Button>
          </AlertDialogTrigger>
        ) : null}
        <AlertDialogContent
          className="max-h-[calc(100dvh-2rem)] overflow-y-auto"
          onCloseAutoFocus={(event) => {
            if (!eligible || !canRerun) {
              event.preventDefault()
              controls.current?.focus()
            }
          }}
        >
          <form
            aria-label="Confirm rerun"
            onSubmit={submit}
            className="min-w-0 space-y-4"
          >
            <AlertDialogHeader>
              <AlertDialogTitle>Re-run from failed nodes?</AlertDialogTitle>
              <AlertDialogDescription>
                Creates a new run with the original pinned version, variables
                and metadata. Failed or incomplete nodes and their downstream
                nodes run again. Successful unaffected nodes reuse their
                original outputs. Setup and cleanup stages run again. The
                original run, active version and schedules stay unchanged.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <p className="break-all">Source run: {runId}</p>
            <p className="break-all">Pinned version: {versionId}</p>
            <p>
              Original placement:{" "}
              {originalPlacement?.singleMachine
                ? `single machine (${originalPlacement.machine ?? "auto-picked"})`
                : "workflow defaults"}
              . Unchanged unless overridden below.
            </p>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={changePlacement}
                disabled={!canRerun || !eligible || !mutation.isIdle}
                onChange={(event) => setChangePlacement(event.target.checked)}
              />
              Change placement for rerun
            </label>
            {changePlacement ? (
              <PlacementFields
                value={placement}
                onChange={setPlacement}
                source={{ runId }}
                disabled={!canRerun || !eligible || !mutation.isIdle}
              />
            ) : null}
            {!eligible ? (
              <p role="status">Waiting for confirmed terminal run state.</p>
            ) : null}
            {!canRerun ? (
              <p role="status">
                Current Operator permission is required. Checking or
                unavailable.
              </p>
            ) : null}
            {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
            {mutation.isError ? (
              <p>
                Retry uses the same request key to avoid creating a duplicate
                run.
              </p>
            ) : null}
            {mutation.data ? (
              <div role="status" className="space-y-2 break-all">
                <p>Rerun accepted. Execution may still be queued.</p>
                <Link to="/runs/$id" params={{ id: mutation.data.runId }}>
                  Open rerun {mutation.data.runId}
                </Link>
              </div>
            ) : null}
            <AlertDialogFooter>
              <AlertDialogCancel disabled={mutation.isPending}>
                {mutation.isSuccess ? "Close" : "Cancel"}
              </AlertDialogCancel>
              <Button
                type="submit"
                disabled={
                  !eligible ||
                  !canRerun ||
                  mutation.isPending ||
                  mutation.isSuccess
                }
              >
                {mutation.isPending
                  ? "Accepting rerun..."
                  : mutation.isError
                    ? "Retry rerun"
                    : "Confirm rerun"}
              </Button>
            </AlertDialogFooter>
          </form>
        </AlertDialogContent>
      </AlertDialog>
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
    </section>
  )
}
