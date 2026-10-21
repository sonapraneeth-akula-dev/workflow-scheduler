import { useId, useRef, useState, type FormEvent } from "react"
import {
  useInfiniteQuery,
  useMutation,
  useQueryClient,
} from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Textarea } from "@/components/ui/textarea"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { usePermissions } from "@/lib/auth/usePermissions"
import { formatTimestamp } from "@/lib/timestamps"
import {
  changeSignOff,
  getSignOff,
  signOffReasonSchema,
  type SignOffEvent,
  type SignOffState,
  type SignOffTarget,
} from "./signoff-api"

export function SignOffBadge({ state }: { state: SignOffState }) {
  return (
    <Badge
      variant={
        state.status === "SignedOff"
          ? "default"
          : state.status === "Revoked"
            ? "destructive"
            : "outline"
      }
    >
      {state.status === "SignedOff"
        ? "Signed off"
        : state.status === "Revoked"
          ? "Revoked"
          : "Not signed off"}
    </Badge>
  )
}

function EventDetails({ event }: { event: SignOffEvent }) {
  return (
    <div className="min-w-0 space-y-1 text-sm [overflow-wrap:anywhere]">
      <p>
        {event.action === "SignOff" ? "Signed off" : "Revoked"} by{" "}
        <span className="font-medium">{event.actorName}</span>{" "}
        <time dateTime={event.at} title={event.at}>
          {formatTimestamp(event.at)}
        </time>
      </p>
      <p className="text-xs text-muted-foreground">
        Actor: <code>{event.actorId}</code>
      </p>
      <p className="whitespace-pre-wrap">{event.reason}</p>
    </div>
  )
}

