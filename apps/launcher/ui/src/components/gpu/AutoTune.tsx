import { useEffect, useState } from 'react'
import { Check } from '../../brand/icons'
import { host, type AutoTune, type AutoTuneMode, type GpuProfile, type PcAbout, type TuneMeasure, type TunePart } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import type { PlainError } from '../../lib/errorWords'
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
  const said = (part: TunePart['id'], text: string | null) => setErrors(current => ({ ...current, [part]: text }))
  // The pressed button shows the work (ActionButton); the rows take the answer after its check, or at once on a refusal.
  const act = async (part: TunePart['id'], action: 'start' | 'stop' | 'undo', mode?: AutoTuneMode) => {
    said(part, null)
    const next = await host.systemTune(action, part, mode)
    if (!next.ok) onTune(next)
    return next
  }
  return (
    <div className="exo-pc-menu-rows">
      {tune?.parts.map(part => (
        <TuneRow key={part.id} part={part} name={part.id === 'ryzen' ? about?.processor ?? 'Processor' : profile?.gpu ?? about?.graphics ?? 'Graphics'}
          error={errors[part.id] ?? null} onAct={(action, mode) => act(part.id, action, mode)}
          onSettled={next => onTune(next as AutoTune)} onFailed={text => said(part.id, text)} />
      ))}
    </div>
  )
}

type TuneAction = 'start' | 'stop' | 'undo'

type Press = { onPress: () => Promise<unknown>; onDone: (next: unknown) => void; onFailed: (failure: PlainError) => void; why: 'none'; context: string; fallback: string }

function TuneRow({ part, name, error, onAct, onSettled, onFailed }: {
  part: TunePart
  name: string
  error: string | null
  onAct: (action: TuneAction, mode?: AutoTuneMode) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (text: string) => void
}) {
  const [mode, setMode] = useState<AutoTuneMode | null>(null)
  const chosen = mode ?? part.mode
  const press = (action: TuneAction): Press => ({
    onPress: () => onAct(action, action === 'start' ? chosen : undefined), onDone: onSettled, onFailed: failure => onFailed(failure.text),
    why: 'none', context: `AutoTune.${action}`, fallback: action === 'start' ? 'That didn’t start. Try again.' : 'Not changed. Try again.',
  })
  const ready = part.state === 'ready' || part.state === 'crashed'
  return (
    <Row label={name} detail={tuneDetail(part)} error={error}>
      {part.state === 'tuned' ? <State on>Tuned</State> : null}
      {part.state === 'needs-driver'
        ? <button type="button" className="exo-set-btn" onClick={() => { host.openUrl('https://pawnio.eu').catch((e: unknown) => report(e, 'AutoTune.pawnio')) }}>Get PawnIO</button>
        : null}
      {ready && part.modes ? <Choice label="Goal" value={chosen} choices={MODES} onChange={value => setMode(value as AutoTuneMode)} /> : null}
      {ready ? <TuneButton warned={!!part.warning} blocked={!!part.blocked} press={press('start')} /> : null}
      <EndButton part={part} press={press} />
    </Row>
  )
}

/** What the row says: what's being tested and how long is left; the value and what was measured; else the warning or why it stopped. */
function tuneDetail(part: TunePart): string | null {
  if (part.state === 'testing') return `${part.testing ?? 'Testing'}${part.minutesLeft ? ` \u00b7 about ${part.minutesLeft} minutes left. Keep Exo open` : ''}`
  if (part.state === 'tuned') return [part.applied, tuneResult(part.before, part.after)].filter(Boolean).join(' \u00b7 ')
  return part.warning ?? part.note ?? null
}

/** Tune: asks in place before a first run (DESIGN.md › Confirm), its warning on the row; after that Windows' own prompt is the only question. */
function TuneButton({ warned, blocked, press }: { warned: boolean; blocked: boolean; press: Press }) {
  const { onPress, ...rest } = press
  if (warned) return <ConfirmButton className="exo-set-btn is-primary" question="Start at your own risk?" disabled={blocked} timeoutMs={null} onConfirm={onPress} {...rest}>Tune</ConfirmButton>
  return <ActionButton className="exo-set-btn is-primary" disabled={blocked} timeoutMs={null} onPress={onPress} {...rest}>Tune</ActionButton>
}

/** Stop while it tests, which is back to stock; Undo once anything is on or a crash left a record. */
function EndButton({ part, press }: { part: TunePart; press: (action: TuneAction) => Press }) {
  if (part.state === 'testing') return <ActionButton className="exo-set-btn" lead={<Spinner label="Testing" />} {...press('stop')}>Stop</ActionButton>
  if (!part.canUndo) return null
  return <ActionButton className="exo-set-btn" timeoutMs={null} {...press('undo')}>Undo</ActionButton>
}
