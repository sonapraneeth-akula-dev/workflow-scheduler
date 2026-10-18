import { useEffect, useState, useSyncExternalStore } from "react"

export type ThemePreference = "system" | "light" | "dark"
const storageKey = "kardia.theme"
const mediaQuery = "(prefers-color-scheme: dark)"

function subscribe(listener: () => void) {
  const media = matchMedia(mediaQuery)
  media.addEventListener("change", listener)
  return () => media.removeEventListener("change", listener)
}

function readPreference(): {
  preference: ThemePreference
  error: Error | null
} {
  try {
    const value = localStorage.getItem(storageKey)
    if (
      value !== null &&
      value !== "system" &&
      value !== "light" &&
      value !== "dark"
    ) {
      throw new Error("The saved theme preference is invalid.")
    }
    return { preference: value ?? "system", error: null }
  } catch (cause) {
    return {
      preference: "system",
      error: new Error(
        "The saved theme could not be read. Using the system theme until you choose a theme.",
        { cause }
      ),
    }
  }
}

export function useThemePreference() {
  const [state, setState] = useState(readPreference)
  const systemDark = useSyncExternalStore(
    subscribe,
    () => matchMedia(mediaQuery).matches
  )
  const dark =
    state.preference === "dark" || (state.preference === "system" && systemDark)
  useEffect(() => {
    document.documentElement.classList.toggle("dark", dark)
    document.documentElement.style.colorScheme = dark ? "dark" : "light"
  }, [dark])

  function setPreference(preference: ThemePreference) {
    let error: Error | null = null
    try {
      localStorage.setItem(storageKey, preference)
    } catch (cause) {
      error = new Error(
        "Theme changed for this page, but the preference could not be saved.",
        { cause }
      )
    }
    setState({ preference, error })
  }

  return {
    preference: state.preference,
    error: state.error,
    dark,
    setPreference,
  }
}
