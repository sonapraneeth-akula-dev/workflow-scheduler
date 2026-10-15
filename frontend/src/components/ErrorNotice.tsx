import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"

export function ErrorNotice({ error }: { error: unknown }) {
  return (
    <Alert variant="destructive">
      <AlertTitle>Request failed</AlertTitle>
      <AlertDescription>
        {error instanceof Error ? error.message : String(error)}
      </AlertDescription>
    </Alert>
  )
}
