import { useRef } from "react"
import {
  useIsMutating,
  useMutation,
  useQueryClient,
} from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Can } from "@/components/ui/Can"
import { usePermissions } from "@/lib/auth/usePermissions"
import { TriggerBadge } from "@/features/runs/TriggerBadge"
import { runNow } from "./api"

export function RunNowButton({
  scheduleId,
  seriesId,
  disabled = false,
}: {
  scheduleId: string
  seriesId: string
  disabled?: boolean
}) {
  const client = useQueryClient()
  const permissions = usePermissions({ scope: "series", id: seriesId })
  const key = useRef<string | null>(null)
  const busy =
    useIsMutating({ mutationKey: ["schedule-write", scheduleId] }) > 0
  const mutation = useMutation({
    mutationKey: ["schedule-write", scheduleId],
    retry: false,
    mutationFn: () => {
      if (!permissions.can("Operator"))
        throw new Error(
          "Operator permission is required to trigger this schedule."
        )
      key.current ??= crypto.randomUUID()
      return runNow(scheduleId, key.current)
    },
    onSuccess: () => {
      key.current = null
      void client.invalidateQueries({ queryKey: ["run-history"] })
      void client.invalidateQueries({ queryKey: ["runs"] })
    },
  })
  return (
    <div className="min-w-0 space-y-3">
      <Can permissions={permissions} role="Operator">
        <Button
          disabled={disabled || busy || mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending
            ? "Accepting run..."
            : mutation.isError
              ? "Retry trigger"
              : "Trigger now"}
        </Button>
      </Can>
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {mutation.error ? (
        <>
          <ErrorNotice error={mutation.error} />
          <p className="text-sm text-muted-foreground">
            Retry reuses the same request key, including when the acceptance
            response was lost.
          </p>
        </>
      ) : null}
      {mutation.data ? (
        <div role="status" className="space-y-2">
          <p>
            <TriggerBadge trigger="Manual" /> Run accepted in this series.
          </p>
          <Link
            className="block font-mono break-all"
            to="/runs/$id"
            params={{ id: mutation.data.runId }}
          >
            {mutation.data.runId}
          </Link>
          <p className="text-sm text-muted-foreground">
            The engine creates the run asynchronously. Refresh run history after
            it commits. The schedule, next fire and saved variables are
            unchanged.
          </p>
        </div>
      ) : null}
    </div>
  )
}
