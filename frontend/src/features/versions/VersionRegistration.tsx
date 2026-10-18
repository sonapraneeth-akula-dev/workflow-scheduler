import type { VersionRegistration as Registration } from "./api"
import { formatTimestamp } from "@/lib/timestamps"

export function VersionRegistration({
  registration,
}: {
  registration: Registration
}) {
  return (
    <dl className="grid min-w-0 gap-2 break-words sm:grid-cols-[max-content_minmax(0,1fr)]">
      <dt className="font-medium">Git author</dt>
      <dd>{registration.gitAuthor}</dd>
      <dt className="font-medium">Registered by</dt>
      <dd>{registration.registeredBy}</dd>
      <dt className="font-medium">Commit</dt>
      <dd className="font-mono break-all">{registration.gitCommit}</dd>
      <dt className="font-medium">Change note</dt>
      <dd className="whitespace-pre-wrap">
        {registration.changeNote ?? "No change note."}
      </dd>
      <dt className="font-medium">SDK version</dt>
      <dd>{registration.sdkVersion}</dd>
      <dt className="font-medium">Registered (local)</dt>
      <dd>
        <time
          dateTime={registration.registeredAt}
          title={registration.registeredAt}
        >
          {formatTimestamp(registration.registeredAt)}
        </time>
      </dd>
    </dl>
  )
}
