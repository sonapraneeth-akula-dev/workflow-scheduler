import type { ReactNode } from "react"
import { Link } from "@tanstack/react-router"
import { WorkflowNav } from "./WorkflowNav"
import { SystemNav } from "./SystemNav"

export function AppHeader({ children }: { children: ReactNode }) {
  return (
    <header className="border-t-2 border-b border-t-primary bg-card">
      <div className="grid min-h-20 w-full grid-cols-2 items-center gap-x-4 gap-y-3 px-5 py-4 sm:px-8 lg:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]">
        <Link
          to="/"
          aria-label="Kardia home"
          className="justify-self-start font-brand text-2xl font-bold tracking-tight text-primary"
        >
          Kardia
        </Link>
        <nav
          aria-label="Main navigation"
          className="col-span-2 row-start-2 flex flex-wrap items-center justify-center gap-4 justify-self-center lg:col-span-1 lg:col-start-2 lg:row-start-1"
        >
          <WorkflowNav view="service-history" />
          <WorkflowNav view="recurring-jobs" />
          <Link
            to="/runs"
            className="inline-flex min-h-10 items-center px-2"
            activeProps={{
              className: "bg-secondary text-secondary-foreground",
            }}
          >
            Runs
          </Link>
          <Link
            to="/machines"
            className="inline-flex min-h-10 items-center px-2"
            activeProps={{
              className: "bg-secondary text-secondary-foreground",
            }}
          >
            Machines
          </Link>
          <SystemNav />
          <Link
            to="/admin/audit"
            className="inline-flex min-h-10 items-center px-2"
            activeProps={{
              className: "bg-secondary text-secondary-foreground",
            }}
          >
            Audit trail
          </Link>
        </nav>
        <div className="col-start-2 row-start-1 flex min-w-0 flex-wrap items-center justify-end gap-3 justify-self-end lg:col-start-3">
          {children}
        </div>
      </div>
    </header>
  )
}
