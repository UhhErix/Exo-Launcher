import type { GamepadKind } from './gamepadMath'

const GAMEPAD_EVENT = 'exo-gamepad-modality'
const GAMEPAD_PAUSED_EVENT = 'exo-gamepad-paused'
let keyboardNavigating = false

export function shouldReduceMotion(): boolean {
  if (typeof document !== 'undefined') {
    const preference = document.documentElement.dataset.exoMotion
    if (preference === 'reduce') return true
    if (preference === 'full') return false
  }
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

export function getGamepadModality(): GamepadKind | null {
  if (typeof document === 'undefined') return null
  const kind = document.documentElement.dataset.exoGamepad
  return kind === 'xbox' || kind === 'playstation' || kind === 'nintendo' ? kind : null
}

export function activateGamepadModality(kind: GamepadKind): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  keyboardNavigating = true
  if (root.dataset.exoInput !== 'key') root.dataset.exoInput = 'key'
  if (root.dataset.exoGamepad !== kind) {
    root.dataset.exoGamepad = kind
    window.dispatchEvent(new CustomEvent(GAMEPAD_EVENT, { detail: kind }))
  }
}

export function clearGamepadModality(): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  if (root.dataset.exoGamepad) {
    delete root.dataset.exoGamepad
    window.dispatchEvent(new CustomEvent(GAMEPAD_EVENT, { detail: null }))
  }
}

export function onGamepadModalityChange(listener: (kind: GamepadKind | null) => void): () => void {
  if (typeof window === 'undefined') return () => { }
  const handler = (event: Event) => listener((event as CustomEvent<GamepadKind | null>).detail ?? null)
  window.addEventListener(GAMEPAD_EVENT, handler)
  return () => window.removeEventListener(GAMEPAD_EVENT, handler)
}

export function getGamepadPaused(): boolean {
  return typeof document !== 'undefined' && document.documentElement.dataset.exoGamepadPaused === '1'
}

export function setGamepadPaused(paused: boolean): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  const current = root.dataset.exoGamepadPaused === '1'
  if (current === paused) return
  if (paused) root.dataset.exoGamepadPaused = '1'
  else delete root.dataset.exoGamepadPaused
  window.dispatchEvent(new CustomEvent(GAMEPAD_PAUSED_EVENT, { detail: paused }))
}

export function onGamepadPausedChange(listener: (paused: boolean) => void): () => void {
  if (typeof window === 'undefined') return () => { }
  const handler = (event: Event) => listener(Boolean((event as CustomEvent<boolean>).detail))
  window.addEventListener(GAMEPAD_PAUSED_EVENT, handler)
  return () => window.removeEventListener(GAMEPAD_PAUSED_EVENT, handler)
}

export function installInputModality() {
  const root = document.documentElement
  const set = (input: 'key' | 'pointer') => { if (root.dataset.exoInput !== input) root.dataset.exoInput = input }
  window.addEventListener('keydown', event => {
    if (/^(Tab|Arrow|Home$|End$|Page)/.test(event.key)) keyboardNavigating = true
    if (event.isTrusted) clearGamepadModality()
    set('key')
  }, { capture: true, passive: true })
  window.addEventListener('pointerdown', () => {
    keyboardNavigating = false
    clearGamepadModality()
    set('pointer')
  }, { capture: true, passive: true })
  const focus = HTMLElement.prototype.focus
  HTMLElement.prototype.focus = function (this: HTMLElement, options?: FocusOptions) {
    focus.call(this, keyboardNavigating ? options : { ...options, focusVisible: false } as FocusOptions)
  }
}

export const fromKeyboard = () => typeof document !== 'undefined' && document.documentElement.dataset.exoInput === 'key'
