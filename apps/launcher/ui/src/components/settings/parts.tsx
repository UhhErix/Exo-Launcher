import { ErrorNote } from '../ui/ErrorNote'
import { useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Switch } from '../Switch'
import { Slider } from '../ui/Slider'
import { useSaid } from '../ui/useSaid'
import { Choice } from './Choice'

/** One settings panel: a heading, optional header action, then rows. `id` makes it a place a link can open at. */
export function Section({ id, title, action, children }: {
  id?: string
  /** None where it is the page itself: the page's title says it. */
  title?: string
  action?: ReactNode
  children: ReactNode
}) {
  const heading = id && title ? `set-${id}-title` : undefined
  return (
    <section className="exo-set-section" id={id && `set-${id}`} aria-label={heading ? undefined : title}
      aria-labelledby={heading ?? (title ? undefined : 'exo-settings-page-title')}>
      {title ? (
        <header className="exo-set-section-head">
          <h2 id={heading} tabIndex={heading ? -1 : undefined}>{title}</h2>
          {action}
        </header>
      ) : null}
      <div className="exo-set-list">{children}</div>
    </section>
  )
}

/** A named group of rows inside a section: its name, an optional action, then rows. */
export function Group({ id, title, action, children }: {
  id: string
  title: string
  action?: ReactNode
  children: ReactNode
}) {
  return (
    <div className="exo-set-group" id={`set-${id}`} role="group" aria-labelledby={`set-${id}-title`}>
      <header className="exo-set-group-head">
        <h3 id={`set-${id}-title`}>{title}</h3>
        {action}
      </header>
      <div className="exo-set-list">{children}</div>
    </div>
  )
}

/**
 * The one settings row: label on the left, state and control on a shared right edge. `detail` is one short plain line for
 * what the row can't show otherwise (a folder, a result just in, why its control is off) or, where the name alone doesn't
 * say it, what the row does; `error` sits under the row; `stacked` puts a wide control (a field, a meter) under the label.
 */
function TruncatedLabel({ id, label }: { id?: string; label: string }) {
  const ref = useRef<HTMLSpanElement>(null)
  const [tip, setTip] = useState<{ left: number; top: number } | null>(null)
  const show = () => {
    const el = ref.current
    if (!el || el.scrollWidth <= el.clientWidth + 1) return
    const rect = el.getBoundingClientRect()
    setTip({
      left: Math.max(16, Math.min(rect.left + rect.width / 2, window.innerWidth - 16)),
      top: Math.max(16, rect.top - 6),
    })
  }
  const hide = () => setTip(null)
  return (
    <>
      <span ref={ref} id={id} className="exo-set-item-label is-truncated" onMouseEnter={show} onMouseLeave={hide}>{label}</span>
      {tip ? createPortal(<span className="exo-glass exo-set-tip" role="tooltip" style={{ left: `${tip.left}px`, top: `${tip.top}px` }}>{label}</span>, document.body) : null}
    </>
  )
}

export function Row({ label, labelId, detail, icon, stacked, truncateLabel, children, error }: {
  label: string
  labelId?: string
  detail?: ReactNode
  icon?: ReactNode
  stacked?: boolean
  truncateLabel?: boolean
  children?: ReactNode
  error?: string | null
}) {
  return (
    <div className={`exo-set-item${stacked ? ' is-stacked' : ''}`}>
      <div className="exo-set-item-main">
        {icon ? <span className="exo-set-item-icon">{icon}</span> : null}
        <div className="exo-set-item-copy">
          {truncateLabel ? <TruncatedLabel id={labelId} label={label} /> : <span id={labelId} className="exo-set-item-label">{label}</span>}
          {detail ? <span className="exo-set-item-detail">{detail}</span> : null}
        </div>
        {children ? <div className="exo-set-item-control">{children}</div> : null}
      </div>
      {error ? <ErrorNote className="exo-set-error" message={error} /> : null}
    </div>
  )
}

