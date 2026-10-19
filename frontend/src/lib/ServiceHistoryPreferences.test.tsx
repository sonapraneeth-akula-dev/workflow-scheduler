import { act, renderHook } from "@testing-library/react"
import type { ReactNode } from "react"
import { afterEach, beforeEach, expect, it, vi } from "vitest"
import type { Session } from "@/lib/auth/session"
import {
  profilePreferenceKey,
  ServiceHistoryPreferences,
  useServiceHistoryPreferences,
} from "./ServiceHistoryPreferences"

const account: Session = {
  displayName: "Example User",
  isDevelopment: false,
  tenantId: "tenant-a",
  accountId: "account-a",
}
function show(session: Session = account) {
  return renderHook(useServiceHistoryPreferences, {
    wrapper: ({ children }: { children: ReactNode }) => (
      <ServiceHistoryPreferences session={session}>
        {children}
      </ServiceHistoryPreferences>
    ),
  })
}
beforeEach(() => localStorage.clear())
afterEach(() => vi.restoreAllMocks())

it("defaults to 10 and persists the personal setting across remounts", () => {
  const first = show()
  expect(first.result.current.runLimit).toBe(10)
  act(() => {
    expect(first.result.current.setRunLimit(25)).toBe(true)
  })
  expect(first.result.current.runLimit).toBe(25)
  first.unmount()
  expect(show().result.current.runLimit).toBe(25)
})

it("isolates accounts, tenants and the explicit development profile", () => {
  const first = show()
  act(() => {
    first.result.current.setRunLimit(30)
  })
  first.unmount()
  expect(
    show({ ...account, accountId: "account-b" }).result.current.runLimit
  ).toBe(10)
  expect(
    show({ ...account, tenantId: "tenant-b" }).result.current.runLimit
  ).toBe(10)
  expect(
    show({ displayName: "Local developer", isDevelopment: true }).result.current
      .runLimit
  ).toBe(10)
  expect(show().result.current.runLimit).toBe(30)
})

it.each([0, 101, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
  "rejects invalid count %s without modifying the existing preference",
  (count) => {
    const { result } = show()
    act(() => {
      result.current.setRunLimit(20)
    })
    act(() => {
      expect(result.current.setRunLimit(count)).toBe(false)
    })
    expect(result.current.runLimit).toBe(20)
    expect(result.current.error?.message).toMatch(/whole number.*1 to 100/)
    expect(localStorage.getItem(profilePreferenceKey(account)!)).toBe("20")
  }
)

it.each(["not-json", "null", '"25"', "0", "101"])(
  "surfaces malformed saved value %s, then allows explicit recovery",
  (value) => {
    localStorage.setItem(profilePreferenceKey(account)!, value)
    const { result } = show()
    expect(result.current.runLimit).toBe(10)
    expect(result.current.error?.message).toMatch(/could not be read/)
    act(() => {
      result.current.setRunLimit(15)
    })
    expect(result.current.runLimit).toBe(15)
    expect(result.current.error).toBeNull()
  }
)

it("surfaces unavailable browser storage without falsely reporting a saved preference", () => {
  const { result } = show()
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
    throw new Error("Storage disabled")
  })
  act(() => {
    expect(result.current.setRunLimit(25)).toBe(false)
  })
  expect(result.current.runLimit).toBe(10)
  expect(result.current.error?.message).toMatch(/could not be saved/)
})

it("synchronizes only the current profile's storage changes", () => {
  const { result } = show()
  const key = profilePreferenceKey(account)!
  localStorage.setItem(key, "35")
  act(() => {
    window.dispatchEvent(
      new StorageEvent("storage", { key, storageArea: localStorage })
    )
  })
  expect(result.current.runLimit).toBe(35)
  act(() => {
    window.dispatchEvent(
      new StorageEvent("storage", {
        key: "other-profile",
        storageArea: localStorage,
      })
    )
  })
  expect(result.current.runLimit).toBe(35)
  localStorage.removeItem(key)
  act(() => {
    window.dispatchEvent(
      new StorageEvent("storage", { key: null, storageArea: localStorage })
    )
  })
  expect(result.current.runLimit).toBe(10)
})
