import { memo, useId, useRef, useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Textarea } from "@/components/ui/textarea"
import { Can } from "@/components/ui/Can"
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
import { usePermissions } from "@/lib/auth/usePermissions"
import type { RunSnapshot } from "@/lib/live/runApi"
import { runStatuses } from "@/features/catalog/api"
import { abortReasonSchema, abortRun } from "./abort"

export const AbortDialog = memo(function AbortDialog({
  runId,
  status,
  abort: recordedAbort,
  live,
}: Pick<RunSnapshot, "runId" | "status" | "abort"> & { live: boolean }) {
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState("")
  const [validation, setValidation] = useState<string>()
  const id = useId()
  const controls = useRef<HTMLElement>(null)
  const client = useQueryClient()
  const permissions = usePermissions({ scope: "run", id: runId })
  const mutation = useMutation({
    mutationKey: ["abort-run", runId],
    retry: false,
    mutationFn: (reason: string) => {
      if (!permissions.can("Operator"))
        throw new Error("Operator permission is required to cancel this run.")
      if (!live || status > 1 || recordedAbort)
        throw new Error("A live queued or running run is required to cancel.")
      return abortRun(runId, reason)
    },
    onSuccess: () => {
      setOpen(false)
      void client.invalidateQueries({ queryKey: ["run-history"] })
      void client.invalidateQueries({ queryKey: ["runs"] })
    },
  })
  const eligible = live && status <= 1 && !recordedAbort && !mutation.data
  const abort = recordedAbort ?? mutation.data
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (mutation.isPending) return
    const parsed = abortReasonSchema.safeParse(reason)
    if (!parsed.success) {
      setValidation("Supply a nonblank reason of 1-500 characters.")
      return
    }
    setValidation(undefined)
    mutation.mutate(parsed.data)
  }
  return (
    <section
      ref={controls}
      tabIndex={-1}
      aria-label="Run controls"
      className="min-w-0 space-y-3"
    >
      <p>
        {live ? "Recorded run status: " : "Last confirmed run status: "}
        <StatusBadge status={runStatuses[status]!} />
      </p>
      <AlertDialog open={open} onOpenChange={setOpen}>
        {eligible ? (
          <Can permissions={permissions} role="Operator">
            <AlertDialogTrigger asChild>
              <Button variant="destructive" disabled={mutation.isPending}>
                Cancel run
              </Button>
            </AlertDialogTrigger>
          </Can>
        ) : null}
        <AlertDialogContent
          onCloseAutoFocus={(event) => {
            if (!eligible) {
              event.preventDefault()
              controls.current?.focus()
            }
          }}
        >
          <form
            aria-label="Confirm run cancellation"
            onSubmit={submit}
            className="min-w-0 space-y-4"
          >
            <AlertDialogHeader>
              <AlertDialogTitle>Cancel this run?</AlertDialogTitle>
              <AlertDialogDescription>
                Cancels queued work and signals running tasks to stop. Cleanup
                and always-run stages may continue. The schedule is not paused.
                An accepted request does not mean work has stopped.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <FieldGroup>
              <Field data-invalid={validation !== undefined}>
                <FieldLabel htmlFor={id}>Cancellation reason</FieldLabel>
                <Textarea
                  id={id}
                  value={reason}
                  onChange={(event) => {
                    setReason(event.currentTarget.value)
                    setValidation(undefined)
                  }}
                  required
                  maxLength={500}
                  readOnly={mutation.isPending}
                  aria-describedby={`${id}-help`}
                  aria-invalid={validation !== undefined}
                  className="min-h-24"
                />
                <p id={`${id}-help`} className="text-sm text-muted-foreground">
                  Required, 1-500 characters. The original actor, time, reason
                  and source are retained for audit.
                </p>
              </Field>
            </FieldGroup>
            {validation ? <p role="alert">{validation}</p> : null}
            {!live || status > 1 || recordedAbort ? (
              <p role="status">
                Run state changed. Close the dialog and review the latest
                confirmed status.
              </p>
            ) : null}
            {!permissions.can("Operator") ? (
              <p role="alert">
                Operator permission is not currently available.
              </p>
            ) : null}
            {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
            <AlertDialogFooter>
              <AlertDialogCancel>
                {mutation.isPending ? "Close dialog" : "Keep running"}
              </AlertDialogCancel>
              <Button
                type="submit"
                variant="destructive"
                disabled={
                  !eligible ||
                  !permissions.can("Operator") ||
                  mutation.isPending ||
                  !abortReasonSchema.safeParse(reason).success
                }
              >
                {mutation.isPending
                  ? "Requesting cancellation..."
                  : "Confirm cancellation"}
              </Button>
            </AlertDialogFooter>
          </form>
        </AlertDialogContent>
      </AlertDialog>
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {!open && mutation.error ? <ErrorNotice error={mutation.error} /> : null}
      {abort ? (
        <section
          aria-label="Run cancellation details"
          role="status"
          className="space-y-2 rounded-md border p-4"
        >
          <h2 className="text-lg font-semibold">
            {recordedAbort ? "Recorded cancellation" : "Cancellation requested"}
          </h2>
          <p>
            {recordedAbort
              ? status === 5
                ? "The run is cancelled (Aborted). Work has stopped."
                : "Cancellation is recorded. Cleanup may still be running; wait for Aborted."
              : "Waiting for committed run state. Acceptance does not mean tasks have stopped."}
          </p>
          <dl className="grid min-w-0 gap-2 break-words">
            <dt className="font-medium">By</dt>
            <dd>
              {abort.byName}
              <code className="block break-all">{abort.by}</code>
            </dd>
            <dt className="font-medium">At (local)</dt>
            <dd>
              <time dateTime={abort.at} title={abort.at}>
                {formatTimestamp(abort.at)}
              </time>
            </dd>
            <dt className="font-medium">Reason</dt>
            <dd className="whitespace-pre-wrap">{abort.reason}</dd>
            <dt className="font-medium">Source</dt>
            <dd>{abort.source}</dd>
          </dl>
        </section>
      ) : status === 5 ? (
        <p role="status">
          Run cancelled (Aborted); original cancellation details were not
          recorded.
        </p>
      ) : null}
    </section>
  )
})
