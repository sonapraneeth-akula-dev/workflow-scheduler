import type { ReactNode } from "react"
import { Link } from "@tanstack/react-router"

export function AppHeader({ children }: { children: ReactNode }) {
  return (
    <header className="border-b">
      <div className="grid min-h-20 w-full grid-cols-2 items-center gap-x-4 gap-y-3 px-5 py-4 sm:px-8 lg:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]">
        <Link
          to="/"
          aria-label="Kardia home"
          className="justify-self-start font-brand text-2xl font-bold tracking-tight"
        >
          Kardia
        </Link>
        <nav
          aria-label="Main navigation"
          className="col-span-2 row-start-2 flex flex-wrap items-center justify-center gap-4 justify-self-center lg:col-span-1 lg:col-start-2 lg:row-start-1"
        >
          <Link to="/runs" className="inline-flex min-h-10 items-center px-1">
            Runs
          </Link>
          <Link
            to="/machines"
            className="inline-flex min-h-10 items-center px-1"
          >
            Machines
          </Link>
          <Link
            to="/admin/audit"
            className="inline-flex min-h-10 items-center px-1"
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
