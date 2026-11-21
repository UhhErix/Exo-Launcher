import { useEffect, useState } from 'react'
import { Cpu, GraphicsCard } from '../../brand/icons'
import { host, type AutoTune, type GpuProfile, type PcAbout, type TuneMeasure, type TunePart } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import type { PlainError } from '../../lib/errorWords'
import { Spinner } from '../ui/Spinner'

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

/** Auto tune's menu: one row per part Exo can tune on this PC. Names come from what the room already read. */
export function AutoTuneRows({ tune, profile, about, onTune, variant = 'rows' }: {
  tune: AutoTune | null
  profile: GpuProfile | null
  about: PcAbout | null
  onTune: (next: AutoTune) => void
  variant?: 'rows' | 'cards'
}) {
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const said = (part: TunePart['id'], text: string | null) => setErrors(current => ({ ...current, [part]: text }))
  const act = async (part: TunePart['id'], action: 'start' | 'stop' | 'undo') => {
    said(part, null)
    const next = await host.systemTune(action, part, action === 'start' ? 'faster' : undefined)
    if (!next.ok) onTune(next)
    return next
  }
  const partName = (part: TunePart) => part.id === 'ryzen' ? about?.processor ?? 'Processor' : profile?.gpu ?? about?.graphics ?? 'Graphics'
  if (variant === 'cards') {
    return (
      <div className="exo-at-silicon-grid">
        {tune?.parts.map(part => (
          <SiliconCard
            key={part.id}
            part={part}
            name={partName(part)}
            error={errors[part.id] ?? null}
            onAct={action => act(part.id, action)}
            onSettled={next => onTune(next as AutoTune)}
            onFailed={text => said(part.id, text)}
          />
        ))}
      </div>
    )
  }
  return (
    <div className="exo-pc-menu-rows">
      {tune?.parts.map(part => (
        <TuneRow
          key={part.id}
          part={part}
          name={partName(part)}
          error={errors[part.id] ?? null}
          onAct={action => act(part.id, action)}
          onSettled={next => onTune(next as AutoTune)}
          onFailed={text => said(part.id, text)}
        />
      ))}
    </div>
  )
}

type TuneAction = 'start' | 'stop' | 'undo'

type Press = { onPress: () => Promise<unknown>; onDone: (next: unknown) => void; onFailed: (failure: PlainError) => void; why: 'none'; context: string; fallback: string }

function makePress(action: TuneAction, onAct: (action: TuneAction) => Promise<unknown>, onSettled: (next: unknown) => void, onFailed: (text: string) => void): Press {
  return {
    onPress: () => onAct(action),
    onDone: onSettled,
    onFailed: failure => onFailed(failure.text),
    why: 'none',
    context: `AutoTune.${action}`,
    fallback: action === 'start' ? 'That didn’t start. Try again.' : 'Not changed. Try again.',
  }
}

function siliconStatusLabel(part: TunePart): { label: string; tone: 'is-good' | 'is-warn' | 'is-testing' | null } {
  if (part.state === 'tuned') return { label: 'Tuned', tone: 'is-good' }
  if (part.state === 'testing') return { label: 'Testing', tone: 'is-testing' }
  if (part.state === 'needs-driver') return { label: 'Needs PawnIO', tone: 'is-warn' }
  if (part.blocked) return { label: 'Game open', tone: 'is-warn' }
  if (part.state === 'crashed') return { label: 'Retry', tone: 'is-warn' }
  return { label: 'Max Perf', tone: null }
}

function openPawnIo() {
  host.openUrl('https://pawnio.eu').catch((e: unknown) => report(e, 'AutoTune.pawnio'))
}

