import { useState } from "react"
import {
  useIsMutating,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query"
import { createFileRoute, Link } from "@tanstack/react-router"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Skeleton } from "@/components/ui/skeleton"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { ErrorNotice } from "@/components/ErrorNotice"
import { ScheduleForm } from "@/features/schedules/ScheduleForm"
import { OriginBadge } from "@/features/schedules/OriginBadge"
import { formatFire } from "@/features/schedules/schedule-draft"
import {
  deleteSchedule,
  getSchedule,
  setSchedulePaused,
  type ScheduleSnapshot,
} from "@/lib/api/client"

export const Route = createFileRoute("/schedules/$id")({
  component: ScheduleDetail,
})

function ScheduleDetail() {
  const { id } = Route.useParams()
  const queryClient = useQueryClient()
  const [formRevision, setFormRevision] = useState(0)
  const key = ["schedule", id]
  const query = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => getSchedule(id, signal),
    staleTime: Infinity,
  })
  const update = (snapshot: ScheduleSnapshot) =>
    queryClient.setQueryData(key, snapshot)
  const pause = useMutation({
    mutationKey: ["schedule-write", id],
    mutationFn: setSchedulePaused,
    onSuccess: update,
  })
  const remove = useMutation({
    mutationKey: ["schedule-write", id],
    mutationFn: deleteSchedule,
  })
  const isMutating = useIsMutating({ mutationKey: ["schedule-write", id] }) > 0
  if (remove.isSuccess) {
    return (
      <>
        <h1 className="text-2xl font-semibold">Schedule deleted</h1>
        <p role="status">
          Future fires stopped. Historical runs and series remain unchanged.
        </p>
        <Link to="/schedules/new">Create another schedule</Link>
      </>
    )
  }
  if (query.isPending) {
    return (
      <div role="status" aria-label="Loading schedule">
        <Skeleton className="h-20 w-full" />
      </div>
    )
  }
  if (query.error) {
    return (
      <>
        <h1>Schedule unavailable</h1>
        <ErrorNotice error={query.error} />
      </>
    )
  }
  const current = query.data
  const schedule = current.schedule
  const canMutate = schedule.origin === "User"
  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold">{schedule.name}</h1>
        <Badge variant="secondary">
          {schedule.paused ? "Paused" : "Active"}
        </Badge>
      </div>
      <p>{schedule.workflowId}</p>
      <OriginBadge schedule={schedule} />
      <Link to="/series/$id" params={{ id: schedule.seriesId }}>
        View series history
      </Link>
      <p>
        Next fire:{" "}
        {schedule.nextFireAt
          ? formatFire(schedule.nextFireAt, schedule.timezone)
          : "Not scheduled"}
      </p>
      <div className="flex flex-wrap gap-3">
        <Button
          variant="outline"
          disabled={isMutating || query.isFetching}
          onClick={async () => {
            pause.reset()
            remove.reset()
            const refreshed = await query.refetch()
            if (refreshed.isSuccess) {
              setFormRevision((revision) => revision + 1)
            }
          }}
        >
          Reload latest schedule
        </Button>
        {canMutate ? (
          <>
            <Button
              variant="outline"
              disabled={isMutating}
              onClick={() => pause.mutate(current)}
            >
              {schedule.paused ? "Resume schedule" : "Pause schedule"}
            </Button>
            <AlertDialog>
              <AlertDialogTrigger asChild>
                <Button variant="destructive" disabled={isMutating}>
                  Delete schedule
                </Button>
              </AlertDialogTrigger>
              <AlertDialogContent>
                <AlertDialogHeader>
                  <AlertDialogTitle>Delete this schedule?</AlertDialogTitle>
                  <AlertDialogDescription>
                    Future fires stop. Existing runs and the permanent series
                    are kept. This action cannot be undone in the UI.
                  </AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                  <AlertDialogCancel>Keep schedule</AlertDialogCancel>
                  <AlertDialogAction
                    variant="destructive"
                    onClick={() => remove.mutate(current)}
                  >
                    Confirm delete
                  </AlertDialogAction>
                </AlertDialogFooter>
              </AlertDialogContent>
            </AlertDialog>
          </>
        ) : null}
      </div>
      <p className="text-sm text-muted-foreground">
        Reloading discards unsaved edits. Pause and resume preserve them.
      </p>
      {pause.error ? <ErrorNotice error={pause.error} /> : null}
      {remove.error ? <ErrorNotice error={remove.error} /> : null}
      {canMutate ? (
        <ScheduleForm
          key={formRevision}
          current={current}
          onSaved={(snapshot) => {
            update(snapshot)
            setFormRevision((revision) => revision + 1)
          }}
        />
      ) : (
        <>
          <p>
            Code-defined schedules are read-only. Change and register their
            source file.
          </p>
          <dl className="grid grid-cols-[auto_1fr] gap-3">
            <dt>Timezone</dt>
            <dd>{schedule.timezone}</dd>
            <dt>Expression</dt>
            <dd>{schedule.cron ?? schedule.interval}</dd>
            <dt>Version pin</dt>
            <dd>{schedule.versionPin ?? "Active version"}</dd>
          </dl>
        </>
      )}
    </>
  )
}
