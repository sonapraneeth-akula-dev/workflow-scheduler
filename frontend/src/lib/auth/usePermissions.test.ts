import { createElement, type ReactNode } from "react"
import {
  onlineManager,
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query"
import {
  act,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { Can } from "@/components/ui/Can"
import { getPermissions, type PermissionResource } from "@/lib/api/client"
import { usePermissions } from "./usePermissions"

vi.mock("@/lib/api/client", () => ({ getPermissions: vi.fn() }))
const read = vi.mocked(getPermissions)
const resource: PermissionResource = { scope: "type", id: "team/release" }
const viewer = {
  ...resource,
  viewer: true,
  author: false,
  operator: false,
  admin: false,
}

function setup() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client }, children)
  return { client, wrapper }
}

describe("authoritative caller permissions", () => {
  beforeEach(() => vi.resetAllMocks())

  it.each([
    { author: false, operator: false, admin: false },
    { author: true, operator: false, admin: false },
    { author: false, operator: true, admin: false },
    { author: true, operator: true, admin: true },
  ])("keeps Author and Operator independent for %j", async (roles) => {
    read.mockResolvedValue({ ...viewer, ...roles })
    const { result } = renderHook(() => usePermissions(resource), setup())
    expect(result.current.can("Viewer")).toBe(false)
    await waitFor(() => expect(result.current.isSuccess).toBe(true))
    expect(result.current.can("Viewer")).toBe(true)
    expect(result.current.can("Author")).toBe(roles.author)
    expect(result.current.can("Operator")).toBe(roles.operator)
    expect(result.current.can("Admin")).toBe(roles.admin)
  })

  it("never requests or authorizes an absent resource", () => {
    const { result } = renderHook(() => usePermissions(), setup())
    expect(result.current.can("Operator")).toBe(false)
    expect(read).not.toHaveBeenCalled()
  })

  it("hides Trigger and Abort from a Viewer, then updates after a fresh grant", async () => {
    read.mockResolvedValue(viewer)
    const { client, wrapper } = setup()
    function Actions() {
      const permissions = usePermissions(resource)
      return createElement(Can, {
        permissions,
        role: "Operator",
        children: [
          createElement("button", { key: "trigger" }, "Trigger"),
          createElement("button", { key: "abort" }, "Abort"),
        ],
      })
    }
    render(createElement(Actions), { wrapper })
    await waitFor(() =>
      expect(
        client.getQueryState(["permissions", "type", resource.id])?.status
      ).toBe("success")
    )
    expect(
      screen.queryByRole("button", { name: "Trigger" })
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole("button", { name: "Abort" })
    ).not.toBeInTheDocument()
    read.mockResolvedValue({ ...viewer, operator: true })
    await act(() => client.invalidateQueries({ queryKey: ["permissions"] }))
    expect(
      await screen.findByRole("button", { name: "Trigger" })
    ).toBeInTheDocument()
    expect(screen.getByRole("button", { name: "Abort" })).toBeInTheDocument()
  })

  it("does not reuse a previous resource grant", async () => {
    read
      .mockResolvedValueOnce({ ...viewer, operator: true })
      .mockRejectedValueOnce(new Error("Other team denied."))
    const { result, rerender } = renderHook(
      ({ target }) => usePermissions(target),
      {
        ...setup(),
        initialProps: { target: resource },
      }
    )
    await waitFor(() => expect(result.current.can("Operator")).toBe(true))
    rerender({ target: { scope: "type", id: "other/release" } })
    expect(result.current.can("Operator")).toBe(false)
    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(result.current.error?.message).toBe("Other team denied.")
    expect(result.current.can("Viewer")).toBe(false)
  })

  it("fails closed during refresh and after revoked access, without stale cache authorization", async () => {
    read.mockResolvedValueOnce({ ...viewer, operator: true })
    const { result } = renderHook(() => usePermissions(resource), setup())
    await waitFor(() => expect(result.current.can("Operator")).toBe(true))
    let reject: (reason: Error) => void = () => {
      throw new Error("Refresh not started.")
    }
    read.mockImplementationOnce(
      () =>
        new Promise((_, fail) => {
          reject = fail
        })
    )
    let refresh: ReturnType<typeof result.current.refetch>
    act(() => {
      refresh = result.current.refetch()
    })
    await waitFor(() => expect(result.current.isFetching).toBe(true))
    expect(result.current.can("Operator")).toBe(false)
    await act(async () => {
      reject(new Error("Access revoked."))
      await refresh
    })
    await waitFor(() =>
      expect(result.current.error?.message).toBe("Access revoked.")
    )
    expect(result.current.data?.operator).toBe(true)
    expect(result.current.can("Operator")).toBe(false)
  })

  it("never authorizes a cached grant while its offline refresh is paused", async () => {
    read.mockResolvedValue({ ...viewer, operator: true })
    const { result } = renderHook(() => usePermissions(resource), setup())
    await waitFor(() => expect(result.current.can("Operator")).toBe(true))
    try {
      act(() => {
        onlineManager.setOnline(false)
        void result.current.refetch()
      })
      await waitFor(() => expect(result.current.fetchStatus).toBe("paused"))
      expect(result.current.can("Operator")).toBe(false)
    } finally {
      act(() => onlineManager.setOnline(true))
    }
    await waitFor(() => expect(result.current.can("Operator")).toBe(true))
  })
})
