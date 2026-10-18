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
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import {
  formatLabels,
  machineMetadataSchema,
  parseLabels,
  patchMachine,
  type Machine,
  type MachineMetadata,
} from "./api"

export function EditLabelsDialog({
  machine,
  canEdit,
}: {
  machine: Machine
  canEdit: boolean
}) {
  const [open, setOpen] = useState(false)
  const [original, setOriginal] = useState<MachineMetadata>(machine)
  const [validation, setValidation] = useState<Error>()
  const client = useQueryClient()
  const mutation = useMutation({
    retry: false,
    mutationFn: (patch: Partial<MachineMetadata>) => {
      if (!canEdit) throw new Error("Global Admin permission is required.")
      return patchMachine(machine.id, patch)
    },
    onSuccess: async () => {
      setOpen(false)
      await client.invalidateQueries({ queryKey: ["machines"] })
    },
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (mutation.isPending) return
    const form = new FormData(event.currentTarget)
    try {
      const next = machineMetadataSchema.parse({
        pool: form.get("pool"),
        labels: parseLabels(String(form.get("labels") ?? "")),
      })
      const patch: Partial<MachineMetadata> = {}
      if (next.pool !== original.pool) patch.pool = next.pool
      if (formatLabels(next.labels) !== formatLabels(original.labels))
        patch.labels = next.labels
      if (Object.keys(patch).length === 0)
        throw new Error("Change labels or pool before saving.")
      setValidation(undefined)
      mutation.mutate(patch)
    } catch (error) {
      setValidation(
        error instanceof Error ? error : new Error("Invalid machine metadata.")
      )
    }
  }
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (mutation.isPending) return
        setOpen(next)
        if (next) {
          setOriginal(machine)
          setValidation(undefined)
          mutation.reset()
        }
      }}
    >
      {canEdit ? (
        <AlertDialogTrigger asChild>
          <Button variant="outline">Edit labels and pool</Button>
        </AlertDialogTrigger>
      ) : null}
      <AlertDialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto">
        <form
          aria-label="Machine metadata"
          onSubmit={submit}
          className="flex min-w-0 flex-col gap-4"
        >
          <AlertDialogHeader>
            <AlertDialogTitle>Edit labels and pool</AlertDialogTitle>
            <AlertDialogDescription>
              Global Admin only. Changes are audited and survive agent
              reconnects. Reservations and running tasks are not interrupted.
              Supplied labels replace the entire label set.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <p className="break-all">{machine.name}</p>
          <label className="flex min-w-0 flex-col gap-1">
            Pool
            <input
              name="pool"
              defaultValue={original.pool}
              required
              maxLength={200}
              readOnly={mutation.isPending}
              className="min-w-0 rounded border bg-background p-2"
            />
          </label>
          <label className="flex min-w-0 flex-col gap-1">
            Labels (one key=value per line)
            <textarea
              name="labels"
              defaultValue={formatLabels(original.labels)}
              rows={6}
              readOnly={mutation.isPending}
              className="min-w-0 rounded border bg-background p-2"
            />
          </label>
          <p>
            At most 100 labels. Keys: 1-100 characters; values: 1-200. Empty
            labels clears all labels.
          </p>
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
              {mutation.isPending ? "Saving metadata..." : "Save metadata"}
            </Button>
          </AlertDialogFooter>
        </form>
      </AlertDialogContent>
    </AlertDialog>
  )
}
