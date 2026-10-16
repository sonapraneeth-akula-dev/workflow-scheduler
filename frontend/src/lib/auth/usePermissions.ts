import { useQuery } from "@tanstack/react-query"
import { getPermissions, type PermissionResource } from "@/lib/api/client"

export type PermissionRole = "Viewer" | "Author" | "Operator" | "Admin"

export function usePermissions(resource?: PermissionResource) {
  const query = useQuery({
    queryKey: ["permissions", resource?.scope, resource?.id],
    enabled: resource !== undefined,
    queryFn: ({ signal }) => {
      if (!resource) {
        throw new Error("A resource is required to read caller permissions.")
      }
      return getPermissions(resource, signal)
    },
    retry: false,
    staleTime: 0,
    refetchInterval: 30_000,
  })
  function can(role: PermissionRole): boolean {
    if (!resource || !query.isSuccess || query.fetchStatus !== "idle") {
      return false
    }
    const permissions = query.data
    return (
      permissions.scope === resource.scope &&
      permissions.id === resource.id &&
      permissions[
        role === "Viewer"
          ? "viewer"
          : role === "Author"
            ? "author"
            : role === "Operator"
              ? "operator"
              : "admin"
      ]
    )
  }
  return { ...query, can }
}
