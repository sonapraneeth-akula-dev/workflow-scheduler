import type { AnchorHTMLAttributes } from "react"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, it, vi } from "vitest"
import { SystemNav } from "./SystemNav"

vi.mock("@tanstack/react-router", () => ({
  useRouterState: () => true,
  Link: ({
    children,
    to,
    activeProps,
    ...props
  }: AnchorHTMLAttributes<HTMLAnchorElement> & {
    to: string
    activeProps: object
  }) => (
    <a {...props} {...activeProps} href={to}>
      {children}
    </a>
  ),
}))
it("opens Live system views by keyboard and exposes both destinations", async () => {
  const user = userEvent.setup()
  render(<SystemNav />)
  const trigger = screen.getByRole("button", { name: "Live system menu" })
  expect(trigger).toHaveClass("bg-secondary")
  trigger.focus()
  await user.keyboard("{Enter}")
  expect(
    await screen.findByRole("menu", { name: "Live system views" })
  ).toBeVisible()
  expect(
    screen.getByRole("menuitem", { name: "Actual system" })
  ).toHaveAttribute("href", "/admin/system")
  expect(screen.getByRole("menuitem", { name: "Deployments" })).toHaveAttribute(
    "href",
    "/admin/deployments"
  )
  await user.keyboard("{Escape}")
  expect(screen.queryByRole("menu")).toBeNull()
  expect(trigger).toHaveFocus()
})