export function SignOffPanel(target: SignOffTarget) {
  const { runId, reportId } = target
  const label = reportId ? "Report" : "Workflow"
  const permissions = usePermissions({ scope: "run", id: runId })
  const permitted = permissions.isSuccess && permissions.data.viewer
  const query = useInfiniteQuery({
    queryKey: ["signoff", runId, reportId ?? null],
    queryFn: ({ pageParam, signal }) => getSignOff(target, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextBefore ?? undefined,
    enabled: permitted,
    retry: false,
    refetchInterval: 30_000,
  })
  const client = useQueryClient()
  const [intent, setIntent] = useState<{
    action: "SignOff" | "Revoke"
    revision: string
  }>()
  const [reason, setReason] = useState("")
  const id = useId()
  const panel = useRef<HTMLElement>(null)
  const mutation = useMutation({
    mutationKey: ["signoff-change", runId, reportId ?? null],
    retry: false,
    mutationFn: (value: {
      action: "SignOff" | "Revoke"
      revision: string
      reason: string
    }) => {
      if (!permissions.can("Operator"))
        throw new Error("Current Operator permission is required.")
      return changeSignOff(target, value.revision, value.action, value.reason)
    },
    onSuccess: () => {
      setIntent(undefined)
      setReason("")
      for (const key of [
        "signoff",
        "runs",
        "run-history",
        "service-history",
        "reports",
      ])
        void client.invalidateQueries({ queryKey: [key] })
    },
  })
  const response = permitted && !query.error ? query.data?.pages[0] : undefined
  const state = response?.state
  const canChange =
    permissions.can("Operator") &&
    response?.isTerminal === true &&
    !query.isFetching &&
    !mutation.isPending

  async function open() {
    mutation.reset()
    const fresh = await query.refetch()
    if (fresh.isError || !fresh.data) return
    const current = fresh.data.pages[0]
    if (!current?.isTerminal) return
    setIntent({
      action: current.state.status === "SignedOff" ? "Revoke" : "SignOff",
      revision: current.state.revision,
    })
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (
      !intent ||
      !canChange ||
      mutation.isError ||
      !signOffReasonSchema.safeParse(reason).success
    )
      return
    mutation.mutate({ ...intent, reason })
  }

  return (
    <section
      ref={panel}
      tabIndex={-1}
      aria-label={`${label} sign-off`}
      className="min-w-0 space-y-3 rounded border bg-card p-3"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="font-semibold">{label} sign-off</h2>
          {state ? <SignOffBadge state={state} /> : null}
        </div>
        <div className="flex flex-wrap gap-2">
          {canChange ? (
            <Button size="sm" variant="outline" onClick={() => void open()}>
              {state?.status === "SignedOff"
                ? "Revoke sign-off"
                : `Sign off ${reportId ? "report" : "workflow"}`}
            </Button>
          ) : null}
          {permitted ? (
            <Button
              size="sm"
              variant="ghost"
              disabled={query.isFetching || mutation.isPending}
              onClick={() => void query.refetch()}
            >
              Refresh sign-off
            </Button>
          ) : null}
        </div>
      </div>
      <p className="text-sm text-muted-foreground">
        {reportId
          ? "This report only. Workflow approval is independent."
          : "This completed run only. Report approvals are independent; future runs are not approved."}
      </p>
      {response ? (
        <p className="text-xs break-all text-muted-foreground">
          Pinned version: {response.versionId}
          {!response.isTerminal
            ? " - Sign-off becomes available when the run completes."
            : ""}
        </p>
      ) : null}
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {!permissions.isSuccess && !permissions.error ? (
        <p role="status">Checking sign-off permissions...</p>
      ) : null}
      {permissions.isSuccess && !permissions.data.viewer ? (
        <p role="alert">Current Run Viewer permission is required.</p>
      ) : null}
      {permitted && query.isPending ? (
        <p role="status">Loading sign-off...</p>
      ) : null}
      {query.error && permitted ? <ErrorNotice error={query.error} /> : null}
      {state?.latest ? <EventDetails event={state.latest} /> : null}
      {response ? (
        <details className="min-w-0 space-y-3">
          <summary className="w-fit rounded text-sm font-medium focus-visible:outline-2 focus-visible:outline-ring">
            Sign-off history
          </summary>
          <p className="text-xs text-muted-foreground">
            Newest first. Approval and revocation reasons remain in history.
          </p>
          <ol className="min-w-0 space-y-3">
            {query.data?.pages.flatMap((page) =>
              page.history.map((event) => (
                <li key={event.revision} className="border-l-2 pl-3">
                  <EventDetails event={event} />
                </li>
              ))
            )}
          </ol>
          {state?.revision === "0" ? (
            <p className="text-sm">No sign-off events recorded.</p>
          ) : null}
          {query.hasNextPage ? (
            <Button
              size="sm"
              variant="outline"
              disabled={query.isFetching}
              onClick={() => void query.fetchNextPage()}
            >
              {query.isFetchingNextPage
                ? "Loading older sign-offs..."
                : "Load older sign-offs"}
            </Button>
          ) : null}
        </details>
      ) : null}
      {!intent && mutation.error ? (
        <ErrorNotice error={mutation.error} />
      ) : null}
      <AlertDialog
        open={intent !== undefined && permitted}
        onOpenChange={(open) => {
          if (!open) setIntent(undefined)
        }}
      >
        <AlertDialogContent
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            panel.current?.focus()
          }}
        >
          <form
            aria-label={
              intent?.action === "Revoke"
                ? "Revoke sign-off"
                : `Sign off ${label.toLowerCase()}`
            }
            onSubmit={submit}
            className="min-w-0 space-y-4"
          >
            <AlertDialogHeader>
              <AlertDialogTitle>
                {intent?.action === "Revoke"
                  ? `Revoke ${label.toLowerCase()} sign-off?`
                  : `Sign off this ${label.toLowerCase()}?`}
              </AlertDialogTitle>
              <AlertDialogDescription>
                {reportId
                  ? "Applies only to this report in this run."
                  : "Applies only to this completed run and its pinned workflow version, not its reports or future runs."}{" "}
                Your identity, reason and time will be visible in permanent
                sign-off history. Revocation never erases earlier approvals.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <div className="space-y-2">
              <label htmlFor={id} className="text-sm font-medium">
                {intent?.action === "Revoke"
                  ? "Revocation reason"
                  : "Sign-off reason"}
              </label>
              <Textarea
                id={id}
                required
                maxLength={2000}
                value={reason}
                readOnly={mutation.isPending || mutation.isError}
                onChange={(event) => setReason(event.currentTarget.value)}
                aria-describedby={`${id}-help`}
                className="min-h-24"
              />
              <p id={`${id}-help`} className="text-xs text-muted-foreground">
                Required, 1-2000 characters. Do not include secrets.
              </p>
            </div>
            {!permissions.can("Operator") ? (
              <p role="alert">Current Operator permission is not available.</p>
            ) : null}
            {mutation.error ? (
              <>
                <ErrorNotice error={mutation.error} />
                <p className="text-sm">
                  No automatic retry. Close this dialog and refresh sign-off to
                  verify the current state before making another change.
                </p>
              </>
            ) : null}
            <AlertDialogFooter>
              <AlertDialogCancel>
                {mutation.isPending ? "Close dialog" : "Cancel"}
              </AlertDialogCancel>
              <Button
                type="submit"
                disabled={
                  !canChange ||
                  mutation.isError ||
                  !signOffReasonSchema.safeParse(reason).success
                }
              >
                {mutation.isPending
                  ? "Saving sign-off..."
                  : intent?.action === "Revoke"
                    ? "Confirm revocation"
                    : "Confirm sign-off"}
              </Button>
            </AlertDialogFooter>
          </form>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
