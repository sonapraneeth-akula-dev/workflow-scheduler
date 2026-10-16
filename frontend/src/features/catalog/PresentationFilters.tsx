import type { Presentation } from "./api"
import { presentationLabel } from "./usePresentation"

export function PresentationFilters({
  config,
}: {
  config: Presentation | null
}) {
  const filters = Object.entries(config?.defaultFilters ?? {})
  if (!filters.length) return null
  return (
    <section
      aria-label="Default metadata filters"
      className="flex flex-col gap-2"
    >
      <h2 className="font-semibold">Team default filters</h2>
      <dl className="flex flex-wrap gap-3">
        {filters.map(([key, value]) => (
          <div key={key} className="flex min-w-0 flex-wrap gap-1">
            <dt className="font-medium break-all">
              {presentationLabel(config, key)}:
            </dt>
            <dd className="break-all">{value || "(empty)"}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}
