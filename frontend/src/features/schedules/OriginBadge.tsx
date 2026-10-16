import { Badge } from "@/components/ui/badge"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { Schedule } from "@/lib/api/client"

function commitLink(
  commit: string,
  template: string | undefined
): string | Error | undefined {
  if (!/^[0-9a-f]{40}([0-9a-f]{24})?$/.test(commit)) {
    return new Error("The schedule has an invalid Git commit.")
  }
  if (!template) return undefined
  if (template.split("{commit}").length !== 2) {
    return new Error(
      "VITE_COMMIT_URL_TEMPLATE must contain exactly one {commit} placeholder."
    )
  }
  const value = template.replace("{commit}", commit)
  if (!URL.canParse(value))
    return new Error("The configured commit browser URL is invalid.")
  const url = new URL(value)
  if (url.protocol !== "https:" || url.username || url.password) {
    return new Error(
      "Commit browser links require HTTPS without embedded credentials."
    )
  }
  return url.href
}

export function OriginBadge({
  schedule,
}: {
  schedule: Pick<Schedule, "origin" | "sourcePath" | "gitCommit">
}) {
  if (schedule.origin === "User")
    return <Badge variant="secondary">UI schedule</Badge>
  const link = schedule.gitCommit
    ? commitLink(schedule.gitCommit, import.meta.env.VITE_COMMIT_URL_TEMPLATE)
    : undefined
  return (
    <section
      aria-label="Schedule origin"
      className="flex min-w-0 flex-col items-start gap-2"
    >
      <Badge variant="secondary">Code schedule</Badge>
      {schedule.sourcePath ? (
        <p className="min-w-0 break-all">
          Source: <code>{schedule.sourcePath}</code>
        </p>
      ) : (
        <p>Source provenance unavailable for this legacy schedule.</p>
      )}
      {schedule.gitCommit ? (
        link instanceof Error ? (
          <ErrorNotice error={link} />
        ) : link ? (
          <a
            href={link}
            target="_blank"
            rel="noopener noreferrer"
            className="break-all underline"
            aria-label={`View schedule commit ${schedule.gitCommit} (opens in a new tab)`}
          >
            Commit: <code>{schedule.gitCommit}</code>
          </a>
        ) : (
          <p className="break-all">
            Commit: <code>{schedule.gitCommit}</code>. Commit link unavailable:
            configure VITE_COMMIT_URL_TEMPLATE for the workflow repository.
          </p>
        )
      ) : (
        <p>Commit unavailable.</p>
      )}
    </section>
  )
}
