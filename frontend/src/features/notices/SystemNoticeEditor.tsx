import { useId, useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
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
import { changeSystemNotice, noticeQueryKey, type SystemNotice } from "./api"
import { useSystemNotice } from "./useSystemNotice"

interface Draft {
  revision: string
  message: string
  severity: SystemNotice["severity"]
  expires: string
}

function localTime(value: string | null) {
  if (!value) return ""
  const date = new Date(value)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16)
}

export function SystemNoticeEditor() {
  const query = useSystemNotice()
  const client = useQueryClient()
  const [draft, setDraft] = useState<Draft>()
  const [confirmClear, setConfirmClear] = useState(false)
  const [saved, setSaved] = useState("")
  const id = useId()
  const mutation = useMutation({
    retry: false,
    mutationFn: (clear: boolean) => {
      if (!draft || !query.data?.canManage)
        throw new Error(
          "Current Global Admin permission and a loaded notice are required."
        )
      return changeSystemNotice(
        draft.revision,
        clear
          ? null
          : {
              message: draft.message,
              severity: draft.severity,
              expiresAt: draft.expires
                ? new Date(draft.expires).toISOString()
                : null,
            }
      )
    },
    onSuccess: async (state, clear) => {
      await client.cancelQueries({ queryKey: noticeQueryKey })
      client.setQueryData(noticeQueryKey, state)
      setDraft(undefined)
      setConfirmClear(false)
      setSaved(clear ? "System notice cleared." : "System notice published.")
    },
  })
  if (!query.data?.canManage) return null
  const busy = query.isFetching || mutation.isPending
  async function edit() {
    setSaved("")
    mutation.reset()
    const fresh = await query.refetch()
    if (fresh.isError || !fresh.data?.canManage) return
    const { notice, revision } = fresh.data
    setDraft({
      revision,
      message: notice?.message ?? "",
      severity: notice?.severity ?? "Info",
      expires: localTime(notice?.expiresAt ?? null),
    })
  }
  async function reload() {
    const fresh = await query.refetch()
    if (fresh.isError || !fresh.data?.canManage) return
    setDraft((current) =>
      current ? { ...current, revision: fresh.data.revision } : current
    )
    mutation.reset()
    setConfirmClear(false)
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!busy && !query.error && !mutation.isError) mutation.mutate(false)
  }
  return (
    <section
      aria-labelledby={`${id}-title`}
      className="min-w-0 space-y-3 rounded border bg-card p-4"
    >
      <h2 id={`${id}-title`} className="font-semibold">
        System notice
      </h2>
      <p className="text-sm text-muted-foreground">
        Global Admins can publish one plain-text notice above navigation for all
        signed-in users. It stays visible until cleared or expired; users cannot
        dismiss it. Manual communication only, not automatic incident detection
        or a health guarantee. Changes are audited. Updates reach open pages
        within 30 seconds while online.
      </p>
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {saved ? <p role="status">{saved}</p> : null}
      {!draft ? (
        <Button variant="outline" disabled={busy} onClick={() => void edit()}>
          {query.data.notice ? "Edit system notice" : "Create system notice"}
        </Button>
      ) : (
        <form onSubmit={submit} className="space-y-3">
          <div className="space-y-1">
            <label htmlFor={`${id}-severity`} className="text-sm font-medium">
              Severity
            </label>
            <select
              id={`${id}-severity`}
              className="block min-h-10 w-full rounded border bg-background px-3 text-sm"
              value={draft.severity}
              disabled={mutation.isPending}
              onChange={(event) => {
                const severity = event.target.value
                if (
                  severity === "Info" ||
                  severity === "Warning" ||
                  severity === "Critical"
                )
                  setDraft({ ...draft, severity })
              }}
            >
              <option value="Info">Information</option>
              <option value="Warning">Warning</option>
              <option value="Critical">Critical</option>
            </select>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-message`} className="text-sm font-medium">
              Message
            </label>
            <Textarea
              id={`${id}-message`}
              value={draft.message}
              onChange={(event) =>
                setDraft({ ...draft, message: event.target.value })
              }
              required
              maxLength={2000}
              rows={3}
              disabled={mutation.isPending}
              aria-describedby={`${id}-help`}
            />
            <p id={`${id}-help`} className="text-xs text-muted-foreground">
              Maximum 2000 characters. HTML and links are displayed as plain
              text.
            </p>
          </div>
          <div className="space-y-1">
            <label htmlFor={`${id}-expires`} className="text-sm font-medium">
              Expiry (your local time, optional)
            </label>
            <Input
              id={`${id}-expires`}
              type="datetime-local"
              value={draft.expires}
              onChange={(event) =>
                setDraft({ ...draft, expires: event.target.value })
              }
              disabled={mutation.isPending}
            />
            <p className="text-xs text-muted-foreground">
              Leave blank to keep the notice until a Global Admin clears it.
            </p>
          </div>
          {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
          {draft.revision !== query.data.revision ? (
            <p role="status" className="text-sm text-warning">
              Another administrator changed this notice. Your draft is
              preserved. Review the current banner before reloading.
            </p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            <Button
              type="submit"
              disabled={
                busy ||
                !!query.error ||
                mutation.isError ||
                !draft.message.trim()
              }
            >
              {mutation.isPending ? "Saving..." : "Publish notice"}
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={busy}
              onClick={() => void reload()}
            >
              Reload latest notice (keep draft)
            </Button>
            {query.data.notice ? (
              <Button
                type="button"
                variant="destructive"
                disabled={busy || !!query.error || mutation.isError}
                onClick={() => setConfirmClear(true)}
              >
                Clear notice
              </Button>
            ) : null}
            <Button
              type="button"
              variant="ghost"
              disabled={mutation.isPending}
              onClick={() => {
                setDraft(undefined)
                mutation.reset()
              }}
            >
              Cancel
            </Button>
          </div>
        </form>
      )}
      <AlertDialog
        open={confirmClear}
        onOpenChange={(open) => {
          if (!mutation.isPending) setConfirmClear(open)
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Clear the system notice?</AlertDialogTitle>
            <AlertDialogDescription>
              This removes the top notice for all signed-in users. It does not
              resolve an incident or change system health.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={mutation.isPending}>
              Cancel
            </AlertDialogCancel>
            <Button
              variant="destructive"
              disabled={busy || !!query.error || mutation.isError}
              onClick={() => mutation.mutate(true)}
            >
              Confirm clear
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
