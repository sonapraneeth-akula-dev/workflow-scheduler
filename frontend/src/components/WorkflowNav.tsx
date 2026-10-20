import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Link, useRouterState } from "@tanstack/react-router"
import { DropdownMenu } from "radix-ui"
import { ChevronDownIcon, ChevronRightIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import {
  accessDenied,
  getTeams,
  getTypes,
  type Presentation,
} from "@/features/catalog/api"
import { presentationName } from "@/features/catalog/usePresentation"

type WorkflowView = "service-history" | "recurring-jobs"

const itemStyle =
  "flex min-h-9 cursor-pointer items-center justify-between gap-4 px-3 py-2 text-sm outline-none focus:bg-accent data-highlighted:bg-accent data-state-open:bg-accent data-disabled:cursor-not-allowed data-disabled:opacity-50"
const contentStyle =
  "z-50 max-h-[70vh] min-w-[min(13rem,var(--radix-dropdown-menu-content-available-width))] max-w-[var(--radix-dropdown-menu-content-available-width)] overflow-auto border bg-popover p-1 text-popover-foreground shadow-md"

function TeamWorkflowLinks({
  team,
  presentation,
  view,
}: {
  team: string
  presentation: Presentation | null
  view: WorkflowView
}) {
  const query = useQuery({
    queryKey: ["catalog", "team", team, "filter-types"],
    queryFn: ({ signal }) => getTypes(team, signal),
  })
  return (
    <>
      <DropdownMenu.Item asChild className={itemStyle}>
        {view === "recurring-jobs" ? (
          <Link to="/recurring-jobs" search={{ team }}>
            All workflow types
          </Link>
        ) : (
          <Link to="/t/$team/services" params={{ team }}>
            All workflow types
          </Link>
        )}
      </DropdownMenu.Item>
      <DropdownMenu.Separator className="my-1 h-px bg-border" />
      {query.isPending ? (
        <DropdownMenu.Item disabled className={itemStyle}>
          Loading types...
        </DropdownMenu.Item>
      ) : null}
      {query.error ? (
        <div className="space-y-2 p-2">
          <ErrorNotice error={query.error} />
          <Button
            variant="outline"
            disabled={query.isFetching}
            onClick={() => void query.refetch()}
          >
            Retry types
          </Button>
        </div>
      ) : null}
      {!accessDenied(query.error)
        ? query.data?.map((type) => {
            const name = presentationName(
              presentation,
              type.name,
              `${team}/${type.name}`
            )
            return (
              <DropdownMenu.Item key={type.name} asChild className={itemStyle}>
                {view === "recurring-jobs" ? (
                  <Link to="/recurring-jobs" search={{ team, type: type.name }}>
                    {name}
                  </Link>
                ) : (
                  <Link
                    to="/t/$team/services"
                    params={{ team }}
                    search={{ type: type.name }}
                  >
                    {name}
                  </Link>
                )}
              </DropdownMenu.Item>
            )
          })
        : null}
      {query.data?.length === 0 ? (
        <DropdownMenu.Item disabled className={itemStyle}>
          No workflow types
        </DropdownMenu.Item>
      ) : null}
    </>
  )
}

export function WorkflowNav({ view }: { view: WorkflowView }) {
  const [open, setOpen] = useState(false)
  const pathname = useRouterState({
    select: (state) => state.location.pathname,
  })
  const label = view === "recurring-jobs" ? "Recurring jobs" : "Service history"
  const active =
    view === "recurring-jobs"
      ? pathname === "/recurring-jobs"
      : pathname === "/service-history" || pathname.endsWith("/services")
  const query = useQuery({
    queryKey: ["catalog", "teams"],
    queryFn: ({ signal }) => getTeams(signal),
    enabled: open,
  })
  return (
    <DropdownMenu.Root open={open} onOpenChange={setOpen} modal={false}>
      <DropdownMenu.Trigger asChild>
        <Button
          variant="ghost"
          aria-label={`${label} menu`}
          className={`min-h-10 px-2 ${active ? "bg-secondary text-secondary-foreground" : ""}`}
        >
          {label}
          <ChevronDownIcon aria-hidden="true" />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          aria-label={`${label} teams`}
          aria-labelledby={undefined}
          sideOffset={8}
          align="start"
          collisionPadding={12}
          className={contentStyle}
        >
          <DropdownMenu.Item asChild className={itemStyle}>
            {view === "recurring-jobs" ? (
              <Link to="/recurring-jobs">All teams</Link>
            ) : (
              <Link to="/service-history">All teams</Link>
            )}
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-border" />
          {query.isPending ? (
            <DropdownMenu.Item disabled className={itemStyle}>
              Loading teams...
            </DropdownMenu.Item>
          ) : null}
          {query.error ? (
            <div className="space-y-2 p-2">
              <ErrorNotice error={query.error} />
              <Button
                variant="outline"
                disabled={query.isFetching}
                onClick={() => void query.refetch()}
              >
                Retry teams
              </Button>
            </div>
          ) : null}
          {!accessDenied(query.error)
            ? query.data?.map((team) => (
                <DropdownMenu.Sub key={team.team}>
                  <DropdownMenu.SubTrigger
                    className={itemStyle}
                    data-team={team.team}
                  >
                    {presentationName(team.presentation, team.team)}
                    <ChevronRightIcon aria-hidden="true" className="size-4" />
                  </DropdownMenu.SubTrigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.SubContent
                      aria-label={`${team.team} workflow types`}
                      aria-labelledby={undefined}
                      sideOffset={4}
                      collisionPadding={12}
                      className={contentStyle}
                    >
                      <TeamWorkflowLinks
                        team={team.team}
                        presentation={team.presentation}
                        view={view}
                      />
                    </DropdownMenu.SubContent>
                  </DropdownMenu.Portal>
                </DropdownMenu.Sub>
              ))
            : null}
          {query.data?.length === 0 ? (
            <DropdownMenu.Item disabled className={itemStyle}>
              No teams available
            </DropdownMenu.Item>
          ) : null}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}
