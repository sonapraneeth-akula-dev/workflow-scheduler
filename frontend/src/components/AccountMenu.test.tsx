import { useState } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { getProfileAccess } from "@/lib/auth/profile-access"
import type { ThemePreference } from "@/lib/useThemePreference"
import { AccountMenu } from "./AccountMenu"
import {
  ServiceHistoryPreferences,
  profilePreferenceKey,
} from "@/lib/ServiceHistoryPreferences"

vi.mock("@/lib/auth/profile-access", () => ({ getProfileAccess: vi.fn() }))
const access = vi.mocked(getProfileAccess)
const signOut = vi.fn()
const session = {
  displayName: "Example User",
  isDevelopment: false,
  tenantId: "tenant-a",
  accountId: "account-a",
}
function Harness({ development }: { development: boolean }) {
  const [preference, setPreference] = useState<ThemePreference>("system")
  return (
    <ServiceHistoryPreferences
      session={{ ...session, isDevelopment: development }}
    >
      <AccountMenu
        session={{ ...session, isDevelopment: development }}
        signingOut={false}
        onSignOut={signOut}
        preference={preference}
        onThemeChange={setPreference}
      />
    </ServiceHistoryPreferences>
  )
}
function setup(development = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <Harness development={development} />
    </QueryClientProvider>
  )
  return client
}
async function openMenu() {
  fireEvent.pointerDown(
    screen.getByRole("button", { name: "Account menu for Example User" }),
    { button: 0, ctrlKey: false }
  )
  return screen.findByRole("menu")
}
beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  vi.stubGlobal("PointerEvent", MouseEvent)
  access.mockResolvedValue({
    systemAdmin: false,
    scopes: [{ kind: "Type", id: "demo/release", roles: ["Viewer", "Admin"] }],
  })
})
afterEach(() => vi.unstubAllGlobals())

it("shows initials and keeps sign-out separated from profile and settings", async () => {
  setup()
  expect(
    screen.getByRole("img", { name: "Profile avatar for Example User" })
  ).toHaveTextContent("EU")
  const menu = await openMenu()
  expect(within(menu).getAllByRole("menuitem")).toHaveLength(3)
  expect(within(menu).getByRole("separator")).toBeInTheDocument()
  fireEvent.click(within(menu).getByRole("menuitem", { name: "Sign out" }))
  expect(signOut).toHaveBeenCalledOnce()
})

it("never pretends to sign out the fixed development identity", async () => {
  setup(true)
  const menu = await openMenu()
  expect(
    within(menu).getByRole("menuitem", { name: "Sign out" })
  ).toHaveAttribute("aria-disabled", "true")
  expect(
    within(menu).getByText("Local development has no sign-out session.")
  ).toBeInTheDocument()
  expect(signOut).not.toHaveBeenCalled()
})

it("loads profile permissions only on request and distinguishes type-only administration", async () => {
  setup()
  expect(access).not.toHaveBeenCalled()
  const menu = await openMenu()
  fireEvent.click(
    within(menu).getByRole("menuitem", { name: "Profile details" })
  )
  const dialog = await screen.findByRole("dialog", { name: "Profile details" })
  await waitFor(() =>
    expect(within(dialog).getByText("demo/release")).toBeInTheDocument()
  )
  const roles = dialog.querySelector('[aria-label="Administrator access"]')!
  expect(
    Array.from(roles.querySelectorAll("dd"), (item) => item.textContent)
  ).toEqual(["No", "No", "demo/release"])
})

it("hides previously read role details when a permission refresh fails", async () => {
  setup()
  const menu = await openMenu()
  fireEvent.click(
    within(menu).getByRole("menuitem", { name: "Profile details" })
  )
  const dialog = await screen.findByRole("dialog", { name: "Profile details" })
  await waitFor(() =>
    expect(
      dialog.querySelector('[aria-label="Administrator access"]')
    ).not.toBeNull()
  )
  access.mockRejectedValueOnce(new Error("Permission service unavailable"))
  fireEvent.click(
    within(dialog).getByRole("button", { name: "Refresh profile access" })
  )
  await screen.findByText("Permission service unavailable")
  expect(dialog.querySelector('[aria-label="Administrator access"]')).toBeNull()
})

it("provides actual light, dark and system appearance settings", async () => {
  setup()
  const menu = await openMenu()
  fireEvent.click(within(menu).getByRole("menuitem", { name: "Settings" }))
  const dialog = await screen.findByRole("dialog", {
    name: "Settings",
  })
  fireEvent.click(within(dialog).getByRole("radio", { name: "Dark" }))
  expect(within(dialog).getByRole("radio", { name: "Dark" })).toHaveAttribute(
    "aria-checked",
    "true"
  )
})

it("saves a configurable run count for the personal profile without changing other accounts", async () => {
  setup()
  const menu = await openMenu()
  fireEvent.click(within(menu).getByRole("menuitem", { name: "Settings" }))
  const dialog = await screen.findByRole("dialog", { name: "Settings" })
  const input = within(dialog).getByRole("spinbutton", {
    name: "Recent runs per card",
  })
  expect(input).toHaveValue(10)
  expect(input).toHaveAttribute("max", "30")
  fireEvent.change(input, { target: { value: "25" } })
  fireEvent.click(
    within(dialog).getByRole("button", {
      name: "Save service history settings",
    })
  )
  expect(await within(dialog).findByRole("status")).toHaveTextContent(
    "Personal run-count setting saved."
  )
  expect(localStorage.getItem(profilePreferenceKey(session)!)).toBe("25")
  expect(
    localStorage.getItem(
      profilePreferenceKey({ ...session, accountId: "other" })!
    )
  ).toBeNull()
  const updatedInput = within(dialog).getByRole("spinbutton", {
    name: "Recent runs per card",
  })
  fireEvent.change(updatedInput, { target: { value: "31" } })
  expect(updatedInput).toBeInvalid()
  fireEvent.submit(updatedInput.closest("form")!)
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "Choose a whole number of runs from 1 to 30."
  )
  expect(localStorage.getItem(profilePreferenceKey(session)!)).toBe("25")
  expect(within(dialog).queryByRole("status")).not.toBeInTheDocument()
})
