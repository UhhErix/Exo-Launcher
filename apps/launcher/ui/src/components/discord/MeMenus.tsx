import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { openDiscord, requestDiscordSettings } from '../../lib/discord'
import { useDismiss } from '../../lib/dismiss'
import { changeAccountSettings, useAccountSettings, type DiscordClientSettings } from '../../lib/discordPrefs'
import { Dialog } from '../ui/Dialog'
import { Slider } from '../ui/Slider'
import { CustomStatusDialog } from './settings/AccountPages'
import { DiscordQrSignIn } from './DiscordSignIn'
import { Avatar } from './presence'
import { SubContext, Submenu } from './UserMenu'
import { Icons } from './icons'
import { SHOP } from './roomPlaces'
import { errorText } from '../../lib/errorWords'

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
  const [editingStatus, setEditingStatus] = useState(false)
  const accounts = useAccounts()
  const box = useRef<HTMLDivElement>(null)
  useClose(box, onClose)
  const settings = useAccountSettings()
  const until = status && status !== 'online' && settings.statusExpiresAtMs ? new Date(settings.statusExpiresAtMs) : null
  const set = (next: Status, minutes = 0) => void host.discordSetStatus(next, minutes).then(
    result => { if (result.ok) onClose(); else setError(errorText(result.message, "Discord didn't change your status.")) },
    () => setError("Discord didn't change your status."))
  const settingsAt = (section: Parameters<typeof requestDiscordSettings>[0]) => { onClose(); requestDiscordSettings(section) }
  const switchTo = (id: string) => void host.discordAccounts('switch', id).then(
    result => { if (result.ok) onClose(); else setError(errorText(result.message, "Exo couldn't switch accounts.")) },
    () => setError("Exo couldn't switch accounts."))
  if (adding) return <AddAccount onClose={onClose} />
  if (editingStatus) {
    return (
      <CustomStatusDialog
        custom={settings.customStatus}
        onSave={next => void changeAccountSettings({ customStatus: next })}
        onClose={onClose}
      />
    )
  }
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
        <button type="button" role="menuitem" onClick={() => setEditingStatus(true)}>{settings.customStatus ? 'Edit custom status' : 'Set a custom status'}</button>
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

type AudioState = Awaited<ReturnType<typeof host.discordAudio>>

/** Quick audio menu on Mute (input) and Deafen (output) in the user panel. */
export function AudioQuickMenu({ kind, onClose }: { kind: 'input' | 'output'; onClose: () => void }) {
  const [audio, setAudio] = useState<AudioState | null>(null)
  const [client, setClient] = useState<DiscordClientSettings | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useClose(box, onClose)
  useEffect(() => {
    void host.discordAudio({}).then(setAudio).catch(() => { })
    if (kind === 'input') void host.discordClientSettings({}).then(setClient).catch(() => { })
  }, [kind])

  const saveAudio = (change: Parameters<typeof host.discordAudio>[0]) => {
    void host.discordAudio(change).then(setAudio).catch(() => { })
  }
  const saveClient = (change: Parameters<typeof host.discordClientSettings>[0]) => {
    void host.discordClientSettings(change).then(setClient).catch(() => { })
  }
  return (
    <div ref={box} className={`exo-dc-menu is-status exo-dc-audio-menu ${kind === 'input' ? 'is-input' : 'is-output'}`}
      role="menu" aria-label={kind === 'input' ? 'Input options' : 'Output options'}>
      {kind === 'input'
        ? <InputQuickItems audio={audio} client={client} saveAudio={saveAudio} saveClient={saveClient} onClose={onClose} />
        : <OutputQuickItems audio={audio} saveAudio={saveAudio} onClose={onClose} />}
    </div>
  )
}

