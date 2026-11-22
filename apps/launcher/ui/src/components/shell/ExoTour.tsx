import '../../styles/tour.css'
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ComponentType } from 'react'
import { createPortal } from 'react-dom'
import {
  Burst,
  Chats,
  ChevronLeft,
  ChevronRight,
  Close,
  Library,
  Mouse,
  MusicNotes,
  New,
  Search,
  Settings,
  StoreBag,
  type IconProps,
} from '../../brand/icons'
import { placeTourCard, TOUR_STEPS, type RectBox, type TourIconName } from '../../lib/tour'
import type { View } from './TitleBar'

const STEP_ICONS: Record<TourIconName, ComponentType<IconProps>> = {
  new: New,
  library: Library,
  search: Search,
  store: StoreBag,
  discord: Chats,
  music: MusicNotes,
  devices: Mouse,
  exoai: Burst,
  settings: Settings,
}

const PAD = 6

function readTargetBox(selector: string | null): { box: RectBox; pill: boolean } | null {
  if (!selector) return null
  const el = document.querySelector<HTMLElement>(selector)
  if (!el) return null
  const rect = el.getBoundingClientRect()
  if (rect.width < 4 || rect.height < 4) return null
  const radius = parseFloat(getComputedStyle(el).borderRadius) || 0
  return {
    box: {
      left: Math.max(4, Math.round(rect.left - PAD)),
      top: Math.max(4, Math.round(rect.top - PAD)),
      width: Math.round(rect.width + PAD * 2),
      height: Math.round(rect.height + PAD * 2),
    },
    pill: radius >= rect.height / 2 - 2,
  }
}

