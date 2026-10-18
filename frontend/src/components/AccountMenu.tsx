import { useRef, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Avatar, Dialog, DropdownMenu } from "radix-ui"
import {
  ChevronDownIcon,
  LogOutIcon,
  SettingsIcon,
  UserRoundIcon,
  XIcon,
} from "lucide-react"
import { cn } from "cn"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { ErrorNotice } from "@/components/ErrorNotice"
import { getProfileAccess } from "@/lib/auth/profile-access"
import type { Session } from "@/lib/auth/session"
import type { ThemePreference } from "@/lib/useThemePreference"

const menuItem =
  "flex cursor-default items-center gap-2 px-3 py-2 text-sm outline-none focus:bg-accent focus:text-accent-foreground data-disabled:pointer-events-none data-disabled:opacity-50 [&_svg]:size-4"

function ProfileDetails({ session }: { session: Session }) {
  const query = useQuery({
    queryKey: [
      "profile-access",
      session.accountId ?? session.displayName,
      session.isDevelopment,
    ],
    queryFn: ({ signal }) => getProfileAccess(signal),
    retry: false,
    gcTime: 0,
    staleTime: 0,
    refetchInterval: 30_000,
  })
  const access = query.isSuccess && !query.isFetching ? query.data : undefined
  const teams = access?.scopes.filter(
    (scope) => scope.kind === "Team" && scope.roles.includes("Admin")
  )
  const types = access?.scopes.filter(
    (scope) => scope.kind === "Type" && scope.roles.includes("Admin")
  )
  return (
    <div className="flex min-w-0 flex-col gap-4">
      <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 text-sm">
        <dt>Name</dt>
        <dd className="break-words">{session.displayName}</dd>
        {session.username ? (
          <>
            <dt>Account</dt>
            <dd className="break-all">{session.username}</dd>
          </>
        ) : null}
        {session.accountId ? (
          <>
            <dt>Account ID</dt>
            <dd className="font-mono break-all">{session.accountId}</dd>
          </>
        ) : null}
        {session.tenantId ? (
          <>
            <dt>Tenant ID</dt>
            <dd className="font-mono break-all">{session.tenantId}</dd>
          </>
        ) : null}
        <dt>Authentication</dt>
        <dd>
          {session.isDevelopment ? "Local development" : "Microsoft Entra"}
        </dd>
      </dl>
      <p className="text-sm text-muted-foreground">
        No profile photo is supplied by this sign-in. The avatar shows initials.
      </p>
      {query.isFetching ? (
        <p role="status">Checking current access...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      <Button
        variant="outline"
        disabled={query.isFetching}
        onClick={() => void query.refetch()}
      >
        Refresh profile access
      </Button>
      {access ? (
        <>
          <dl
            aria-label="Administrator access"
            className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 text-sm"
          >
            <dt>System administrator</dt>
            <dd>{access.systemAdmin ? "Yes" : "No"}</dd>
            <dt>Team administrator</dt>
            <dd>
              {access.systemAdmin
                ? "All teams (system administrator)"
                : teams?.map((scope) => scope.id).join(", ") || "No"}
            </dd>
            <dt>Type administrator</dt>
            <dd>
              {access.systemAdmin
                ? "All types (system administrator)"
                : types?.map((scope) => scope.id).join(", ") || "No"}
            </dd>
          </dl>
          <h3 className="font-semibold">Effective access</h3>
          {access.systemAdmin ? (
            <p>
              System administrator access includes all team and type
              permissions.
            </p>
          ) : access.scopes.length ? (
            <ul className="flex flex-col gap-3">
              {access.scopes.map((scope) => (
                <li
                  key={`${scope.kind}:${scope.id}`}
                  className="flex flex-wrap items-center gap-2"
                >
                  <span className="break-all">
                    {scope.kind}: {scope.id}
                  </span>
                  {scope.roles.map((role) => (
                    <Badge key={role} variant="secondary">
                      {role}
                    </Badge>
                  ))}
                </li>
              ))}
            </ul>
          ) : (
            <p>No team or type access is currently granted.</p>
          )}
          <p className="text-sm text-muted-foreground">
            Server-verified effective permissions, including inherited grants.
            Checked again every 30 seconds while this profile is open.
          </p>
        </>
      ) : null}
      {session.isDevelopment ? (
        <p className="text-sm text-muted-foreground">
          Development authentication is a fixed local identity, not an
          interactive session. Sign out is unavailable in this mode.
        </p>
      ) : null}
    </div>
  )
}

function AccountSettings({
  preference,
  onThemeChange,
  themeError,
}: {
  preference: ThemePreference
  onThemeChange: (value: ThemePreference) => void
  themeError?: Error | null
}) {
  return (
    <div className="flex flex-col gap-3">
      <h3 id="appearance-heading" className="font-semibold">
        Appearance
      </h3>
      <ToggleGroup
        type="single"
        variant="outline"
        value={preference}
        aria-labelledby="appearance-heading"
        onValueChange={(value) => {
          if (value === "system" || value === "light" || value === "dark")
            onThemeChange(value)
        }}
      >
        <ToggleGroupItem value="system">System</ToggleGroupItem>
        <ToggleGroupItem value="light">Light</ToggleGroupItem>
        <ToggleGroupItem value="dark">Dark</ToggleGroupItem>
      </ToggleGroup>
      <p className="text-sm text-muted-foreground">
        Appearance is saved in this browser. System follows your operating
        system preference.
      </p>
      {themeError ? <ErrorNotice error={themeError} /> : null}
    </div>
  )
}

export function AccountMenu({
  session,
  signingOut,
  onSignOut,
  preference,
  onThemeChange,
  themeError,
}: {
  session: Session
  signingOut: boolean
  onSignOut: () => void
  preference: ThemePreference
  onThemeChange: (value: ThemePreference) => void
  themeError?: Error | null
}) {
  const [panel, setPanel] = useState<"profile" | "settings" | null>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const initials =
    session.displayName
      .trim()
      .split(/\s+/)
      .slice(0, 2)
      .map((part) => part[0])
      .join("")
      .toUpperCase() || "?"
  return (
    <>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <Button
            ref={trigger}
            variant="ghost"
            className="h-10 gap-2 px-1"
            aria-label={`Account menu for ${session.displayName}`}
          >
            <span className="hidden max-w-48 truncate sm:block">
              {session.displayName}
            </span>
            <Avatar.Root
              role="img"
              aria-label={`Profile avatar for ${session.displayName}`}
              className="inline-flex size-8 shrink-0 items-center justify-center overflow-hidden border bg-primary/10 text-primary"
            >
              <Avatar.Fallback className="text-xs font-semibold">
                {initials}
              </Avatar.Fallback>
            </Avatar.Root>
            <ChevronDownIcon aria-hidden="true" data-icon="inline-end" />
          </Button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            align="end"
            sideOffset={8}
            collisionPadding={16}
            className="max-w-[calc(100vw-2rem)] min-w-64 border bg-popover p-1 text-popover-foreground shadow-lg"
            onCloseAutoFocus={(event) => {
              if (panel !== null) event.preventDefault()
            }}
          >
            <DropdownMenu.Label className="flex flex-col gap-1 px-3 py-2">
              <span className="max-w-72 truncate font-semibold">
                {session.displayName}
              </span>
              <span className="text-xs text-muted-foreground">
                {session.isDevelopment
                  ? "Development authentication"
                  : "Microsoft Entra account"}
              </span>
            </DropdownMenu.Label>
            <DropdownMenu.Group>
              <DropdownMenu.Item
                className={menuItem}
                onSelect={() => setPanel("profile")}
              >
                <UserRoundIcon aria-hidden="true" />
                Profile details
              </DropdownMenu.Item>
              <DropdownMenu.Item
                className={menuItem}
                onSelect={() => setPanel("settings")}
              >
                <SettingsIcon aria-hidden="true" />
                Settings
              </DropdownMenu.Item>
            </DropdownMenu.Group>
            <DropdownMenu.Separator asChild>
              <Separator className="my-1" decorative={false} />
            </DropdownMenu.Separator>
            <DropdownMenu.Group>
              <DropdownMenu.Item
                disabled={session.isDevelopment || signingOut}
                onSelect={onSignOut}
                className={cn(
                  menuItem,
                  "text-destructive focus:bg-destructive/10 focus:text-destructive"
                )}
              >
                <LogOutIcon aria-hidden="true" />
                {signingOut ? "Signing out..." : "Sign out"}
              </DropdownMenu.Item>
            </DropdownMenu.Group>
            {session.isDevelopment ? (
              <DropdownMenu.Label className="max-w-72 px-3 py-2 text-xs text-muted-foreground">
                Local development has no sign-out session.
              </DropdownMenu.Label>
            ) : null}
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
      <Dialog.Root
        open={panel !== null}
        onOpenChange={(open) => {
          if (!open) setPanel(null)
        }}
      >
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 bg-foreground/30" />
          <Dialog.Content
            className="fixed top-1/2 left-1/2 flex max-h-[85vh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 flex-col gap-4 overflow-y-auto border bg-background p-5 text-foreground shadow-lg"
            onCloseAutoFocus={(event) => {
              event.preventDefault()
              trigger.current?.focus()
            }}
          >
            <div className="flex items-center justify-between gap-3">
              <Dialog.Title className="text-xl font-semibold">
                {panel === "profile" ? "Profile details" : "Settings"}
              </Dialog.Title>
              <Dialog.Close asChild>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label="Close account dialog"
                >
                  <XIcon aria-hidden="true" data-icon="inline-start" />
                </Button>
              </Dialog.Close>
            </div>
            <Dialog.Description className="text-sm text-muted-foreground">
              {panel === "profile"
                ? "Your identity and current server-verified access."
                : "Preferences for this browser."}
            </Dialog.Description>
            {panel === "profile" ? (
              <ProfileDetails session={session} />
            ) : (
              <AccountSettings
                preference={preference}
                onThemeChange={onThemeChange}
                themeError={themeError}
              />
            )}
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  )
}
