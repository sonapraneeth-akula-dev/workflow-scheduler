import { useId, useRef, useState, type FormEvent } from "react"
import {
  useInfiniteQuery,
  useMutation,
  useQueryClient,
  type InfiniteData,
} from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { ApiError } from "@/lib/api/client"
import { usePermissions } from "@/lib/auth/usePermissions"
import {
  channelSchema,
  createSubscription,
  deleteSubscription,
  eventSchema,
  getSubscriptions,
  type SubscriptionPage,
  type SubscriptionScope,
} from "./api"

type Props = SubscriptionScope &
  ({ scope: "schedule"; seriesId: string } | { scope: "workflow" | "type" })

export function SubscribeMenu(props: Props) {
  return (
    <ScopedSubscriptions key={`${props.scope}:${props.scopeId}`} {...props} />
  )
}

function ScopedSubscriptions(props: Props) {
  const { scope, scopeId } = props
  const resource = { scope, scopeId }
  const typeScope =
    scope === "workflow" ? scopeId.split("/").slice(0, 2).join("/") : scopeId
  const authority =
    props.scope === "schedule"
      ? { scope: "series" as const, id: props.seriesId }
      : { scope: "type" as const, id: typeScope }
  const permissions = usePermissions(authority)
  const [open, setOpen] = useState(false)
  const [channel, setChannel] = useState<"Email" | "Teams" | "Webhook">("Email")
  const [event, setEvent] = useState<"Completed" | "Failed">("Completed")
  const [target, setTarget] = useState("")
  const [secretRef, setSecretRef] = useState("")
  const [error, setError] = useState<Error>()
  const pending = useRef(false)
  const panel = useId()
  const client = useQueryClient()
  const key = ["subscriptions", scope, scopeId]
  const stableViewer =
    permissions.isSuccess &&
    permissions.data.viewer &&
    permissions.data.scope === authority.scope &&
    permissions.data.id === authority.id
  const query = useInfiniteQuery({
    queryKey: key,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      getSubscriptions(resource, pageParam, signal),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: open && stableViewer,
    retry: false,
    staleTime: 0,
  })
  const rows = query.data?.pages.flatMap((page) => page.items)
  const duplicate =
    rows && new Set(rows.map((row) => row.id)).size !== rows.length
  const fresh =
    permissions.can("Viewer") &&
    query.isSuccess &&
    !query.isFetching &&
    !duplicate
  const save = useMutation({
    retry: false,
    mutationFn: () => {
      if (!fresh)
        throw new Error(
          "Current Viewer access and a fresh subscription list are required."
        )
      return createSubscription({
        ...resource,
        events: [event],
        channel,
        target,
        secretRef: channel === "Webhook" ? secretRef : null,
      })
    },
    onSuccess: () => client.invalidateQueries({ queryKey: key, exact: true }),
    onError: async (failure) => {
      if (failure instanceof ApiError && failure.status === 403)
        await client.invalidateQueries({ queryKey: ["permissions"] })
    },
    onSettled: () => {
      pending.current = false
    },
  })
  const remove = useMutation({
    retry: false,
    mutationFn: deleteSubscription,
    onSuccess: async (_, id) => {
      client.setQueryData<InfiniteData<SubscriptionPage>>(key, (previous) =>
        previous
          ? {
              ...previous,
              pages: previous.pages.map((page) => ({
                ...page,
                items: page.items.filter((row) => row.id !== id),
              })),
            }
          : previous
      )
      if (save.data?.id === id) save.reset()
      await client.invalidateQueries({ queryKey: key, exact: true })
    },
    onSettled: () => {
      pending.current = false
    },
  })
  const busy = save.isPending || remove.isPending
  const known = [
    ...new Map(
      [...(rows ?? []), ...(save.data ? [save.data] : [])].map((row) => [
        row.id,
        row,
      ])
    ).values(),
  ]
  function submit(form: FormEvent<HTMLFormElement>) {
    form.preventDefault()
    if (pending.current) return
    if (!fresh) {
      setError(
        new Error(
          "Current Viewer access and a fresh subscription list are required."
        )
      )
      return
    }
    setError(undefined)
    pending.current = true
    save.mutate()
  }
  function unsubscribe(id: string) {
    if (pending.current) return
    pending.current = true
    remove.mutate(id)
  }
  async function refresh() {
    const checked = await permissions.refetch()
    if (
      checked.isSuccess &&
      checked.data.viewer &&
      checked.data.scope === authority.scope &&
      checked.data.id === authority.id
    )
      await query.refetch()
  }
  return (
    <div className="min-w-0 space-y-3">
      <Button
        variant="outline"
        aria-expanded={open}
        aria-controls={panel}
        onClick={() => setOpen(!open)}
      >
        Subscribe
      </Button>
      {open ? (
        <section
          id={panel}
          aria-label="Notification subscriptions"
          className="min-w-0 space-y-4 rounded-md border p-4"
        >
          <h2 className="text-lg font-semibold">
            Notifications for this {scope}
          </h2>
          <p className="text-sm break-all text-muted-foreground">{scopeId}</p>
          <p className="text-sm">
            Your subscriptions only. Completed includes success, failure,
            timeout and abort. Delivery requires an operator-approved
            destination and enabled dispatcher; saving does not send a test
            message.
          </p>
          <Button
            variant="outline"
            disabled={busy || query.isFetching || permissions.isFetching}
            onClick={() => void refresh()}
          >
            Refresh subscriptions
          </Button>
          {permissions.isFetching || query.isFetching ? (
            <p role="status">Checking access and loading subscriptions...</p>
          ) : null}
          {!permissions.isFetching && !stableViewer ? (
            <p role="status">
              Current Viewer permission is required to view or create
              subscriptions.
            </p>
          ) : null}
          {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
          {query.error ? <ErrorNotice error={query.error} /> : null}
          {duplicate ? (
            <ErrorNotice
              error={
                new Error(
                  "The API repeated a subscription across pages. Refresh before making changes."
                )
              }
            />
          ) : null}
          {error ? <ErrorNotice error={error} /> : null}
          {save.error ? <ErrorNotice error={save.error} /> : null}
          {remove.error ? <ErrorNotice error={remove.error} /> : null}
          {save.isSuccess ? (
            <p role="status">
              Subscription saved. Delivery depends on operator configuration.
            </p>
          ) : null}
          {remove.isSuccess ? (
            <p role="status">
              Unsubscribed. Pending notifications for this subscription were
              removed.
            </p>
          ) : null}
          {fresh ? (
            <>
              <form
                aria-label="Create notification subscription"
                onSubmit={submit}
                className="min-w-0 space-y-3"
              >
                <fieldset disabled={busy} className="min-w-0 space-y-3">
                  <legend className="font-medium">Add a subscription</legend>
                  <label className="flex min-w-0 flex-col gap-1">
                    Channel
                    <select
                      className="max-w-full rounded border bg-background p-2"
                      value={channel}
                      onChange={(change) => {
                        setChannel(
                          channelSchema.parse(change.currentTarget.value)
                        )
                        setTarget("")
                        setSecretRef("")
                        save.reset()
                      }}
                    >
                      <option>Email</option>
                      <option>Teams</option>
                      <option>Webhook</option>
                    </select>
                  </label>
                  <label className="flex min-w-0 flex-col gap-1">
                    Events
                    <select
                      className="max-w-full rounded border bg-background p-2"
                      value={event}
                      onChange={(change) =>
                        setEvent(eventSchema.parse(change.currentTarget.value))
                      }
                    >
                      <option value="Completed">All terminal outcomes</option>
                      <option value="Failed">
                        Failures, timeouts and aborts only
                      </option>
                    </select>
                  </label>
                  <label className="flex min-w-0 flex-col gap-1">
                    {channel === "Email"
                      ? "Email address"
                      : channel === "Teams"
                        ? "Teams destination reference"
                        : "Webhook URL"}
                    <Input
                      type={
                        channel === "Email"
                          ? "email"
                          : channel === "Webhook"
                            ? "url"
                            : "text"
                      }
                      required
                      maxLength={2048}
                      value={target}
                      onChange={(change) =>
                        setTarget(change.currentTarget.value)
                      }
                    />
                  </label>
                  {channel === "Webhook" ? (
                    <label className="flex min-w-0 flex-col gap-1">
                      Signing secret reference
                      <Input
                        required
                        maxLength={273}
                        value={secretRef}
                        onChange={(change) =>
                          setSecretRef(change.currentTarget.value)
                        }
                      />
                    </label>
                  ) : null}
                  {channel !== "Email" ? (
                    <p className="text-sm text-muted-foreground">
                      Use configured secret names such as
                      secret://notifications/
                      {channel === "Teams" ? "teams" : "signing"}, never
                      resolved credentials.
                      {channel === "Teams"
                        ? " Do not paste the private Teams workflow URL."
                        : " Webhooks require HTTPS; loopback HTTP is allowed for local testing."}
                    </p>
                  ) : null}
                  <Button type="submit" disabled={busy}>
                    {save.isPending
                      ? "Saving subscription..."
                      : "Save subscription"}
                  </Button>
                </fieldset>
              </form>
              <h3 className="font-medium">Your subscriptions</h3>
              {rows?.length === 0 ? (
                <p role="status">No subscriptions for this {scope}.</p>
              ) : null}
              <ul aria-label="Your subscriptions" className="min-w-0 space-y-3">
                {rows?.map((row) => (
                  <li
                    key={row.id}
                    data-subscription-id={row.id}
                    className="min-w-0 space-y-2 rounded border p-3"
                  >
                    <p>
                      {row.channel} -{" "}
                      {row.events.includes("Completed")
                        ? "All terminal outcomes"
                        : "Failures, timeouts and aborts only"}
                    </p>
                    <p className="break-all">{row.target}</p>
                    {row.secretRef ? (
                      <p className="break-all">
                        Signing reference: {row.secretRef}
                      </p>
                    ) : null}
                    <Button
                      variant="outline"
                      disabled={busy}
                      onClick={() => unsubscribe(row.id)}
                    >
                      Unsubscribe {row.channel}
                    </Button>
                  </li>
                ))}
              </ul>
              {query.hasNextPage ? (
                <Button
                  variant="outline"
                  disabled={busy || query.isFetching}
                  onClick={() => void query.fetchNextPage()}
                >
                  Load more subscriptions
                </Button>
              ) : null}
            </>
          ) : known.length > 0 &&
            !query.isFetching &&
            !permissions.isFetching ? (
            <div className="min-w-0 space-y-3">
              <p>
                Destinations hidden until access is confirmed. You can still
                unsubscribe from your previously loaded subscriptions.
              </p>
              {known.map((row) => (
                <div
                  key={row.id}
                  data-subscription-id={row.id}
                  className="min-w-0 space-y-2"
                >
                  <p className="break-all">
                    {row.channel} subscription {row.id}
                  </p>
                  <Button
                    variant="outline"
                    disabled={busy}
                    onClick={() => unsubscribe(row.id)}
                  >
                    Unsubscribe {row.channel}
                  </Button>
                </div>
              ))}
            </div>
          ) : null}
        </section>
      ) : null}
    </div>
  )
}