export function ExoTour({
  open,
  step,
  currentView,
  onStep,
  onNavigate,
  onBeforeNavigate,
  onClose,
}: {
  open: boolean
  step: number
  currentView: View
  onStep: (next: number) => void
  onNavigate: (view: View) => Promise<void> | void
  onBeforeNavigate?: () => void
  onClose: () => void
}) {
  const cardRef = useRef<HTMLDivElement>(null)
  const [spot, setSpot] = useState<{ box: RectBox; pill: boolean } | null>(null)
  const [cardPos, setCardPos] = useState<{ left: number; top: number }>({ left: 20, top: 20 })
  const current = TOUR_STEPS[Math.min(Math.max(step, 0), TOUR_STEPS.length - 1)]
  const total = TOUR_STEPS.length
  const isFirst = step === 0
  const isLast = step === total - 1

  const finish = useCallback(() => {
    onClose()
  }, [onClose])

  const next = useCallback(() => {
    if (isLast) {
      finish()
      return
    }
    onStep(step + 1)
  }, [finish, isLast, onStep, step])

  const back = useCallback(() => {
    if (step > 0) onStep(step - 1)
  }, [onStep, step])

  useEffect(() => {
    if (!open) return
    if (current.room !== currentView) {
      onBeforeNavigate?.()
      void onNavigate(current.room)
    }
  }, [open, current.room, currentView, onBeforeNavigate, onNavigate])

  useLayoutEffect(() => {
    if (!open) return
    const measure = () => {
      const target = readTargetBox(current.selector)
      setSpot(target)
      const cardRect = cardRef.current?.getBoundingClientRect()
      const cardW = cardRect && cardRect.width > 0 ? cardRect.width : Math.min(408, window.innerWidth - 32)
      const cardH = cardRect && cardRect.height > 0 ? cardRect.height : 240
      const placed = placeTourCard(
        target?.box ?? null,
        { width: cardW, height: cardH },
        { width: window.innerWidth, height: window.innerHeight },
      )
      setCardPos({ left: placed.left, top: placed.top })
    }
    measure()
    const raf = window.requestAnimationFrame(measure)
    const settle = window.setTimeout(measure, 240)
    const late = window.setTimeout(measure, 520)
    window.addEventListener('resize', measure)
    const observer = cardRef.current ? new ResizeObserver(measure) : null
    if (cardRef.current) observer?.observe(cardRef.current)
    return () => {
      window.cancelAnimationFrame(raf)
      window.clearTimeout(settle)
      window.clearTimeout(late)
      window.removeEventListener('resize', measure)
      observer?.disconnect()
    }
  }, [open, step, current.selector, currentView])

  useEffect(() => {
    if (!open) return
    const frame = window.requestAnimationFrame(() => cardRef.current?.focus({ preventScroll: true }))
    return () => window.cancelAnimationFrame(frame)
  }, [open, step])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        finish()
        return
      }
      if (event.key === 'ArrowRight') {
        event.preventDefault()
        event.stopPropagation()
        next()
        return
      }
      if (event.key === 'ArrowLeft') {
        event.preventDefault()
        event.stopPropagation()
        back()
        return
      }
      if (event.key === 'Tab' && cardRef.current) {
        const stops = Array.from(cardRef.current.querySelectorAll<HTMLElement>('button:not(:disabled)'))
        const edge = event.shiftKey ? stops[0] : stops[stops.length - 1]
        if (!edge || document.activeElement === edge || !cardRef.current.contains(document.activeElement)) {
          event.preventDefault()
          ;(event.shiftKey ? stops[stops.length - 1] : stops[0])?.focus()
        }
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [open, finish, next, back])

  if (!open) return null

  const Icon = STEP_ICONS[current.icon]

  return createPortal(
    <div className="exo-tour-layer exo-portal">
      {spot ? (
        <div
          key={`spot-${current.id}`}
          className={`exo-tour-spot${spot.pill ? ' is-pill' : ''}`}
          style={spot.box}
          aria-hidden="true"
        />
      ) : (
        <div className="exo-tour-scrim" aria-hidden="true" />
      )}
      <div
        ref={cardRef}
        key={`card-${current.id}`}
        className="exo-glass is-panel exo-tour-card"
        style={cardPos}
        role="dialog"
        aria-modal="true"
        aria-labelledby="exo-tour-title"
        aria-describedby="exo-tour-body"
        tabIndex={-1}
      >
        <div className="exo-tour-head">
          <span className="exo-tour-badge" aria-hidden="true">
            <Icon size={18} />
          </span>
          <h2 id="exo-tour-title" className="exo-tour-title">{current.title}</h2>
          <span className="exo-tour-count">{step + 1} / {total}</span>
          <button type="button" className="exo-tour-close" aria-label="Skip tour" onClick={finish}>
            <Close size={14} />
          </button>
        </div>
        <p id="exo-tour-body" className="exo-tour-body">{current.body}</p>
        {current.shortcut || current.hint ? (
          <div className="exo-tour-meta">
            {current.shortcut ? <kbd className="exo-tour-key">{current.shortcut}</kbd> : null}
            {current.hint ? <span>{current.hint}</span> : null}
          </div>
        ) : null}
        <div className="exo-tour-dots" role="tablist" aria-label="Tour steps">
          {TOUR_STEPS.map((item, index) => (
            <button
              key={item.id}
              type="button"
              role="tab"
              className="exo-tour-dot"
              aria-selected={index === step}
              aria-label={`Step ${index + 1}: ${item.title}`}
              onClick={() => onStep(index)}
            />
          ))}
        </div>
        <div className="exo-tour-foot">
          <button type="button" className="exo-set-btn" onClick={finish}>
            Skip
          </button>
          <div className="exo-tour-actions">
            {!isFirst ? (
              <button type="button" className="exo-set-btn" onClick={back}>
                <ChevronLeft size={14} />
                Back
              </button>
            ) : null}
            <button type="button" className="exo-set-btn is-primary" onClick={next}>
              {isFirst ? 'Start tour' : isLast ? 'Done' : 'Next'}
              {!isLast ? <ChevronRight size={14} /> : null}
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  )
}
