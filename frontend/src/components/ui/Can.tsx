import type { ReactNode } from "react"
import type { PermissionRole, usePermissions } from "@/lib/auth/usePermissions"

export function Can({
  permissions,
  role,
  children,
}: {
  readonly permissions: Pick<ReturnType<typeof usePermissions>, "can">
  readonly role: PermissionRole
  readonly children: ReactNode
}) {
  return permissions.can(role) ? children : null
}
