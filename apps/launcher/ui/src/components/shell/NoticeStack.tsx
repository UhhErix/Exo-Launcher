import { memo, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { Alert, Bell, Check, Close } from '../../brand/icons'
import { anythingOpen } from '../../lib/dismiss'
import { dismissNotice, isSticky, renewNotices, stayFor, useNotices, type NoticeAction, type ShownNotice } from '../../lib/notify'
import { cn } from '../../lib/utils'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Progress, ProgressWords } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'

/**
 * Every notice in Exo (lib/notify.ts), in one stack at the top right under the Bar, newest on top, three at most (the
 * rest wait their turn): large glass, its glyph or picture, a title, one short line, at most one button, a wait along
 * its foot. One that passes goes after the time its words take to read; none goes while the pointer or focus is on the
 * stack or Exo is out of sight. A failure, a button to answer and a wait stay until answered or dismissed. A swipe to the
 * right, its × or Escape takes one away; pressing one that leads somewhere opens it. Screen readers hear each arrival
 * once, from two always-present live regions (polite; a failure assertive). Nothing here ever takes focus.
 */
export const NoticeStack = memo(function NoticeStack() {
  const notices = useNotices()
  const [held, setHeld] = useState(false)
  const hidden = useHidden(notices.length > 0)
  const live = notices.filter(notice => !notice.leaving)
  // A notice that went while the pointer was on it never says it left: nothing is held with none on screen.
  useEffect(() => { if (live.length === 0) setHeld(false) }, [live.length])

  // Each passing notice goes its time after it last changed; none goes while the stack is held or Exo can't be seen.
  const paused = held || hidden
  const passing = live.filter(notice => !isSticky(notice))
  const nextDue = paused || !passing.length ? null : Math.min(...passing.map(due))
  const latest = useRef(passing)
  useEffect(() => { latest.current = passing })
  useEffect(() => {
    if (nextDue === null) return
    // A timer can fire a millisecond or two early; a notice that close is due, or nothing would re-arm and it would stay.
    const timer = window.setTimeout(() => {
      for (const notice of latest.current) if (due(notice) <= Date.now() + 10) dismissNotice(notice.id)
    }, Math.max(0, nextDue - Date.now()))
    return () => window.clearTimeout(timer)
  }, [nextDue])

  // Coming back from a hold gives every notice its full time again.
  const wasPaused = useRef(false)
  useEffect(() => {
    if (wasPaused.current && !paused) renewNotices()
    wasPaused.current = paused
  }, [paused])

  // Escape takes away the notice that has focus, or the newest one that can go when nothing else open wants the key.
  const any = live.length > 0
  const liveRef = useRef(live)
  useEffect(() => { liveRef.current = live })
  useEffect(() => {
    if (!any) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return
      const focused = document.activeElement?.closest<HTMLElement>('[data-notice]')
      const typing = document.activeElement instanceof HTMLElement && document.activeElement.matches('input, textarea, select, [contenteditable]')
      if (!focused && (typing || anythingOpen())) return
      const target = focused ? liveRef.current.find(notice => notice.id === focused.dataset.notice) : liveRef.current.find(notice => !notice.lasting)
      if (!target || target.lasting) return
      event.preventDefault()
      event.stopPropagation()
      target.onDismiss?.()
      dismissNotice(target.id)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [any])

  return (
    <div className="exo-notices" role={any ? 'group' : undefined} aria-label={any ? 'Notifications' : undefined}
      onPointerEnter={() => setHeld(true)} onPointerLeave={() => setHeld(false)}
      onFocus={() => setHeld(true)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setHeld(false) }}>
      <Announcer notices={notices} />
      {notices.map(notice => <NoticeCard key={notice.key} notice={notice} />)}
    </div>
  )
})

const due = (notice: ShownNotice) => notice.at + stayFor(notice)

/** Whether Exo is out of sight (minimized, behind a game): nothing passes unread. Listens only while a notice is up. */
function useHidden(watch: boolean): boolean {
  const [hidden, setHidden] = useState(() => document.hidden)
  useEffect(() => {
    if (!watch) return
    const change = () => setHidden(document.hidden)
    change()
    document.addEventListener('visibilitychange', change)
    return () => document.removeEventListener('visibilitychange', change)
  }, [watch])
  return hidden
}

/** What each arrival says, once: polite for the rest, assertive for a failure. A wait's next percent is not an arrival. */
function Announcer({ notices }: { notices: ShownNotice[] }) {
  const said = useRef(new Map<string, string>())
  const [polite, setPolite] = useState('')
  const [assertive, setAssertive] = useState('')
  useEffect(() => {
    const live = notices.filter(notice => !notice.leaving)
    const heard = said.current
    for (const id of [...heard.keys()]) if (!live.some(notice => notice.id === id)) heard.delete(id)
    const calm: string[] = []
    const urgent: string[] = []
    for (const notice of live) {
      const signature = `${notice.tone ?? ''}|${notice.progress === undefined ? `${notice.title}|${notice.text}` : 'wait'}`
      if (heard.get(notice.id) === signature) continue
      heard.set(notice.id, signature)
      ;(notice.tone === 'bad' ? urgent : calm).push([notice.title, notice.where, notice.text].filter(Boolean).join('. '))
    }
    if (calm.length) setPolite(calm.join('. '))
    if (urgent.length) setAssertive(urgent.join('. '))
  }, [notices])
  return <>
    <div className="exo-sr-only" role="status" aria-live="polite" aria-atomic="true">{polite}</div>
    <div className="exo-sr-only" role="alert" aria-live="assertive" aria-atomic="true">{assertive}</div>
  </>
}

