import { createRef } from "react"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { describe, expect, it, vi } from "vitest"
import { NativeSelect } from "./native-select"

describe("shared native dropdown", () => {
  it("retains label association, refs, form values and native change events", async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    const ref = createRef<HTMLSelectElement>()
    const { container } = render(
      <form>
        <label htmlFor="kind">Report kind</label>
        <NativeSelect
          id="kind"
          name="kind"
          defaultValue=""
          ref={ref}
          onChange={changed}
        >
          <option value="">All kinds</option>
          <option value="json">JSON</option>
        </NativeSelect>
      </form>
    )
    const select = screen.getByRole("combobox", { name: "Report kind" })
    expect(ref.current).toBe(select)
    await user.selectOptions(select, "json")
    expect(select).toHaveValue("json")
    expect(changed).toHaveBeenCalledOnce()
    expect(new FormData(container.querySelector("form")!).get("kind")).toBe(
      "json"
    )
    expect(
      container.querySelector('[data-slot="native-select-icon"]')
    ).toHaveAttribute("aria-hidden", "true")
  })

  it("preserves disabled fieldsets and never changes their selection", async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    render(
      <fieldset disabled>
        <label>
          Channel
          <NativeSelect defaultValue="Email" onChange={changed}>
            <option>Email</option>
            <option>Teams</option>
          </NativeSelect>
        </label>
      </fieldset>
    )
    const select = screen.getByRole("combobox", { name: "Channel" })
    expect(select).toBeDisabled()
    await user.selectOptions(select, "Teams")
    expect(select).toHaveValue("Email")
    expect(changed).not.toHaveBeenCalled()
  })

  it("keeps caller layout on the wrapper and compact size on the control", () => {
    const { container } = render(
      <NativeSelect aria-label="Scope" className="w-full" size="sm">
        <option>Task</option>
      </NativeSelect>
    )
    expect(
      container.querySelector('[data-slot="native-select-wrapper"]')
    ).toHaveClass("w-full")
    expect(screen.getByRole("combobox")).toHaveAttribute("data-size", "sm")
    expect(screen.getByRole("combobox")).not.toHaveAttribute("size")
  })
})
