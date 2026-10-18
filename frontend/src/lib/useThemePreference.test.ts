import { act, renderHook } from "@testing-library/react"
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { useThemePreference } from "./useThemePreference"

const listeners = new Set<() => void>()
const media = {
  matches: false,
  addEventListener: (_: string, listener: () => void) =>
    listeners.add(listener),
  removeEventListener: (_: string, listener: () => void) =>
    listeners.delete(listener),
}
beforeEach(() => {
  media.matches = false
  listeners.clear()
  localStorage.clear()
  vi.stubGlobal("matchMedia", () => media)
})
afterEach(() => {
  vi.unstubAllGlobals()
  document.documentElement.classList.remove("dark")
  document.documentElement.style.colorScheme = ""
})

it("tracks the actual system theme until an explicit preference is selected", () => {
  const { result } = renderHook(useThemePreference)
  expect(result.current.dark).toBe(false)
  act(() => {
    media.matches = true
    listeners.forEach((listener) => listener())
  })
  expect(result.current.dark).toBe(true)
  act(() => result.current.setPreference("light"))
  expect(result.current.dark).toBe(false)
  expect(localStorage.getItem("kardia.theme")).toBe("light")
  expect(document.documentElement.style.colorScheme).toBe("light")
})

it("restores a saved dark preference", () => {
  localStorage.setItem("kardia.theme", "dark")
  const { result } = renderHook(useThemePreference)
  expect(result.current.dark).toBe(true)
  expect(document.documentElement).toHaveClass("dark")
})

it("surfaces invalid saved preferences instead of silently accepting them", () => {
  localStorage.setItem("kardia.theme", "not-a-theme")
  const { result } = renderHook(useThemePreference)
  expect(result.current.preference).toBe("system")
  expect(result.current.error?.message).toContain("could not be read")
})

it("applies the theme but exposes a failed persistence write", () => {
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
    throw new Error("Storage denied")
  })
  const { result } = renderHook(useThemePreference)
  act(() => result.current.setPreference("dark"))
  expect(result.current.dark).toBe(true)
  expect(result.current.error?.message).toContain("could not be saved")
})