export function State({ on, children }: { on?: boolean; children: ReactNode }) {
  return <span className={`exo-set-state${on ? ' is-on' : ''}`}>{children}</span>
}

/** What a row's save answers: null, or nothing, when it took; else why not, which shows under the row. */
type Saved = Promise<string | null | void> | void

/**
 * A switch row that saves on change. The knob moves on the press and holds there until the newest save answers, when
 * `checked` (which the caller has updated by then) takes over again, so a refusal puts it back.
 */
export function SwitchRow({ id, label, detail, checked, disabled, children, onChange }: {
  id?: string
  label: string
  detail?: ReactNode
  checked: boolean
  disabled?: boolean
  /** A control before the switch (a sound's Preview). */
  children?: ReactNode
  onChange: (value: boolean) => Saved
}) {
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState<boolean | null>(null)
  const newest = useRef(0)
  function change(value: boolean) {
    const mine = ++newest.current
    setError(null)
    setPending(value)
    void Promise.resolve(onChange(value))
      .then(why => setError(why ?? null), () => setError('The change could not be saved. Try again.'))
      .finally(() => { if (mine === newest.current) setPending(null) })
  }
  return (
    <Row label={label} labelId={id} detail={detail} error={error}>
      {children}
      <Switch checked={pending ?? checked} disabled={disabled} labelledBy={id} label={id ? undefined : label} onChange={change} />
    </Row>
  )
}

/** A choice among a few, saved on change; a failed save shows under the row. */
export function ChoiceRow<T extends string | number>({ id, label, detail, value, choices, disabled, onChange }: {
  id?: string
  label: string
  detail?: ReactNode
  value: T
  choices: readonly { value: T; label: string }[]
  disabled?: boolean
  onChange: (value: T) => Saved
}) {
  const [error, setError] = useState<string | null>(null)
  return (
    <Row label={label} labelId={id} detail={detail} error={error}>
      <Choice label={label} value={String(value)} disabled={disabled} choices={choices.map(c => ({ value: String(c.value), label: c.label }))}
        onChange={next => {
          const picked = choices.find(c => String(c.value) === next)
          if (!picked) return
          setError(null)
          void Promise.resolve(onChange(picked.value)).then(why => setError(why ?? null), () => setError('The change could not be saved. Try again.'))
        }} />
    </Row>
  )
}

/** A slider that shows its value as it moves and saves when let go. */
export function SliderRow({ label, min, max, step = 1, value, render, disabled, error, onCommit }: {
  label: string
  min: number
  max: number
  step?: number
  value: number
  render: (value: number) => string
  disabled?: boolean
  error?: string | null
  onCommit: (value: number) => void
}) {
  const [moving, setMoving] = useState<number | null>(null)
  const shown = moving ?? value
  const commit = (next: number) => { setMoving(null); if (next !== value) onCommit(next) }
  return (
    <Row label={label} error={error}>
      <span className="exo-set-volume">
        <Slider min={min} max={max} step={step} value={shown} disabled={disabled} aria-label={label} aria-valuetext={render(shown)}
          onChange={event => setMoving(Number(event.target.value))}
          onPointerUp={event => commit(Number(event.currentTarget.value))} onKeyUp={event => commit(Number(event.currentTarget.value))} />
        <span>{render(shown)}</span>
      </span>
    </Row>
  )
}

/** A link or address to paste elsewhere: the press copies it and says so. */
export function CopyRow({ label, text, detail }: { label: string; text: string; detail?: ReactNode }) {
  const [copied, sayCopied] = useSaid()
  return (
    <Row label={label} detail={detail}>
      <button type="button" className="exo-set-btn" aria-label={`Copy ${label}`}
        onClick={() => { navigator.clipboard.writeText(text).then(() => sayCopied('Copied'), () => sayCopied('Not copied')) }}>
        {copied ?? 'Copy'}
      </button>
    </Row>
  )
}
