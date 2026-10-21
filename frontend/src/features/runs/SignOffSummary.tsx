import { Link } from "@tanstack/react-router"
import { Badge } from "@/components/ui/badge"
import { formatTimestamp } from "@/lib/timestamps"
import type { SignOffState } from "./signoff-api"

const labels = {
  NotSignedOff: "Not signed off",
  SignedOff: "Signed off",
  Revoked: "Revoked",
}

export function signOffDescription(state: SignOffState) {
  const event = state.latest
  return event
    ? `${labels[state.status]} by ${event.actorName} (${event.actorId}), ${formatTimestamp(event.at)}. Reason: ${event.reason}`
    : labels[state.status]
}

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
      title={signOffDescription(state)}
    >
      {labels[state.status]}
    </Badge>
  )
}

export function SignOffSummary({
  state,
  runId,
  compact = false,
}: {
  state: SignOffState
  runId: string
  compact?: boolean
}) {
  const event = state.latest
  return (
    <div
      aria-label="Workflow sign-off summary"
      className="min-w-0 space-y-1 text-xs"
    >
      <Link
        to="/runs/$id"
        params={{ id: runId }}
        hash="signoff"
        title={signOffDescription(state)}
        aria-label={`${signOffDescription(state)}. View sign-off details and history.`}
        className="inline-flex max-w-full flex-wrap items-center gap-x-2 gap-y-1 rounded focus-visible:outline-2 focus-visible:outline-ring"
      >
        <SignOffBadge state={state} />
        {event ? (
          <time dateTime={event.at} title={event.at}>
            {formatTimestamp(event.at)}
          </time>
        ) : null}
      </Link>
      {event ? (
        <p
          className={
            compact
              ? "truncate"
              : "line-clamp-2 max-w-xs [overflow-wrap:anywhere] whitespace-pre-wrap"
          }
          title={`${event.actorName} (${event.actorId}): ${event.reason}`}
        >
          {event.actorName}: {event.reason}
        </p>
      ) : null}
    </div>
  )
}
