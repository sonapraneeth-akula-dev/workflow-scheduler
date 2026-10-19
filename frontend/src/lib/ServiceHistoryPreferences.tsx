import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react"
import { z } from "zod"
import type { Session } from "@/lib/auth/session"

export const maximumServiceRunLimit = 30
export const serviceRunLimitSchema = z.int().min(1).max(maximumServiceRunLimit)
const defaultRunLimit = 10
interface PreferenceState {
  runLimit: number
  error: Error | null
}
const Preferences = createContext<
  (PreferenceState & { setRunLimit: (value: number) => boolean }) | null
>(null)

export function profilePreferenceKey(session: Session | null) {
  if (!session) return null
  if (session.isDevelopment)
    return "kardia.profile.v1.development.serviceRunLimit"
  if (!session.tenantId || !session.accountId)
    throw new Error(
      "A signed-in account and tenant are required to save personal settings."
    )
  return `kardia.profile.v1.${encodeURIComponent(session.tenantId)}.${encodeURIComponent(session.accountId)}.serviceRunLimit`
}

function readPreference(session: Session | null): PreferenceState {
  try {
    const key = profilePreferenceKey(session)
    const value = key === null ? null : localStorage.getItem(key)
    return {
      runLimit:
        value === null
          ? defaultRunLimit
          : serviceRunLimitSchema.parse(JSON.parse(value)),
      error: null,
    }
  } catch (cause) {
    return {
      runLimit: defaultRunLimit,
      error: new Error(
        `Your saved run-count preference could not be read. Showing 10 runs until you save a valid setting from 1 to ${maximumServiceRunLimit}.`,
        { cause }
      ),
    }
  }
}

export function ServiceHistoryPreferences({
  session,
  children,
}: {
  session: Session | null
  children: ReactNode
}) {
  const [state, setState] = useState(() => readPreference(session))
  useEffect(() => {
    function changed(event: StorageEvent) {
      if (
        event.storageArea === localStorage &&
        (event.key === null || event.key === profilePreferenceKey(session))
      )
        setState(readPreference(session))
    }
    window.addEventListener("storage", changed)
    return () => window.removeEventListener("storage", changed)
  }, [session])

  function setRunLimit(value: number) {
    const result = serviceRunLimitSchema.safeParse(value)
    if (!result.success) {
      setState((previous) => ({
        ...previous,
        error: new Error(
          `Choose a whole number of runs from 1 to ${maximumServiceRunLimit}.`
        ),
      }))
      return false
    }
    try {
      const key = profilePreferenceKey(session)
      if (key === null)
        throw new Error("Sign in before saving personal settings.")
      localStorage.setItem(key, JSON.stringify(value))
      setState({ runLimit: value, error: null })
      return true
    } catch (cause) {
      setState((previous) => ({
        ...previous,
        error: new Error(
          "Your run-count preference could not be saved. The previous setting is unchanged.",
          { cause }
        ),
      }))
      return false
    }
  }
  return (
    <Preferences.Provider value={{ ...state, setRunLimit }}>
      {children}
    </Preferences.Provider>
  )
}

export function useServiceHistoryPreferences() {
  const value = useContext(Preferences)
  if (!value)
    throw new Error(
      "Personal preferences require a signed-in profile provider."
    )
  return value
}
