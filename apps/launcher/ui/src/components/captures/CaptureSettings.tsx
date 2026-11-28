import { useState, type KeyboardEvent } from 'react'
import { host } from '../../lib/host'
import { clock, keysLabel, saveCaptureSettings as save, useCapture, type CaptureSettings, type CaptureState } from '../../lib/capture'
import { Choice } from '../settings/Choice'
import { Group, Row, State, SwitchRow } from '../settings/parts'
import { Switch } from '../Switch'
import { errorText } from '../../lib/errorWords'

const MODES = [
  { value: 'games', label: 'While playing' },
  { value: 'always', label: 'Always' },
  { value: 'off', label: 'Off' },
] as const
const LENGTHS = [
  { value: '30', label: '30 s' },
  { value: '60', label: '1 min' },
  { value: '120', label: '2 min' },
  { value: '300', label: '5 min' },
  { value: '600', label: '10 min' },
] as const

const KEEP_GB = [
  { value: '0', label: 'No limit' },
  { value: '25', label: '25 GB' },
  { value: '50', label: '50 GB' },
  { value: '100', label: '100 GB' },
] as const
const KEEP_DAYS = [
  { value: '0', label: 'Never' },
  { value: '7', label: '7 days' },
  { value: '30', label: '30 days' },
  { value: '90', label: '90 days' },
] as const

type Keys = 'saveReplayKeys' | 'recordKeys' | 'snipKeys'

/** The keys pressed, as "Ctrl+Alt+F10"; null while only modifiers are down. */
function comboOf(event: KeyboardEvent): string | null {
  const key = event.key
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(key)) return null
  const name = key === ' ' ? 'Space' : key === 'PrintScreen' ? 'PrintScreen' : /^F\d{1,2}$/.test(key) ? key
    : key.length === 1 && /[a-z0-9]/i.test(key) ? key.toUpperCase() : event.code.startsWith('Digit') ? event.code.slice(5) : null
  if (!name) return null
  return [event.ctrlKey ? 'Ctrl' : null, event.altKey ? 'Alt' : null, event.shiftKey ? 'Shift' : null, name].filter(Boolean).join('+')
}

/** A hotkey: press Change, then the keys; Escape keeps the old ones. */
function HotkeyRow({ label, field, settings, ok, disabled }: {
  label: string
  field: Keys
  settings: CaptureSettings | null
  ok: boolean
  disabled: boolean
}) {
  const [listening, setListening] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const id = `set-capture-${field}`
  async function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (!listening) return
    event.preventDefault()
    event.stopPropagation()
    if (event.key === 'Escape') { setListening(false); return }
    const combo = comboOf(event)
    if (!combo) return
    setListening(false)
    setError(await save({ [field]: combo }))
  }
  return (
    <Row label={label} labelId={id} error={error ?? (!ok ? 'Another app already uses these keys. Pick others.' : null)}>
      <button
        type="button"
        className={`exo-set-btn exo-capture-hotkey${listening ? ' is-listening' : ''}`}
        aria-labelledby={id}
        disabled={disabled}
        onClick={() => { setError(null); setListening(value => !value) }}
        onBlur={() => setListening(false)}
        onKeyDown={event => void onKeyDown(event)}
      >
        {listening ? 'Press keys' : keysLabel(settings?.[field])}
      </button>
    </Row>
  )
}

/**
 * Captures › Capture settings: Exo Capture's instant replay, quality, keys, folder and
 * storage limit, where the captures are. Every row saves at once. Every capture has the mix,
 * then the game and your microphone each on its own track; the call's voices only with
 * Record the call's voices on (on a track of their own, and the call is told it's recorded).
 */
export function CaptureSettingsView() {
  const { state, settings } = useCapture()
  if (state && !state.present) {
    return (
      <div className="exo-capture-settings">
        <div className="exo-set-list">
          <Row label="Exo Capture">
            <State>Not installed</State>
          </Row>
        </div>
      </div>
    )
  }
  return (
    <div className="exo-capture-settings">
      <Group id="capture-recording" title="Recording">
        <ReplayRows state={state} settings={settings} />
        <CallVoicesRow settings={settings} />
        <CaptureKeyRows state={state} settings={settings} />
      </Group>
      <Group id="capture-storage" title="Storage">
        <StorageRows settings={settings} />
      </Group>
    </div>
  )
}

