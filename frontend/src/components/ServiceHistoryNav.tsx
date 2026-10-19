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

const itemStyle =
  "flex min-h-9 cursor-pointer items-center justify-between gap-4 px-3 py-2 text-sm outline-none focus:bg-accent data-highlighted:bg-accent data-state-open:bg-accent data-disabled:cursor-not-allowed data-disabled:opacity-50"
const contentStyle =
  "z-50 max-h-[70vh] min-w-52 max-w-[calc(100vw-2rem)] overflow-auto border bg-popover p-1 text-popover-foreground shadow-md"

function TeamServiceLinks({
  team,
  presentation,
}: {
  team: string
  presentation: Presentation | null
}) {
  const query = useQuery({
    queryKey: ["catalog", "team", team, "filter-types"],
    queryFn: ({ signal }) => getTypes(team, signal),
  })
  return (
    <>
      <DropdownMenu.Item asChild className={itemStyle}>
        <Link to="/t/$team/services" params={{ team }}>
          All workflow types
        </Link>
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
        ? query.data?.map((type) => (
            <DropdownMenu.Item key={type.name} asChild className={itemStyle}>
              <Link
                to="/t/$team/services"
                params={{ team }}
                search={{ type: type.name }}
              >
                {presentationName(
                  presentation,
                  type.name,
                  `${team}/${type.name}`
                )}
              </Link>
            </DropdownMenu.Item>
          ))
        : null}
      {query.data?.length === 0 ? (
        <DropdownMenu.Item disabled className={itemStyle}>
          No workflow types
        </DropdownMenu.Item>
      ) : null}
    </>
  )
}

export function ServiceHistoryNav() {
  const [open, setOpen] = useState(false)
  const pathname = useRouterState({
    select: (state) => state.location.pathname,
  })
  const active =
    pathname === "/service-history" || pathname.endsWith("/services")
  const query = useQuery({
    queryKey: ["catalog", "teams"],
    queryFn: ({ signal }) => getTeams(signal),
    enabled: open,
  })
  return (
    <DropdownMenu.Root open={open} onOpenChange={setOpen}>
      <DropdownMenu.Trigger asChild>
        <Button
          variant="ghost"
          aria-label="Service history menu"
          className={`min-h-10 px-2 ${active ? "bg-secondary text-secondary-foreground" : ""}`}
        >
          Service history
          <ChevronDownIcon aria-hidden="true" />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          aria-label="Service history teams"
          aria-labelledby={undefined}
          sideOffset={8}
          align="start"
          collisionPadding={12}
          className={contentStyle}
        >
          <DropdownMenu.Item asChild className={itemStyle}>
            <Link to="/service-history">All teams</Link>
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
                      <TeamServiceLinks
                        team={team.team}
                        presentation={team.presentation}
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
