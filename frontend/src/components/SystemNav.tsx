import { Link, useRouterState } from "@tanstack/react-router"
import { DropdownMenu } from "radix-ui"
import { ChevronDownIcon } from "lucide-react"
import { Button } from "@/components/ui/button"

export function SystemNav() {
  const active = useRouterState({
    select: (state) =>
      state.location.pathname === "/admin/system" ||
      state.location.pathname === "/admin/deployments",
  })
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <Button
          variant="ghost"
          aria-label="Live system menu"
          className={`min-h-10 px-2 ${active ? "bg-secondary text-secondary-foreground" : ""}`}
        >
          Live system
          <ChevronDownIcon aria-hidden="true" />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          aria-label="Live system views"
          aria-labelledby={undefined}
          sideOffset={8}
          align="start"
          collisionPadding={12}
          className="z-50 max-w-[var(--radix-dropdown-menu-content-available-width)] border bg-popover p-1 text-popover-foreground shadow-md"
        >
          {(
            [
              ["/admin/system", "Actual system"],
              ["/admin/deployments", "Deployments"],
            ] as const
          ).map(([to, label]) => (
            <DropdownMenu.Item
              key={to}
              asChild
              className="flex min-h-10 cursor-pointer items-center px-3 py-2 text-sm outline-none focus:bg-accent data-highlighted:bg-accent"
            >
              <Link to={to} activeProps={{ "aria-current": "page" }}>
                {label}
              </Link>
            </DropdownMenu.Item>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}
