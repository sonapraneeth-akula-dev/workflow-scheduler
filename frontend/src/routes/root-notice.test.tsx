import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  createMemoryHistory,
  createRoute,
  createRouter,
  RouterProvider,
} from "@tanstack/react-router"
import { render, screen } from "@testing-library/react"
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { Route } from "./__root"
import { getSession } from "@/lib/auth/session"

vi.mock("@/lib/auth/session", () => ({
  getSession: vi.fn(),
  signIn: vi.fn(),
  signOut: vi.fn(),
}))
vi.mock("@/features/notices/SystemNoticeBar", () => ({
  SystemNoticeBar: () => <aside>Global notice marker</aside>,
}))
const clients: QueryClient[] = []
function show() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  clients.push(queryClient)
  const index = createRoute({
    getParentRoute: () => Route,
    path: "/",
    component: () => <p>Signed-in page</p>,
  })
  const router = createRouter({
    routeTree: Route.addChildren([index]),
    context: { queryClient },
    history: createMemoryHistory({ initialEntries: ["/"] }),
  })
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
beforeEach(() => {
  vi.stubGlobal("matchMedia", () => ({
    matches: false,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }))
  vi.stubGlobal("scrollTo", vi.fn())
})
afterEach(() => {
  clients.splice(0).forEach((client) => client.clear())
  vi.unstubAllGlobals()
})

it("places the signed-in notice before navigation, outside page-specific content", async () => {
  vi.mocked(getSession).mockResolvedValue({
    displayName: "Admin",
    isDevelopment: true,
  })
  show()
  await screen.findByText("Signed-in page")
  const notice = screen.getByText("Global notice marker")
  const header = screen.getByRole("banner")
  expect(
    notice.compareDocumentPosition(header) & Node.DOCUMENT_POSITION_FOLLOWING
  ).toBeTruthy()
  expect(screen.getByRole("main")).not.toContainElement(notice)
})
it("does not mount or fetch the notice before sign-in", async () => {
  vi.mocked(getSession).mockResolvedValue(null)
  show()
  await screen.findByText("Sign in to manage workflow schedules.")
  expect(screen.queryByText("Global notice marker")).toBeNull()
})
