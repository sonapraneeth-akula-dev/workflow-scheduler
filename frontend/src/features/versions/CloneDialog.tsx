import { useId, useState, type FormEvent } from "react"
import { useMutation } from "@tanstack/react-query"
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
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { usePermissions } from "@/lib/auth/usePermissions"
import { cloneRequestSchema, cloneWorkflow, type CloneRequest } from "./api"

export function CloneDialog({
  workflowId,
  versionId,
}: {
  workflowId: string
  versionId: string
}) {
  const [open, setOpen] = useState(false)
  const [newName, setNewName] = useState("")
  const [targetType, setTargetType] = useState(
    workflowId.split("/").slice(0, 2).join("/")
  )
  const [validation, setValidation] = useState(false)
  const id = useId()
  const source = usePermissions({
    scope: "type",
    id: workflowId.split("/").slice(0, 2).join("/"),
  })
  const parsed = cloneRequestSchema.safeParse({ newName, targetType })
  const target = usePermissions(
    open && parsed.success ? { scope: "type", id: targetType } : undefined
  )
  const permitted = source.can("Viewer") && target.can("Author")
  const mutation = useMutation({
    retry: false,
    mutationFn: (request: CloneRequest) => {
      if (!permitted) {
        throw new Error(
          "Viewer(source) and Author(target type) permissions are required."
        )
      }
      return cloneWorkflow(workflowId, versionId, request)
    },
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob)
      const link = document.createElement("a")
      link.href = url
      link.download = "workflow-clone.zip"
      document.body.append(link)
      link.click()
      link.remove()
      setTimeout(() => URL.revokeObjectURL(url), 0)
    },
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (mutation.isPending || mutation.isSuccess) {
      return
    }
    setValidation(!parsed.success)
    if (parsed.success) {
      mutation.mutate(parsed.data)
    }
  }
  return (
    <AlertDialog
      open={open}
      onOpenChange={(value) => {
        if (!mutation.isPending) {
          setOpen(value)
          if (value) {
            mutation.reset()
            setValidation(false)
          }
        }
      }}
    >
      {source.can("Viewer") ? (
        <AlertDialogTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            aria-label={`Clone version ${versionId}`}
          >
            Clone version
          </Button>
        </AlertDialogTrigger>
      ) : null}
      <AlertDialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto">
        <form
          onSubmit={submit}
          aria-label="Clone immutable workflow version"
          className="flex min-w-0 flex-col gap-4"
        >
          <AlertDialogHeader>
            <AlertDialogTitle>Clone workflow version</AlertDialogTitle>
            <AlertDialogDescription>
              Download a scaffold, not a deployment. The original stays
              unchanged. Review downloaded source before running tests or
              publishing.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <p className="break-all">
            Source: {workflowId}@{versionId}
          </p>
          <FieldGroup>
            <Field data-invalid={validation && !parsed.success}>
              <FieldLabel htmlFor={`${id}-name`}>New workflow name</FieldLabel>
              <Input
                id={`${id}-name`}
                value={newName}
                maxLength={256}
                required
                readOnly={mutation.isPending || mutation.isSuccess}
                aria-invalid={validation && !parsed.success}
                onChange={(event) => {
                  setNewName(event.target.value)
                  setValidation(false)
                }}
              />
            </Field>
            <Field data-invalid={validation && !parsed.success}>
              <FieldLabel htmlFor={`${id}-type`}>Target team/type</FieldLabel>
              <Input
                id={`${id}-type`}
                value={targetType}
                maxLength={513}
                required
                readOnly={mutation.isPending || mutation.isSuccess}
                aria-invalid={validation && !parsed.success}
                onChange={(event) => {
                  setTargetType(event.target.value)
                  setValidation(false)
                }}
              />
            </Field>
          </FieldGroup>
          {validation ? (
            <p role="alert">
              Supply a workflow leaf name and an existing team/type, without dot
              segments, slashes or control characters in names.
            </p>
          ) : null}
          {open && parsed.success && !permitted ? (
            <p role="status">
              Viewer(source) and Author(target type) permissions are required.
              Checking or unavailable.
            </p>
          ) : null}
          {source.error ? <ErrorNotice error={source.error} /> : null}
          {target.error ? <ErrorNotice error={target.error} /> : null}
          {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
          {mutation.isSuccess ? (
            <p role="status">
              Scaffold downloaded. Version 1.0.0, empty schedules; no source was
              executed or registered.
            </p>
          ) : null}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={mutation.isPending}>
              Close
            </AlertDialogCancel>
            <Button
              type="submit"
              disabled={
                !parsed.success ||
                !permitted ||
                mutation.isPending ||
                mutation.isSuccess
              }
            >
              {mutation.isPending
                ? "Downloading scaffold..."
                : "Download scaffold"}
            </Button>
          </AlertDialogFooter>
        </form>
      </AlertDialogContent>
    </AlertDialog>
  )
}
