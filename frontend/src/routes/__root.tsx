import { useMutation, useQuery, type QueryClient } from "@tanstack/react-query"
import { createRootRouteWithContext, Outlet } from "@tanstack/react-router"
import { AppHeader } from "@/components/AppHeader"
import { AccountMenu } from "@/components/AccountMenu"
import { ThemeToggle } from "@/components/ThemeToggle"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { getSession, signIn, signOut } from "@/lib/auth/session"
import { useThemePreference } from "@/lib/useThemePreference"

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
  const theme = useThemePreference()
  return (
    <>
      <a
        className="sr-only focus:not-sr-only focus:fixed focus:bg-background focus:p-3"
        href="#main"
      >
        Skip to content
      </a>
      <AppHeader>
        <ThemeToggle
          dark={theme.dark}
          onToggle={() => theme.setPreference(theme.dark ? "light" : "dark")}
        />
        {session.data ? (
          <AccountMenu
            session={session.data}
            signingOut={authentication.isPending}
            onSignOut={() => authentication.mutate()}
            preference={theme.preference}
            onThemeChange={theme.setPreference}
            themeError={theme.error}
          />
        ) : (
          <Button
            disabled={session.isPending || authentication.isPending}
            onClick={() => authentication.mutate()}
          >
            Sign in
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
        {theme.error ? <ErrorNotice error={theme.error} /> : null}
        {session.isSuccess && session.data ? <Outlet /> : null}
        {session.isSuccess && !session.data ? (
          <p>Sign in to manage workflow schedules.</p>
        ) : null}
      </main>
    </>
  )
}
