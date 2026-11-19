import { CopyLogs } from '../../ui/ErrorNote'
import { useEffect, useState, type Dispatch, type ReactNode, type SetStateAction } from 'react'
import { host } from '../../../lib/host'
import { report } from '../../../lib/report'
import { changeAccountSettings, type AccountSettings, type DiscordClientSettings } from '../../../lib/discordPrefs'
import { Row, SwitchRow } from '../../settings/parts'
import { errorText } from '../../../lib/errorWords'

/** A switch on one of the account's settings; invert for Discord's "restricted" and "opted out" fields shown as allows. */
export function AccountToggle({ label, settings, field, invert = false, disabled }: {
  label: string
  settings: AccountSettings
  field: { [K in keyof AccountSettings]: AccountSettings[K] extends boolean ? K : never }[keyof AccountSettings]
  invert?: boolean
  disabled?: boolean
}) {
  return (
    <SwitchRow label={label} checked={settings[field] !== invert} disabled={disabled}
      onChange={value => changeAccountSettings({ [field]: value !== invert })} />
  )
}

/** A row whose action happens on Discord's own site (the password, two-factor, closing the account): its value, its button. */
export function LinkRow({ label, value, button, url, danger }: { label: string; value?: ReactNode; button: string; url: string; danger?: boolean }) {
  return (
    <Row label={label}>
      {value}
      <button type="button" className={`exo-set-btn${danger ? ' is-danger' : ''}`} aria-label={`${button}: ${label}`} onClick={() => void host.openUrl(url).catch(() => { })}>{button}</button>
    </Row>
  )
}

/** Discord's settings page for the web, where what Exo's client can't do is done. */
export const DISCORD_SETTINGS_URL = 'https://discord.com/settings/account'

// What each settings page last read, by what it is: a section opens at once with it (they mount as you switch to
// them) and reads again behind it, never showing Loading for what it has shown before.
const lastRead = new Map<string, unknown>()

/** A page's state that starts from what was last read under key, and is kept there for the next time. */
export function useRemembered<T>(key: string): [T | null, Dispatch<SetStateAction<T | null>>] {
  const [value, setValue] = useState<T | null>(() => (lastRead.get(key) as T | undefined) ?? null)
  useEffect(() => { if (value !== null) lastRead.set(key, value) }, [key, value])
  return [value, setValue]
}

/**
 * The settings Exo keeps for its Discord client: null until read (error says why it wasn't, reload tries again); save
 * shows a change at once, then takes what was saved, or puts it back with why.
 */
export function useClientSettings() {
  const [settings, setSettings] = useRemembered<DiscordClientSettings>('client')
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    let live = true
    host.discordClientSettings().then(read => { if (live) { setSettings(read); setError(null) } })
      .catch((err: unknown) => { report(err, 'DiscordSettings.client'); if (live) setError("Exo couldn't read these settings.") })
    return () => { live = false }
  }, [round, setSettings])
  const save = (change: Partial<DiscordClientSettings>) => {
    // Only this change's own settings go back if it fails, never another one's made meanwhile.
    const before = settings && Object.fromEntries(Object.keys(change).map(key => [key, settings[key as keyof DiscordClientSettings]])) as Partial<DiscordClientSettings>
    setSettings(s => s && { ...s, ...change })
    return host.discordClientSettings(change).then(saved => { setSettings(saved); return null }, (err: unknown) => {
      report(err, 'DiscordSettings.save')
      setSettings(s => s && before && { ...s, ...before })
      return "Exo didn't save that. Try again."
    })
  }
  return { settings, save, error, reload: () => { setError(null); setRound(r => r + 1) } }
}

/** A page's settings that didn't load: why, and Try Again. */
export function LoadError({ error, onRetry }: { error: string | null; onRetry: () => void }) {
  if (!error) return null
  return (
    <p className="exo-dc-settings-note" role="alert">
      {error} <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button> <CopyLogs error={error} />
    </p>
  )
}

/**
 * Loads something for a page (key: what it is); null until it first arrives, with the error line if it didn't. It
 * shows what was last read at once and reads again behind it; a failed read keeps what the page had.
 */
export function useLoad<T>(key: string, load: () => Promise<{ ok: boolean; message?: string | null; data?: unknown }>, map: (data: unknown) => T) {
  const [value, setValue] = useRemembered<T>(key)
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    let live = true
    load().then(result => {
      if (!live) return
      if (result.ok) { setValue(map(result.data)); setError(null) }
      else setError(errorText(result.message, "Discord didn't answer. Try again."))
    }).catch(() => { if (live) setError("Discord didn't answer. Try again.") })
    return () => { live = false }
  }, [round]) // eslint-disable-line react-hooks/exhaustive-deps -- load and map are the page's own, fixed per page
  return { value, error, loading: value === null && error === null, reload: () => setRound(r => r + 1) }
}
