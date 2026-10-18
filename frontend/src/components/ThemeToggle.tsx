import { MoonIcon, SunIcon } from "lucide-react"
import { Button } from "@/components/ui/button"

export function ThemeToggle({
  dark,
  onToggle,
}: {
  dark: boolean
  onToggle: () => void
}) {
  const label = dark ? "Light theme" : "Dark theme"
  return (
    <Button
      variant="outline"
      size="icon"
      aria-label={label}
      title={label}
      onClick={onToggle}
    >
      {dark ? (
        <SunIcon aria-hidden="true" data-icon="inline-start" />
      ) : (
        <MoonIcon aria-hidden="true" data-icon="inline-start" />
      )}
    </Button>
  )
}
