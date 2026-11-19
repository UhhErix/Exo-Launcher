import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { openDiscord, requestDiscordSettings } from '../../lib/discord'
import { useDismiss } from '../../lib/dismiss'
import { useAccountSettings } from '../../lib/discordPrefs'
import { Dialog } from '../ui/Dialog'
import { DiscordQrSignIn } from './DiscordSignIn'
import { Avatar } from './presence'
import { SubContext, Submenu } from './UserMenu'
import { Icons } from './icons'
import { SHOP } from './roomPlaces'

type Status = 'online' | 'idle' | 'dnd' | 'invisible'

// Discord's own lengths for a status, in minutes (0: until you change it).
const STATUS_FOR: [number, string][] = [[15, 'For 15 minutes'], [60, 'For 1 hour'], [480, 'For 8 hours'], [1440, 'For 24 hours'], [4320, 'For 3 days'], [0, 'Forever']]
const NAMES: Record<Status, string> = { online: 'Online', idle: 'Idle', dnd: 'Do not disturb', invisible: 'Invisible' }

/** Closes on a click outside or Escape. */
function useClose(box: React.RefObject<HTMLDivElement | null>, onClose: () => void) {
  useDismiss(box, onClose)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose])
}

/**
 * Your picture's menu at the end of the band: Online, or Idle, Do not disturb and Invisible each for a while (15 minutes
 * to 3 days) or for good, with when a chosen one ends; your custom status and profile; your microphone and speakers; the
 * Shop; and your user ID. A status for a while is kept on your account, so it ends on every device.
 */
export function MeMenu({ meId, status, onClose }: { meId: string; status: string | null; onClose: () => void }) {
  const [sub, setSub] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const accounts = useAccounts()
  const box = useRef<HTMLDivElement>(null)
  useClose(box, onClose)
  const settings = useAccountSettings()
  const until = status && status !== 'online' && settings.statusExpiresAtMs ? new Date(settings.statusExpiresAtMs) : null
  const set = (next: Status, minutes = 0) => void host.discordSetStatus(next, minutes).then(
    result => { if (result.ok) onClose(); else setError(result.message ?? "Discord didn't change your status.") },
    () => setError("Discord didn't change your status."))
  const settingsAt = (section: Parameters<typeof requestDiscordSettings>[0]) => { onClose(); requestDiscordSettings(section) }
  const switchTo = (id: string) => void host.discordAccounts('switch', id).then(
    result => { if (result.ok) onClose(); else setError(result.message ?? "Exo couldn't switch accounts.") },
    () => setError("Exo couldn't switch accounts."))
  if (adding) return <AddAccount onClose={onClose} />
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
      <div ref={box} className="exo-dc-menu is-status" role="menu" aria-label="Your status">
        <button type="button" role="menuitemradio" aria-checked={(status ?? 'online') === 'online'} onClick={() => set('online')}>{NAMES.online}</button>
        {(['idle', 'dnd', 'invisible'] as const).map(kind => (
          <Submenu key={kind} label={NAMES[kind]}>
            {STATUS_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => set(kind, minutes)}>{label}</button>)}
          </Submenu>
        ))}
        {until ? <span className="exo-dc-menu-note">{NAMES[status as Status]} until {until.toLocaleString(undefined, { weekday: until.getTime() - Date.now() > 864e5 ? 'short' : undefined, hour: 'numeric', minute: '2-digit' })}</span> : null}
        {error ? <span className="exo-dc-menu-note is-bad" role="alert">{error}</span> : null}
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" onClick={() => settingsAt('account')}>{settings.customStatus ? 'Edit custom status' : 'Set a custom status'}</button>
        <button type="button" role="menuitem" onClick={() => settingsAt('profiles')}>Edit profile</button>
        <Submenu label="Switch accounts">
          {accounts.map(account => (
            <button key={account.id} type="button" role="menuitemradio" aria-checked={account.current} disabled={account.current} onClick={() => switchTo(account.id)}>
              <Avatar src={account.avatar} name={account.name} status={null} size={20} />{account.name}
            </button>
          ))}
          {accounts.length > 0 ? <span className="exo-dc-menu-line" role="separator" /> : null}
          <button type="button" role="menuitem" disabled={accounts.length >= 5} onClick={() => setAdding(true)}>Add an account</button>
        </Submenu>
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" onClick={() => settingsAt('voice')}>Voice settings</button>
        <button type="button" role="menuitem" onClick={() => { onClose(); openDiscord({ guildId: null, channelId: SHOP, outside: false }) }}>Shop</button>
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(meId).catch(() => { }); onClose() }}><Icons.CopyId size={16} />Copy user ID</button>
      </div>
    </SubContext.Provider>
  )
}

type Account = { id: string; name: string; avatar: string | null; current: boolean }

/** The accounts signed in on this PC, read as the menu opens. */
function useAccounts() {
  const [accounts, setAccounts] = useState<Account[]>([])
  useEffect(() => { host.discordAccounts().then(result => setAccounts(result.accounts ?? []), () => setAccounts([])) }, [])
  return accounts
}

/** Add an account: Discord's QR sign-in; once the phone confirms, Exo connects as it and keeps the other one to switch back to. */
function AddAccount({ onClose }: { onClose: () => void }) {
  useEffect(() => onHostEvent('discord.signin', step => { if (step.state === 'done') onClose() }), [onClose])
  return (
    <Dialog title="Add an account" onClose={onClose} actions={<button type="button" className="exo-set-btn" onClick={onClose}>Cancel</button>}>
      <DiscordQrSignIn />
    </Dialog>
  )
}