/** The capture keys and Print Screen. */
export function CaptureKeyRows({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  return (
    <>
      <HotkeyRow label="Save replay" field="saveReplayKeys" settings={settings} ok={state?.hotkeys.saveReplay ?? true} disabled={!settings} />
      <HotkeyRow label="Record" field="recordKeys" settings={settings} ok={state?.hotkeys.record ?? true} disabled={!settings} />
      <HotkeyRow label="Snip" field="snipKeys" settings={settings} ok={state?.hotkeys.snip ?? true} disabled={!settings} />
      <PrintScreenRow state={state} settings={settings} />
    </>
  )
}

/** One error per row: a failed save reads under the row whose control made it. */
function useRowErrors() {
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  return {
    of: (row: string) => (error?.row === row ? error.text : null),
    set: (row: string) => (text: string | null) => setError(text ? { row, text } : null),
  }
}


function ReplayRows({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  const errors = useRowErrors()
  const disabled = !settings
  const retained = state?.buffering
    ? state.replayRetainedMs == null ? 'Available time not reported' : `${clock(state.replayRetainedMs)} available`
    : null
  const limit = settings && settings.replayMode !== 'off' && state?.replayLimitSeconds != null && state.replayLimitSeconds < settings.replaySeconds
    ? `Up to ${clock(state.replayLimitSeconds * 1000)} on this PC` : null
  return (
    <>
      <Row label="Instant replay" labelId="set-capture-mode" error={errors.of('mode')}>
        <Choice
          label="Instant replay"
          value={settings?.replayMode ?? 'games'}
          choices={MODES}
          disabled={disabled}
          onChange={value => void save({ replayMode: value as CaptureSettings['replayMode'] }).then(errors.set('mode'))}
        />
      </Row>
      {settings?.replayMode !== 'off' ? (
        <Row label="Replay length" labelId="set-capture-length" detail={[retained, limit].filter(Boolean).join(' · ')} error={errors.of('length')}>
          <Choice
            label="Replay length"
            value={String(settings?.replaySeconds ?? 60)}
            choices={LENGTHS}
            disabled={disabled}
            onChange={value => void save({ replaySeconds: Number(value) }).then(errors.set('length'))}
          />
        </Row>
      ) : null}

    </>
  )
}

/**
 * Print Screen as Snip. When Windows keeps the key for Snipping Tool, that reads under the row as what is wrong and what
 * to do, as a hotkey another app holds does (a row's grey line is only for a value).
 */
function PrintScreenRow({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  const [error, setError] = useState<string | null>(null)
  const printBlocked = state?.hotkeys.printScreen === false && settings?.printScreen === true
  return (
    <Row label="Print Screen snips" labelId="set-capture-prtsc"
      error={error ?? (printBlocked ? 'Windows keeps Print Screen for Snipping Tool. Turn that off in Windows Settings > Accessibility > Keyboard.' : null)}>
      <Switch checked={settings?.printScreen === true} disabled={!settings} labelledBy="set-capture-prtsc"
        onChange={value => { setError(null); void save({ printScreen: value }).then(setError) }} />
    </Row>
  )
}

/** Off (the default), a capture leaves the call out; on, Exo tells everyone in the call, as Discord's clips do. */
function CallVoicesRow({ settings }: { settings: CaptureSettings | null }) {
  return (
    <SwitchRow
      id="set-capture-call"
      label="Record voice chat"
      checked={settings?.callVoices === true}
      disabled={!settings}
      onChange={value => save({ callVoices: value })}
    />
  )
}

function StorageRows({ settings }: { settings: CaptureSettings | null }) {
  const errors = useRowErrors()
  const [picking, setPicking] = useState(false)
  const disabled = !settings
  async function openFolder() {
    const fail = errors.set('folder')
    fail(null)
    try {
      const result = await host.captureAct('openFolder')
      if (!result.ok) fail(errorText(result.message, 'Exo couldn’t open the captures folder.'))
    } catch (error) {
      fail(errorText(error, 'Exo couldn’t open the captures folder.'))
    }
  }
  async function browse() {
    if (picking) return
    setPicking(true)
    const fail = errors.set('folder')
    fail(null)
    try {
      const result = await host.pickFolder('Choose where captures are saved')
      if (result.ok && result.path) fail(await save({ folder: result.path }))
      else if (!result.ok && !result.cancelled) fail(errorText(result.message, 'Exo couldn’t open the folder picker. Try again.'))
    } catch (error) {
      fail(errorText(error, 'Exo couldn’t open the folder picker. Try again.'))
    } finally { setPicking(false) }
  }
  return (
    <>
      <Row label="Save to" detail={settings?.folder} error={errors.of('folder')}>
        <button type="button" className="exo-set-btn" disabled={disabled} onClick={() => void openFolder()}>Open</button>
        <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => void browse()}>Change</button>
      </Row>
      <Row label="Max storage" labelId="set-capture-gb" error={errors.of('gb')}>
        <Choice label="Max storage" value={String(settings?.keepGb ?? 0)} choices={KEEP_GB} disabled={disabled} onChange={value => void save({ keepGb: Number(value) }).then(errors.set('gb'))} />
      </Row>
      <Row label="Auto-delete after" labelId="set-capture-days" error={errors.of('days')}>
        <Choice label="Auto-delete after" value={String(settings?.keepDays ?? 0)} choices={KEEP_DAYS} disabled={disabled} onChange={value => void save({ keepDays: Number(value) }).then(errors.set('days'))} />
      </Row>
    </>
  )
}
