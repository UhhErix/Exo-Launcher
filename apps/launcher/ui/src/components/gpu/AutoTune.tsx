import { useEffect, useState } from 'react'
import { Check } from '../../brand/icons'
import { host, type AutoTune, type AutoTuneMode, type GpuProfile, type PcAbout, type TuneMeasure, type TunePart } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'

const MODES = [
  { value: 'cooler', label: 'Cooler' },
  { value: 'faster', label: 'Faster' },
] as const

/** Measured by Exo's own test, before and after, in the same words every time; nothing when either wasn't measured. */
function tuneResult(before?: TuneMeasure | null, after?: TuneMeasure | null): string | null {
  if (!before || !after) return null
  const heat = before.peakC != null && after.peakC != null ? Math.round(before.peakC - after.peakC) : null
  const score = before.score > 0 ? Math.round((after.score / before.score - 1) * 100) : null
  const words = [
    heat === null ? null : heat > 0 ? `${heat} °C cooler` : heat < 0 ? `${-heat} °C warmer` : 'same temperature',
    score === null ? null : score > 0 ? `score ${score}% higher` : score < 0 ? `score ${-score}% lower` : 'same score',
  ].filter(Boolean)
  return words.length ? words.join(', ') : null
}

/** A test runs for many minutes in its own process: read while it does, and while its menu is open for one starting. */
export function useTuneReads(active: boolean, tune: AutoTune | null, open: boolean, onTune: (next: AutoTune) => void) {
  const testing = tune?.parts.some(part => part.state === 'testing') ?? false
  useEffect(() => {
    if (!active || (!testing && !open)) return
    const timer = window.setInterval(() => { host.systemTune().then(onTune, (e: unknown) => report(e, 'AutoTune.read', { level: 'warn' })) }, 3000)
    return () => window.clearInterval(timer)
  }, [active, testing, open, onTune])
}

/** PC's chip for Auto tune: nothing on a PC with nothing to tune. Green once a part is tuned; off until pressed, so it never counts as something left to set. */
export function AutoTuneChip({ tune, onOpen }: { tune: AutoTune | null; onOpen: () => void }) {
  if (!tune?.parts.length) return null
  const tuned = tune.parts.some(part => part.state === 'tuned')
  const testing = tune.parts.some(part => part.state === 'testing')
  return (
    <button type="button" className={cn('exo-pc-chip', tuned && 'is-good')} data-pc-menu="tune" aria-haspopup="dialog" onClick={onOpen}>
      {tuned ? <Check size={14} /> : null}{testing ? 'Testing' : tuned ? 'Tuned' : 'Auto tune'}
    </button>
  )
}

/** Auto tune's menu: one row per part Exo can tune on this PC. Names come from what the room already read. */
export function AutoTuneRows({ tune, profile, about, onTune }: {
  tune: AutoTune | null
  profile: GpuProfile | null
  about: PcAbout | null
  onTune: (next: AutoTune) => void
}) {
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const act = async (part: TunePart['id'], action: 'start' | 'stop' | 'undo', mode?: AutoTuneMode) => {
    setErrors(current => ({ ...current, [part]: null }))
    try {
      const next = await host.systemTune(action, part, mode)
      onTune(next)
      if (!next.ok) setErrors(current => ({ ...current, [part]: next.message ?? 'That didn’t start.' }))
    } catch (e) {
      report(e, 'AutoTune.act')
      setErrors(current => ({ ...current, [part]: 'That didn’t start.' }))
    }
  }
  return (
    <div className="exo-pc-menu-rows">
      {tune?.parts.map(part => (
        <TuneRow key={part.id} part={part} name={part.id === 'ryzen' ? about?.processor ?? 'Processor' : profile?.gpu ?? about?.graphics ?? 'Graphics'}
          error={errors[part.id] ?? null} onAct={(action, mode) => act(part.id, action, mode)} />
      ))}
    </div>
  )
}

type TuneAction = 'start' | 'stop' | 'undo'

function TuneRow({ part, name, error, onAct }: {
  part: TunePart
  name: string
  error: string | null
  onAct: (action: TuneAction, mode?: AutoTuneMode) => Promise<void>
}) {
  const [busy, setBusy] = useState<TuneAction | null>(null)
  const [mode, setMode] = useState<AutoTuneMode | null>(null)
  const chosen = mode ?? part.mode
  const press = (action: TuneAction) => {
    setBusy(action)
    void onAct(action, action === 'start' ? chosen : undefined).finally(() => setBusy(null))
  }
  const ready = part.state === 'ready' || part.state === 'crashed'
  return (
    <Row label={name} detail={tuneDetail(part)} error={error}>
      {part.state === 'tuned' ? <State on>Tuned</State> : null}
      {part.state === 'needs-driver'
        ? <button type="button" className="exo-set-btn" onClick={() => { host.openUrl('https://pawnio.eu').catch((e: unknown) => report(e, 'AutoTune.pawnio')) }}>Get PawnIO</button>
        : null}
      {ready && part.modes ? <Choice label="Goal" value={chosen} choices={MODES} disabled={!!busy} onChange={value => setMode(value as AutoTuneMode)} /> : null}
      {ready ? <TuneButton warned={!!part.warning} busy={busy} blocked={!!part.blocked} onPress={() => press('start')} /> : null}
      <EndButton part={part} busy={busy} onPress={press} />
    </Row>
  )
}

/** What the row says: what's being tested and how long is left; the value and what was measured; else the warning or why it stopped. */
function tuneDetail(part: TunePart): string | null {
  if (part.state === 'testing') return `${part.testing ?? 'Testing'}${part.minutesLeft ? ` \u00b7 about ${part.minutesLeft} minutes left. Keep Exo open` : ''}`
  if (part.state === 'tuned') return [part.applied, tuneResult(part.before, part.after)].filter(Boolean).join(' \u00b7 ')
  return part.warning ?? part.note ?? null
}

/** Tune: asks in place before a first run (DESIGN.md \u203a Confirm), its warning on the row; after that Windows' own prompt is the only question. */
function TuneButton({ warned, busy, blocked, onPress }: { warned: boolean; busy: TuneAction | null; blocked: boolean; onPress: () => void }) {
  if (warned) return <ConfirmButton className="exo-set-btn is-primary" question="Start at your own risk?" disabled={!!busy || blocked} onConfirm={onPress}>Tune</ConfirmButton>
  return (
    <button type="button" className="exo-set-btn is-primary" disabled={!!busy || blocked} aria-busy={busy === 'start'} onClick={onPress}>
      {busy === 'start' ? <Spinner label="Starting" /> : null}Tune
    </button>
  )
}

/** Stop while it tests, which is back to stock; Undo once anything is on or a crash left a record. */
function EndButton({ part, busy, onPress }: { part: TunePart; busy: TuneAction | null; onPress: (action: TuneAction) => void }) {
  if (part.state === 'testing') {
    return (
      <button type="button" className="exo-set-btn" disabled={busy === 'stop'} aria-busy={busy === 'stop'} onClick={() => onPress('stop')}>
        <Spinner label="Testing" />Stop
      </button>
    )
  }
  if (!part.canUndo) return null
  return (
    <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => onPress('undo')}>
      {busy === 'undo' ? <Spinner label="Going back to stock" /> : null}Undo
    </button>
  )
}