function SiliconCard({ part, name, error, onAct, onSettled, onFailed }: {
  part: TunePart
  name: string
  error: string | null
  onAct: (action: TuneAction) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (text: string) => void
}) {
  const press = (action: TuneAction) => makePress(action, onAct, onSettled, onFailed)
  const ready = part.state === 'ready' || part.state === 'crashed'
  const isCpu = part.id === 'ryzen'
  const status = siliconStatusLabel(part)
  const tuneLabel = isCpu ? 'Tune CPU' : 'Tune GPU'

  return (
    <article className={cn('exo-at-silicon-card', part.state === 'tuned' && 'is-tuned', part.state === 'testing' && 'is-testing')}>
      <div className="exo-at-sc-head">
        <span className="exo-at-sc-kind">
          {isCpu ? <Cpu size={14} /> : <GraphicsCard size={14} />}
          {isCpu ? 'CPU · Curve Optimizer' : 'GPU · Max Performance'}
        </span>
        <span className={cn('exo-at-sc-pill', status.tone)}>{status.label}</span>
      </div>
      <div className="exo-at-sc-body">
        <h4 className="exo-at-sc-name">{name}</h4>
        <p className="exo-at-sc-detail">{tuneDetail(part) ?? 'Ready to measure stock and apply maximum stable boost'}</p>
        {error ? <p className="exo-at-sc-error">{error}</p> : null}
      </div>
      <div className="exo-at-sc-actions">
        {part.state === 'needs-driver'
          ? <button type="button" className="exo-set-btn" onClick={openPawnIo}>Get PawnIO</button>
          : null}
        {ready ? <TuneButton warned={!!part.warning} blocked={!!part.blocked} label={tuneLabel} press={press('start')} /> : null}
        <EndButton part={part} press={press} />
      </div>
    </article>
  )
}

function TuneRow({ part, name, error, onAct, onSettled, onFailed }: {
  part: TunePart
  name: string
  error: string | null
  onAct: (action: TuneAction) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (text: string) => void
}) {
  const press = (action: TuneAction) => makePress(action, onAct, onSettled, onFailed)
  const ready = part.state === 'ready' || part.state === 'crashed'
  return (
    <Row label={name} detail={tuneDetail(part)} error={error}>
      {part.state === 'tuned' ? <State on>Tuned</State> : null}
      {part.state === 'needs-driver'
        ? <button type="button" className="exo-set-btn" onClick={openPawnIo}>Get PawnIO</button>
        : null}
      {ready ? <TuneButton warned={!!part.warning} blocked={!!part.blocked} label="Tune" press={press('start')} /> : null}
      <EndButton part={part} press={press} />
    </Row>
  )
}

/** What the row says: what's being tested and how long is left; the value and what was measured; else why Tune waits, the warning or why it stopped. */
function tuneDetail(part: TunePart): string | null {
  if (part.state === 'testing') return `${part.testing ?? 'Testing'}${part.minutesLeft ? ` \u00b7 about ${part.minutesLeft} minutes left. Keep Exo open` : ''}`
  if (part.state === 'tuned') return [part.applied, tuneResult(part.before, part.after)].filter(Boolean).join(' \u00b7 ')
  return part.blocked ?? part.warning ?? part.note ?? null
}

/** Tune: asks in place before a first run (DESIGN.md › Confirm), its warning on the row; after that Windows' own prompt is the only question. */
function TuneButton({ warned, blocked, label, press }: { warned: boolean; blocked: boolean; label: string; press: Press }) {
  const { onPress, ...rest } = press
  if (warned) return <ConfirmButton className="exo-set-btn is-primary" question="Start at your own risk?" disabled={blocked} timeoutMs={null} onConfirm={onPress} {...rest}>{label}</ConfirmButton>
  return <ActionButton className="exo-set-btn is-primary" disabled={blocked} timeoutMs={null} onPress={onPress} {...rest}>{label}</ActionButton>
}

/** Stop while it tests, which is back to stock; Undo once anything is on or a crash left a record. */
function EndButton({ part, press }: { part: TunePart; press: (action: TuneAction) => Press }) {
  if (part.state === 'testing') return <ActionButton className="exo-set-btn" lead={<Spinner label="Testing" />} {...press('stop')}>Stop</ActionButton>
  if (!part.canUndo) return null
  return <ActionButton className="exo-set-btn" timeoutMs={null} {...press('undo')}>Undo</ActionButton>
}
