import type { ReactNode } from "react"
import { Link } from "@tanstack/react-router"

export function AppHeader({ children }: { children: ReactNode }) {
  return (
    <header className="border-b">
      <div className="flex min-h-20 w-full flex-wrap items-center justify-between gap-4 px-5 py-4 sm:px-8">
        <Link
          to="/"
          aria-label="Kardia home"
          className="font-brand text-2xl font-bold tracking-tight"
        >
          Kardia
        </Link>
        <nav aria-label="Main navigation" className="flex flex-wrap gap-4">
          <Link to="/runs">Runs</Link>
          <Link to="/machines">Machines</Link>
          <Link to="/admin/audit">Audit trail</Link>
        </nav>
        <div className="flex flex-wrap items-center gap-3">{children}</div>
      </div>
    </header>
  )
}