/** A swipe to the right takes a notice away; slower than a flick it must go a third of the way, and short of that it comes back. */
function useSwipe(enabled: boolean, onAway: () => void) {
  const drag = useRef<{ x: number; y: number; at: number; dx: number; active: boolean; id: number } | null>(null)
  const suppress = useRef(false)
  const away = useRef(onAway)
  useEffect(() => { away.current = onAway })
  if (!enabled) return {}
  const slotOf = (event: ReactPointerEvent) => event.currentTarget as HTMLElement
  return {
    onPointerDown: (event: ReactPointerEvent) => {
      suppress.current = false
      if (event.button !== 0 || (event.target as Element).closest('.exo-notice-end')) return
      drag.current = { x: event.clientX, y: event.clientY, at: event.timeStamp, dx: 0, active: false, id: event.pointerId }
    },
    onPointerMove: (event: ReactPointerEvent) => {
      const state = drag.current
      if (!state || event.pointerId !== state.id) return
      const dx = event.clientX - state.x
      if (!state.active) {
        if (Math.abs(dx) < 8 || Math.abs(dx) < Math.abs(event.clientY - state.y)) return
        state.active = true
        slotOf(event).setPointerCapture(event.pointerId)
        slotOf(event).dataset.drag = ''
      }
      state.dx = dx
      const slot = slotOf(event)
      slot.style.translate = `${dx > 0 ? dx : dx * 0.12}px 0`
      slot.style.opacity = String(Math.max(0.35, 1 - Math.max(dx, 0) / (slot.offsetWidth * 1.2)))
    },
    onPointerUp: (event: ReactPointerEvent) => {
      const state = drag.current
      drag.current = null
      if (!state?.active) return
      const slot = slotOf(event)
      suppress.current = true
      delete slot.dataset.drag
      const speed = state.dx / Math.max(1, event.timeStamp - state.at)
      if (state.dx > slot.offsetWidth / 3 || (state.dx > 24 && speed > 0.5)) {
        slot.style.translate = `${slot.offsetWidth + 16}px 0`
        slot.style.opacity = '0'
        away.current()
      } else {
        slot.style.translate = ''
        slot.style.opacity = ''
      }
    },
    onPointerCancel: (event: ReactPointerEvent) => {
      drag.current = null
      const slot = slotOf(event)
      delete slot.dataset.drag
      slot.style.translate = ''
      slot.style.opacity = ''
    },
    // The press that ended a drag is not a click on what it ended over.
    onClickCapture: (event: ReactMouseEvent) => {
      if (!suppress.current) return
      suppress.current = false
      event.preventDefault()
      event.stopPropagation()
    },
  }
}

function ActionButton({ action }: { action: NoticeAction }) {
  const className = cn('exo-set-btn is-compact', action.primary && 'is-primary', action.danger && 'is-danger')
  const lead = action.busy ? <Spinner label={action.label} /> : null
  if (action.confirm) {
    return (
      <ConfirmButton question={action.confirm} danger={action.danger} disabled={action.disabled || action.busy}
        className={className} lead={lead} onConfirm={action.onPress}>{action.label}</ConfirmButton>
    )
  }
  return <button type="button" disabled={action.disabled || action.busy} className={className} onClick={action.onPress}>{lead}{action.label}</button>
}

function NoticeWords({ notice }: { notice: ShownNotice }) {
  const glyph = notice.icon ?? <span className="exo-notice-glyph" aria-hidden>
    {notice.tone === 'bad' ? <Alert size={18} /> : notice.tone === 'good' ? <Check size={18} /> : <Bell size={18} />}
  </span>
  return <>
    {glyph}
    <span className="exo-notice-text">
      {notice.title ? <span className="exo-notice-head">
        <strong>{notice.title}</strong>
        {notice.where ? <span className="exo-notice-where">{notice.where}</span> : null}
        {notice.count && notice.count > 1 ? <span className="exo-notice-count">{notice.count} new</span> : null}
      </span> : null}
      <span className="exo-notice-body"><ProgressWords words={notice.text} value={notice.progress} /></span>
    </span>
  </>
}

function NoticeCard({ notice }: { notice: ShownNotice }) {
  const sticky = isSticky(notice)
  const close = () => { notice.onDismiss?.(); dismissNotice(notice.id) }
  const open = notice.onOpen ? () => { notice.onOpen?.(); dismissNotice(notice.id) } : null
  const swipe = useSwipe(!notice.lasting && !notice.leaving, close)
  return (
    <div className={cn('exo-notice-slot', notice.leaving && 'is-leaving')} inert={notice.leaving} data-notice={notice.id} {...swipe}>
      <div className={cn('exo-notice exo-glass is-panel', notice.tone && `is-${notice.tone}`, sticky && 'is-sticky')}>
        {open
          ? <button type="button" className="exo-notice-main" onClick={open}
              aria-label={`${notice.title ? `${notice.title}: ` : ''}${notice.text}. Open.`}><NoticeWords notice={notice} /></button>
          : <div className="exo-notice-main"><NoticeWords notice={notice} /></div>}
        {notice.action || !notice.lasting ? <div className="exo-notice-end">
          {notice.action ? <ActionButton action={notice.action} /> : null}
          {notice.lasting ? null : <button type="button" className="exo-notice-close" aria-label="Dismiss" onClick={close}>
            <Close size={14} />
          </button>}
        </div> : null}
        {notice.progress !== undefined ? <Progress edge value={notice.progress} label={notice.text} /> : null}
      </div>
    </div>
  )
}
