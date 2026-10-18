import { useEffect, useState } from "react"
import { useMutation, useQuery, type QueryClient } from "@tanstack/react-query"
import { createRootRouteWithContext, Outlet } from "@tanstack/react-router"
import { AppHeader } from "@/components/AppHeader"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { getSession, signIn, signOut } from "@/lib/auth/session"

export const Route = createRootRouteWithContext<{ queryClient: QueryClient }>()(
  {
    component: RootLayout,
    errorComponent: ({ error }) => <ErrorNotice error={error} />,
  }
)

function RootLayout() {
  const { queryClient } = Route.useRouteContext()
  const session = useQuery({ queryKey: ["session"], queryFn: getSession })
  const authentication = useMutation({
    mutationFn: async () => {
      if (session.data) {
        await signOut()
      } else {
        await signIn()
      }
    },
    onSuccess: async () => {
      await queryClient.cancelQueries()
      queryClient.removeQueries({
        predicate: (query) => query.queryKey[0] !== "session",
      })
      await queryClient.resetQueries({ queryKey: ["session"] })
    },
  })
  const [theme, setTheme] = useState<"system" | "light" | "dark">("system")
  useEffect(() => {
    const media = matchMedia("(prefers-color-scheme: dark)")
    const apply = () => {
      document.documentElement.classList.toggle(
        "dark",
        theme === "dark" || (theme === "system" && media.matches)
      )
    }
    apply()
    media.addEventListener("change", apply)
    return () => media.removeEventListener("change", apply)
  }, [theme])
  return (
    <>
      <a
        className="sr-only focus:not-sr-only focus:fixed focus:bg-background focus:p-3"
        href="#main"
      >
        Skip to content
      </a>
      <AppHeader>
        <Button
          variant="outline"
          onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
        >
          {theme === "dark" ? "Light theme" : "Dark theme"}
        </Button>
        {session.data ? <span>{session.data.displayName}</span> : null}
        {session.data?.isDevelopment ? (
          <span>Development authentication</span>
        ) : (
          <Button
            disabled={session.isPending || authentication.isPending}
            onClick={() => authentication.mutate()}
          >
            {session.data ? "Sign out" : "Sign in"}
          </Button>
        )}
      </AppHeader>
      <main
        id="main"
        tabIndex={-1}
        className="mx-auto flex max-w-5xl flex-col gap-6 p-5"
      >
        {session.isPending ? <p role="status">Loading session...</p> : null}
        {session.error ? <ErrorNotice error={session.error} /> : null}
        {authentication.error ? (
          <ErrorNotice error={authentication.error} />
        ) : null}
        {session.isSuccess && session.data ? <Outlet /> : null}
        {session.isSuccess && !session.data ? (
          <p>Sign in to manage workflow schedules.</p>
        ) : null}
      </main>
    </>
  )
}
