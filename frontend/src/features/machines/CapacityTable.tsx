import { useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { parseJson } from "@/lib/json"
import {
  manualCapacitySchema,
  patchMachine,
  type Machine,
  type ManualCapacity,
} from "./api"

export function CapacityTable({
  machine,
  canEdit,
}: {
  machine: Machine
  canEdit: boolean
}) {
  const [draft, setDraft] = useState<ManualCapacity>()
  const [validation, setValidation] = useState<Error>()
  const client = useQueryClient()
  const mutation = useMutation({
    retry: false,
    mutationFn: (manualCapacity: ManualCapacity) => {
      if (!canEdit) throw new Error("Global Admin permission is required.")
      return patchMachine(machine.id, { manualCapacity })
    },
    onSuccess: async () => {
      setDraft(undefined)
      await client.invalidateQueries({ queryKey: ["machines"] })
    },
  })
  function edit(value: ManualCapacity) {
    setValidation(undefined)
    mutation.reset()
    setDraft(value)
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (mutation.isPending) return
    const form = new FormData(event.currentTarget)
    try {
      const value = manualCapacitySchema.parse({
        profile: {
          transport: form.get("transport"),
          codec: form.get("codec"),
          payloadSizeClass: form.get("payloadSizeClass"),
          loadSettings: parseJson(String(form.get("loadSettings") ?? "")),
        },
        capacityRps: Number(form.get("capacityRps")),
      })
      setValidation(undefined)
      mutation.mutate(value)
    } catch (error) {
      setValidation(
        error instanceof Error ? error : new Error("Invalid capacity profile.")
      )
    }
  }
  return (
    <section aria-label="Profile capacities" className="min-w-0 space-y-3">
      <h3 className="font-semibold">Profile capacities</h3>
      <p>
        Capacity applies only to the exact transport, codec, payload class and
        load settings. Manual values override calibration for that profile.
      </p>
      {canEdit ? (
        <Button
          variant="outline"
          onClick={() =>
            edit({
              profile: {
                transport: "",
                codec: "",
                payloadSizeClass: "",
                loadSettings: {},
              },
              capacityRps: 1,
            })
          }
        >
          Set manual capacity
        </Button>
      ) : null}
      {machine.capacities.length === 0 ? (
        <p>Unverified: no capacity has been set for any profile.</p>
      ) : (
        <div
          role="region"
          aria-label="Capacity table"
          tabIndex={0}
          className="max-w-full overflow-x-auto"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">Capacity by exact profile</caption>
            <thead>
              <tr>
                {[
                  "Transport",
                  "Codec",
                  "Payload class",
                  "Load settings",
                  "Requests/s",
                  "Source",
                  "Verification",
                  ...(canEdit ? ["Actions"] : []),
                ].map((title) => (
                  <th key={title} scope="col" className="p-2">
                    {title}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {machine.capacities.map((capacity, index) => (
                <tr key={index} className="border-t align-top">
                  <td className="p-2 break-all">
                    {capacity.profile.transport}
                  </td>
                  <td className="p-2 break-all">{capacity.profile.codec}</td>
                  <td className="p-2 break-all">
                    {capacity.profile.payloadSizeClass}
                  </td>
                  <td className="max-w-64 p-2 break-all whitespace-pre-wrap">
                    {JSON.stringify(capacity.profile.loadSettings, null, 2)}
                  </td>
                  <td className="p-2">{capacity.capacityRps}</td>
                  <td className="p-2">{capacity.source}</td>
                  <td className="p-2">
                    {capacity.calibratedAt ? (
                      <time dateTime={capacity.calibratedAt}>
                        {capacity.calibratedAt}
                      </time>
                    ) : (
                      "Unverified (manual; not calibrated)"
                    )}
                  </td>
                  {canEdit ? (
                    <td className="p-2">
                      <Button variant="outline" onClick={() => edit(capacity)}>
                        Override profile {index + 1}
                      </Button>
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <AlertDialog
        open={draft !== undefined}
        onOpenChange={(open) => {
          if (!open && !mutation.isPending) setDraft(undefined)
        }}
      >
        <AlertDialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto">
          {draft ? (
            <form
              aria-label="Manual capacity"
              onSubmit={submit}
              className="flex min-w-0 flex-col gap-4"
            >
              <AlertDialogHeader>
                <AlertDialogTitle>Set manual capacity</AlertDialogTitle>
                <AlertDialogDescription>
                  Global Admin only. Audited changes replace any calibrated
                  value for this exact profile, without changing other profiles
                  or interrupting reservations.
                </AlertDialogDescription>
              </AlertDialogHeader>
              <p className="break-all">{machine.name}</p>
              {[
                ["transport", "Transport"],
                ["codec", "Codec"],
                ["payloadSizeClass", "Payload class"],
              ].map(([name, label]) => (
                <label key={name} className="flex min-w-0 flex-col gap-1">
                  {label}
                  <input
                    name={name}
                    defaultValue={
                      name === "transport"
                        ? draft.profile.transport
                        : name === "codec"
                          ? draft.profile.codec
                          : draft.profile.payloadSizeClass
                    }
                    required
                    maxLength={100}
                    readOnly={mutation.isPending}
                    className="min-w-0 rounded border bg-background p-2"
                  />
                </label>
              ))}
              <label className="flex min-w-0 flex-col gap-1">
                Load settings (JSON object of strings)
                <textarea
                  name="loadSettings"
                  defaultValue={JSON.stringify(
                    draft.profile.loadSettings,
                    null,
                    2
                  )}
                  rows={4}
                  required
                  readOnly={mutation.isPending}
                  className="min-w-0 rounded border bg-background p-2"
                />
              </label>
              <p>
                Profile identifiers: 1-100 characters, no surrounding whitespace
                or controls. Server validates the 2,000-byte canonical profile
                limit.
              </p>
              <label className="flex min-w-0 flex-col gap-1">
                Capacity (requests/s)
                <input
                  name="capacityRps"
                  type="number"
                  min={1}
                  max={2147483647}
                  step={1}
                  defaultValue={draft.capacityRps}
                  required
                  readOnly={mutation.isPending}
                  className="min-w-0 rounded border bg-background p-2"
                />
              </label>
              {validation ? <ErrorNotice error={validation} /> : null}
              {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
              {!canEdit ? (
                <p role="status">
                  Global Admin permission checking or unavailable.
                </p>
              ) : null}
              <AlertDialogFooter>
                <AlertDialogCancel disabled={mutation.isPending}>
                  Cancel
                </AlertDialogCancel>
                <Button type="submit" disabled={mutation.isPending || !canEdit}>
                  {mutation.isPending ? "Saving capacity..." : "Save capacity"}
                </Button>
              </AlertDialogFooter>
            </form>
          ) : null}
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
