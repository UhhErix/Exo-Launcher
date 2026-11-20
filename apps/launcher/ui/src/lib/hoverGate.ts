/**
 * Suppresses synthetic pointerenter events caused by a room switch or view transition mounting or revealing controls
 * directly beneath a stationary cursor. Genuine cursor motion or elapsing past the grace window unlocks hover again.
 */

const HOVER_GRACE_MS = 450
const MOVE_THRESHOLD_PX = 2

let blockedUntil = 0
let lastPointer: { x: number; y: number } | null = null
let movedSinceSuppress = false
let listening = false

function ensureListener() {
  if (listening || typeof window === 'undefined') return
  listening = true
  window.addEventListener('pointerdown', (event) => {
    lastPointer = { x: event.clientX, y: event.clientY }
  }, { capture: true, passive: true })
  window.addEventListener('pointermove', (event) => {
    const prev = lastPointer
    lastPointer = { x: event.clientX, y: event.clientY }
    if (!prev) {
      if (event.pointerType !== 'touch' && Math.hypot(event.movementX ?? 0, event.movementY ?? 0) >= MOVE_THRESHOLD_PX) {
        blockedUntil = 0
        movedSinceSuppress = true
      }
      return
    }
    if (Math.hypot(event.clientX - prev.x, event.clientY - prev.y) >= MOVE_THRESHOLD_PX) {
      blockedUntil = 0
      movedSinceSuppress = true
    }
  }, { capture: true, passive: true })
}

/** Arms the hover gate across a room/view transition until the pointer genuinely moves or the grace window passes. */
export function suppressHover(ms = HOVER_GRACE_MS, now = Date.now()) {
  ensureListener()
  movedSinceSuppress = false
  blockedUntil = Math.max(blockedUntil, now + ms)
}

/** Extends transition-end suppression only while the pointer has not genuinely moved since the transition began. */
export function extendHoverSuppression(ms = 180, now = Date.now()) {
  ensureListener()
  if (movedSinceSuppress) return
  blockedUntil = Math.max(blockedUntil, now + ms)
}

/** Returns true only when pointer hover should open previews/tooltips (not synthetic post-transition hover). */
export function allowHover(event?: { pointerType?: string; clientX?: number; clientY?: number }, now = Date.now()): boolean {
  ensureListener()
  if (event?.pointerType === 'touch') return false
  if (now >= blockedUntil) return true
  if (event && typeof event.clientX === 'number' && typeof event.clientY === 'number' && lastPointer) {
    if (Math.hypot(event.clientX - lastPointer.x, event.clientY - lastPointer.y) >= MOVE_THRESHOLD_PX) {
      blockedUntil = 0
      movedSinceSuppress = true
      lastPointer = { x: event.clientX, y: event.clientY }
      return true
    }
  }
  return false
}

/** Resets gate state for tests. */
export function resetHoverGate() {
  blockedUntil = 0
  lastPointer = null
  movedSinceSuppress = false
  listening = false
}

if (typeof window !== 'undefined') ensureListener()