function DeviceRadioList({ label, devices, chosen, onPick }: { label: string; devices: { id: string; name: string }[]; chosen: string; onPick: (id: string) => void }) {
  const options = [{ id: '', name: 'Default' }, ...devices]
  return (
    <>
      <span className="exo-dc-menu-note">{label}</span>
      {options.map(d => (
        <button key={d.id || 'default'} type="button" role="menuitemradio" aria-checked={chosen === d.id} onClick={() => onPick(d.id)}>
          <span className="exo-dc-menu-item-label">{d.name}</span>
          {chosen === d.id ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
        </button>
      ))}
    </>
  )
}

function PercentSliderRow({ label, value, max, onCommit }: { label: string; value: number; max: number; onCommit: (v: number) => void }) {
  const [draft, setDraft] = useState<number | null>(null)
  const shown = draft ?? value
  return (
    <div className="exo-dc-ctx-volume" role="group" aria-label={label}>
      <span>{label}</span>
      <span className="exo-dc-ctx-volume-row">
        <Slider min={0} max={max} value={shown} aria-label={label}
          onChange={e => setDraft(Number(e.target.value))}
          onPointerUp={e => { const v = Number(e.currentTarget.value); setDraft(null); onCommit(v) }}
          onKeyUp={e => { const v = Number(e.currentTarget.value); setDraft(null); onCommit(v) }} />
        <span>{Math.round(shown)}%</span>
      </span>
    </div>
  )
}

function InputQuickItems({ audio, client, saveAudio, saveClient, onClose }: {
  audio: AudioState | null
  client: DiscordClientSettings | null
  saveAudio: (c: Parameters<typeof host.discordAudio>[0]) => void
  saveClient: (c: Parameters<typeof host.discordClientSettings>[0]) => void
  onClose: () => void
}) {
  const suppression = !client ? null : !client.noiseSuppression ? 'none' : client.noiseSuppressionKind === 'standard' ? 'standard' : 'exo'
  return (
    <>
      {audio ? (
        <>
          <DeviceRadioList label="Input device" devices={audio.inputs} chosen={audio.input ?? ''} onPick={input => saveAudio({ input })} />
          <span className="exo-dc-menu-line" role="separator" />
          <PercentSliderRow label="Input volume" value={audio.inputVolume} max={200} onCommit={inputVolume => saveAudio({ inputVolume })} />
        </>
      ) : null}
      {client ? (
        <>
          <span className="exo-dc-menu-line" role="separator" />
          <span className="exo-dc-menu-note">Noise suppression</span>
          {([['exo', 'Exo Voice'], ['standard', 'Standard'], ['none', 'Off']] as const).map(([key, text]) => (
            <button key={key} type="button" role="menuitemradio" aria-checked={suppression === key}
              onClick={() => saveClient(key === 'none' ? { noiseSuppression: false } : { noiseSuppression: true, noiseSuppressionKind: key === 'standard' ? 'standard' : 'exo' })}>
              <span className="exo-dc-menu-item-label">{text}</span>
              {suppression === key ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
            </button>
          ))}
          <span className="exo-dc-menu-line" role="separator" />
          <span className="exo-dc-menu-note">Input mode</span>
          <button type="button" role="menuitemradio" aria-checked={!client.pushToTalk} onClick={() => saveClient({ pushToTalk: false })}>
            <span className="exo-dc-menu-item-label">Voice activity</span>
            {!client.pushToTalk ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
          </button>
          <button type="button" role="menuitemradio" aria-checked={client.pushToTalk} onClick={() => saveClient({ pushToTalk: true })}>
            <span className="exo-dc-menu-item-label">Push to talk</span>
            {client.pushToTalk ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
          </button>
        </>
      ) : null}
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" onClick={() => { onClose(); requestDiscordSettings('voice') }}>Voice settings</button>
    </>
  )
}

function OutputQuickItems({ audio, saveAudio, onClose }: {
  audio: AudioState | null
  saveAudio: (c: Parameters<typeof host.discordAudio>[0]) => void
  onClose: () => void
}) {
  return (
    <>
      {audio ? (
        <>
          <DeviceRadioList label="Output device" devices={audio.outputs} chosen={audio.output ?? ''} onPick={output => saveAudio({ output })} />
          <span className="exo-dc-menu-line" role="separator" />
          <PercentSliderRow label="Output volume" value={audio.outputVolume} max={200} onCommit={outputVolume => saveAudio({ outputVolume })} />
          <PercentSliderRow label="Soundboard volume" value={audio.soundboardMuted ? 0 : audio.soundboardVolume} max={100}
            onCommit={v => saveAudio(v === 0 ? { soundboardMuted: true } : { soundboardVolume: v, soundboardMuted: false })} />
          <button type="button" role="menuitemcheckbox" aria-checked={audio.soundboardMuted}
            onClick={() => saveAudio({ soundboardMuted: !audio.soundboardMuted })}>
            <span className="exo-dc-menu-item-label">Mute soundboard</span>
            {audio.soundboardMuted ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
          </button>
        </>
      ) : null}
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" onClick={() => { onClose(); requestDiscordSettings('voice') }}>Voice settings</button>
    </>
  )
}


