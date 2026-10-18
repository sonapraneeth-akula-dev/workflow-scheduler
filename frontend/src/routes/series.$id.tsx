import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"
import { trendWindowSchema } from "@/features/series/api"
import { TrendsPanel } from "@/features/series/TrendsPanel"

export const Route = createFileRoute("/series/$id")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  validateSearch: historySearchSchema.extend({
    view: z.literal("trends").optional(),
    trendWindow: trendWindowSchema.optional(),
  }),
  component: SeriesHistory,
})

function SeriesHistory() {
  const { id } = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <>
      <h1 className="text-2xl font-semibold">Series history</h1>
      <p className="text-sm break-all text-muted-foreground">Series: {id}</p>
      <Link
        to="/series/$id/compare"
        params={{ id }}
        className="text-primary underline"
      >
        Compare reports
      </Link>
      <nav aria-label="Series views" className="flex flex-wrap gap-4">
        <Link
          to="/series/$id"
          params={{ id }}
          search={{ ...search, view: undefined }}
          aria-current={search.view !== "trends" ? "page" : undefined}
          className="text-primary underline"
        >
          Run history
        </Link>
        <Link
          to="/series/$id"
          params={{ id }}
          search={{ ...search, view: "trends" }}
          aria-current={search.view === "trends" ? "page" : undefined}
          className="text-primary underline"
        >
          Trends
        </Link>
      </nav>
      {search.view === "trends" ? (
        <TrendsPanel
          seriesId={id}
          window={search.trendWindow}
          onWindowChange={(trendWindow) =>
            void navigate({ search: { ...search, trendWindow } })
          }
        />
      ) : (
        <>
          <HistoryFilters
            key={JSON.stringify(search)}
            search={search}
            onChange={(next) =>
              void navigate({
                search: { ...next, trendWindow: search.trendWindow },
              })
            }
          />
          <HistoryTable scope={{ seriesId: id }} search={search} />
        </>
      )}
    </>
  )
}
