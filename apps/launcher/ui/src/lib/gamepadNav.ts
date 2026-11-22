import { focusableSelector } from './focusTrap.ts'
import { detectGamepadKind, pickSpatialTarget, stepSliderValue, type Direction } from './gamepadMath.ts'
import { activateGamepadModality, setGamepadPaused, shouldReduceMotion } from './motionPreference.ts'

export type GamepadNavHandlers = {
  onRoomStep: (delta: -1 | 1) => void
  onSearchToggle: () => void
  onSettingsToggle: () => void
  onBackFallback: () => void
}

const STICK_DEADZONE = 0.52
const SCROLL_DEADZONE = 0.26
const TRIGGER_THRESHOLD = 0.55
const REPEAT_INITIAL_MS = 340
const REPEAT_INTERVAL_MS = 105
const SCROLL_PX_PER_SEC = 760

const scopeMemory = new WeakMap<HTMLElement, HTMLElement>()

const SCOPE_SELECTORS = [
  '.exo-music-immersive',
  '.exo-game-page',
  '.exo-store-page-room',
  '.exo-set-room:not([hidden])',
  '.exo-devices.is-device',
]

function isVisibleElement(el: HTMLElement): boolean {
  if (el.hidden || el.getAttribute('aria-hidden') === 'true') return false
  if (el.closest('[hidden], [inert], .is-backstage')) return false
  const rect = el.getBoundingClientRect()
  if (rect.width < 2 || rect.height < 2) return false
  const style = getComputedStyle(el)
  return style.visibility !== 'hidden' && style.display !== 'none'
}

function isEnabledElement(el: HTMLElement): boolean {
  if (!isVisibleElement(el)) return false
  if ('disabled' in el && (el as HTMLButtonElement).disabled) return false
  return el.getAttribute('aria-disabled') !== 'true'
}

function rememberScopeFocus(scope: HTMLElement | null, target: HTMLElement) {
  if (!scope || !scope.contains(target)) return
  if ('disabled' in target && (target as HTMLButtonElement).disabled) return
  scopeMemory.set(scope, target)
}

function resolveActiveScope(): HTMLElement | null {
  const popovers = [...document.querySelectorAll<HTMLElement>('[popover]:popover-open')].filter(isVisibleElement)
  if (popovers.length > 0) return popovers[popovers.length - 1]
  const modals = [...document.querySelectorAll<HTMLElement>('[aria-modal="true"], [role="menu"], [role="dialog"]')].filter(isVisibleElement)
  if (modals.length > 0) return modals[modals.length - 1]
  for (const selector of SCOPE_SELECTORS) {
    const match = document.querySelector<HTMLElement>(selector)
    if (match && isVisibleElement(match)) return match
  }
  const rooms = [...document.querySelectorAll<HTMLElement>('.exo-room:not([hidden], .is-backstage)')].filter(isVisibleElement)
  return rooms[rooms.length - 1] ?? document.body
}

function focusablesIn(scope: HTMLElement): HTMLElement[] {
  return [...scope.querySelectorAll<HTMLElement>(focusableSelector)].filter(isEnabledElement)
}

function scrollTargetIntoView(el: HTMLElement) {
  const behavior: ScrollBehavior = shouldReduceMotion() ? 'instant' : 'smooth'
  const track = el.closest<HTMLElement>('[data-h-track]')
  if (track && typeof track.scrollTo === 'function') {
    const trackRect = track.getBoundingClientRect()
    const elRect = el.getBoundingClientRect()
    const nextLeft = Math.max(0, track.scrollLeft + (elRect.left - trackRect.left) - (trackRect.width - elRect.width) / 2)
    track.scrollTo({ left: nextLeft, behavior })
  }
  el.scrollIntoView({
    behavior,
    block: 'nearest',
    inline: 'center',
  })
}

function focusAndTrack(scope: HTMLElement, target: HTMLElement) {
  rememberScopeFocus(scope, target)
  target.focus({ preventScroll: true })
  scrollTargetIntoView(target)
}

const PREFERRED_FOCUS_SELECTORS = [
  '.exo-deck-card.is-active .exo-deck-card-btn',
  '.exo-game-page .exo-play:not(:disabled)',
  '.exo-sgp-cta .exo-play:not(:disabled)',
  '.exo-settings-nav-pages button[aria-current="page"]',
  '.exo-devices.is-device .exo-devices-back button',
  '.exo-device-card',
  '[data-device-card]',
  '.exo-store-hero-stage',
  '.exo-music-tile',
  '.exo-ai-head-actions button',
  '.exo-apps-pick',
  '.exo-tile button',
  '[data-autofocus]',
]

export function focusRoomPrimary(scope?: HTMLElement | null): boolean {
  const root = scope ?? resolveActiveScope()
  if (!root) return false
  const remembered = scopeMemory.get(root)
  if (remembered && remembered.isConnected && root.contains(remembered) && isEnabledElement(remembered)) {
    focusAndTrack(root, remembered)
    return true
  }
  for (const selector of PREFERRED_FOCUS_SELECTORS) {
    const match = root.querySelector<HTMLElement>(selector)
    if (match && isEnabledElement(match)) {
      focusAndTrack(root, match)
      return true
    }
  }
  const first = focusablesIn(root)[0]
  if (!first) return false
  focusAndTrack(root, first)
  return true
}

function adjustRangeInput(input: HTMLInputElement, delta: -1 | 1): boolean {
  const min = Number(input.min === '' ? 0 : input.min)
  const max = Number(input.max === '' ? 100 : input.max)
  const step = input.step && input.step !== 'any' ? Number(input.step) : null
  const current = Number(input.value)
  const next = stepSliderValue(min, max, step, Number.isFinite(current) ? current : min, delta)
  if (next === current) return true
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
  setter?.call(input, String(next))
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new Event('change', { bubbles: true }))
  return true
}

function moveHomePoster(scope: HTMLElement, active: HTMLElement, dir: 'left' | 'right'): boolean {
  const card = active.closest('.exo-deck-card')
  if (!card) return false
  const cards = [...(card.parentElement?.querySelectorAll<HTMLElement>('.exo-deck-card') ?? [])]
  const index = cards.indexOf(card as HTMLElement)
  if (index < 0) return false
  const next = cards[index + (dir === 'right' ? 1 : -1)]
  const button = next?.querySelector<HTMLElement>('.exo-deck-card-btn')
  if (!button) return true
  focusAndTrack(scope, button)
  return true
}

function moveFocus(dir: Direction) {
  const scope = resolveActiveScope()
  if (!scope) return
  const active = document.activeElement instanceof HTMLElement && scope.contains(document.activeElement)
    ? document.activeElement
    : null
  if (!active) {
    focusRoomPrimary(scope)
    return
  }
  if (active instanceof HTMLInputElement && active.type === 'range' && (dir === 'left' || dir === 'right')) {
    adjustRangeInput(active, dir === 'right' ? 1 : -1)
    return
  }
  if ((dir === 'left' || dir === 'right') && moveHomePoster(scope, active, dir)) return
  const fromRect = active.getBoundingClientRect()
  const candidates = focusablesIn(scope)
    .filter(el => el !== active)
    .map(item => ({ item, rect: item.getBoundingClientRect() }))
  const target = pickSpatialTarget(fromRect, candidates, dir)
  if (!target) return
  focusAndTrack(scope, target)
}

function cycleSubTabs(delta: -1 | 1) {
  const scope = resolveActiveScope()
  if (!scope) return
  const group = scope.querySelector<HTMLElement>('.exo-home-tabs, .exo-store-top .exo-shelf-chips, .exo-dc-tabs, .exo-settings-nav, .exo-shelf-chips, [role="tablist"]')
  if (!group || !isVisibleElement(group)) return
  const buttons = [...group.querySelectorAll<HTMLElement>('button:not(:disabled), [role="tab"]:not([aria-disabled="true"])')].filter(isVisibleElement)
  if (buttons.length < 2) return
  const current = buttons.findIndex(btn => btn.getAttribute('aria-pressed') === 'true' || btn.getAttribute('aria-selected') === 'true' || btn.getAttribute('aria-current') === 'page' || btn.classList.contains('is-on') || btn.classList.contains('is-active'))
  const nextIdx = current < 0 ? 0 : (current + delta + buttons.length) % buttons.length
  const next = buttons[nextIdx]
  next.click()
  focusAndTrack(scope, next)
}

function triggerPrimaryQuickAction() {
  const scope = resolveActiveScope()
  if (!scope) return
  const targets = [
    '.exo-home-play:not(:disabled)',
    '.exo-game-page .exo-play:not(:disabled)',
    '.exo-sgp-cta .exo-play:not(:disabled)',
    '.exo-music-immersive .exo-music-bar-play:not(:disabled)',
    '.exo-music-bar-play:not(:disabled)',
  ]
  for (const selector of targets) {
    const button = scope.querySelector<HTMLElement>(selector) ?? document.querySelector<HTMLElement>(selector)
    if (button && isVisibleElement(button)) {
      button.click()
      return
    }
  }
}

function triggerBack(onBackFallback: () => void) {
  const popovers = [...document.querySelectorAll<HTMLElement>('[popover]:popover-open')].filter(isVisibleElement)
  if (popovers.length > 0) {
    popovers[popovers.length - 1].hidePopover()
    return
  }
  const inSettings = !!document.querySelector('.exo-set-room:not([hidden], .is-backstage)')
  if (!inSettings) {
    const subBack = document.querySelector<HTMLElement>('.exo-game-back, .exo-devices-back button, .exo-music-back, .exo-st-back, .exo-sgp-back, .exo-library-back')
    if (subBack && isVisibleElement(subBack)) {
      subBack.click()
      requestAnimationFrame(() => focusRoomPrimary())
      return
    }
  }
  const esc = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
  const target = document.activeElement instanceof HTMLElement ? document.activeElement : document.body
  target.dispatchEvent(esc)
  if (!esc.defaultPrevented) {
    const settingsBack = inSettings ? document.querySelector<HTMLElement>('.exo-settings-back') : null
    if (settingsBack && isVisibleElement(settingsBack)) settingsBack.click()
    else onBackFallback()
  }
  if (inSettings) requestAnimationFrame(() => requestAnimationFrame(() => focusRoomPrimary()))
}

function triggerConfirm() {
  const scope = resolveActiveScope()
  if (!scope) return
  const active = document.activeElement instanceof HTMLElement && scope.contains(document.activeElement)
    ? document.activeElement
    : null
  if (!active) {
    focusRoomPrimary(scope)
    return
  }
  if (active instanceof HTMLInputElement && active.type !== 'checkbox' && active.type !== 'radio' && active.type !== 'range') {
    active.focus()
    return
  }
  active.click()
}

function scrollActiveContainer(rx: number, ry: number, dtSec: number) {
  const scope = resolveActiveScope()
  if (!scope) return
  const focused = document.activeElement instanceof HTMLElement && scope.contains(document.activeElement)
    ? document.activeElement
    : null
  const scroller = findScroller(focused, scope)
  if (!scroller) return
  if (Math.abs(ry) > SCROLL_DEADZONE) scroller.scrollTop += ry * SCROLL_PX_PER_SEC * dtSec
  if (Math.abs(rx) > SCROLL_DEADZONE) scroller.scrollLeft += rx * SCROLL_PX_PER_SEC * dtSec
}

function findScroller(start: HTMLElement | null, scope: HTMLElement): HTMLElement | null {
  let node: HTMLElement | null = start
  while (node && scope.contains(node)) {
    if (node.scrollHeight > node.clientHeight + 8 && /auto|scroll/.test(getComputedStyle(node).overflowY)) return node
    node = node.parentElement
  }
  const marked = scope.querySelector<HTMLElement>('[data-room-scroll], .exo-store-scroll, .exo-game-page-inner, .exo-music-main, .exo-library-pane')
  if (marked && isVisibleElement(marked) && marked.scrollHeight > marked.clientHeight + 8) return marked
  return scope.scrollHeight > scope.clientHeight + 8 ? scope : null
}

function isControllerLabActive(): boolean {
  const lab = document.querySelector<HTMLElement>('[data-controller-lab="active"]')
  if (!lab || lab.closest('[hidden], [inert], .is-backstage')) return false
  const box = lab.firstElementChild instanceof HTMLElement ? lab.firstElementChild : lab
  return isVisibleElement(box)
}

function readStickDirection(pad: Gamepad): Direction | null {
  const b = pad.buttons
  if (b[12]?.pressed) return 'up'
  if (b[13]?.pressed) return 'down'
  if (b[14]?.pressed) return 'left'
  if (b[15]?.pressed) return 'right'
  const lx = pad.axes[0] ?? 0
  const ly = pad.axes[1] ?? 0
  if (Math.abs(lx) < STICK_DEADZONE && Math.abs(ly) < STICK_DEADZONE) return null
  return Math.abs(lx) > Math.abs(ly) ? (lx > 0 ? 'right' : 'left') : (ly > 0 ? 'down' : 'up')
}

function readPressedMap(pad: Gamepad): Record<number, boolean> {
  const b = pad.buttons
  return {
    0: !!b[0]?.pressed,
    1: !!b[1]?.pressed,
    2: !!b[2]?.pressed,
    3: !!b[3]?.pressed,
    4: !!b[4]?.pressed,
    5: !!b[5]?.pressed,
    6: (b[6]?.value ?? 0) > TRIGGER_THRESHOLD || !!b[6]?.pressed,
    7: (b[7]?.value ?? 0) > TRIGGER_THRESHOLD || !!b[7]?.pressed,
    8: !!b[8]?.pressed,
    9: !!b[9]?.pressed,
  }
}

type PadLoopState = {
  prevButtons: Record<number, boolean>
  heldDir: Direction | null
  nextRepeatAt: number
}

function handleEdgeButton(index: number, handlers: GamepadNavHandlers) {
  if (index === 0) triggerConfirm()
  else if (index === 1) triggerBack(handlers.onBackFallback)
  else if (index === 2) triggerPrimaryQuickAction()
  else if (index === 3 || index === 8) handlers.onSearchToggle()
  else if (index === 4) { handlers.onRoomStep(-1); requestAnimationFrame(() => requestAnimationFrame(() => focusRoomPrimary())) }
  else if (index === 5) { handlers.onRoomStep(1); requestAnimationFrame(() => requestAnimationFrame(() => focusRoomPrimary())) }
  else if (index === 6) cycleSubTabs(-1)
  else if (index === 7) cycleSubTabs(1)
  else if (index === 9) handlers.onSettingsToggle()
}

function stepDirectionRepeat(dir: Direction | null, now: number, state: PadLoopState) {
  if (dir !== state.heldDir) {
    state.heldDir = dir
    if (dir) {
      moveFocus(dir)
      state.nextRepeatAt = now + REPEAT_INITIAL_MS
    }
    return
  }
  if (dir && now >= state.nextRepeatAt) {
    moveFocus(dir)
    state.nextRepeatAt = now + REPEAT_INTERVAL_MS
  }
}

function processActivePad(pad: Gamepad, now: number, dtSec: number, state: PadLoopState, handlers: GamepadNavHandlers) {
  const pressed = readPressedMap(pad)
  const dir = readStickDirection(pad)
  const rx = pad.axes[2] ?? 0
  const ry = pad.axes[3] ?? 0
  const scrolled = Math.abs(rx) > SCROLL_DEADZONE || Math.abs(ry) > SCROLL_DEADZONE
  if (dir !== null || scrolled || Object.values(pressed).some(Boolean)) {
    activateGamepadModality(detectGamepadKind(pad.id))
  }
  const labActive = isControllerLabActive()
  setGamepadPaused(labActive)
  if (labActive) {
    state.heldDir = null
    state.prevButtons = pressed
    return
  }
  for (const key of [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) {
    if (pressed[key] && !state.prevButtons[key]) handleEdgeButton(key, handlers)
  }
  stepDirectionRepeat(dir, now, state)
  if (scrolled) scrollActiveContainer(rx, ry, dtSec)
  state.prevButtons = pressed
}

export function installGamepadNav(handlers: GamepadNavHandlers): () => void {
  if (typeof window === 'undefined' || typeof navigator.getGamepads !== 'function') return () => { }
  let raf = 0
  let running = false
  let lastFrame = performance.now()
  const state: PadLoopState = { prevButtons: {}, heldDir: null, nextRepeatAt: 0 }

  const tick = (now: number) => {
    if (!running) return
    const dtSec = Math.min(0.05, Math.max(0.001, (now - lastFrame) / 1000))
    lastFrame = now
    const pads = [...(navigator.getGamepads() ?? [])].filter((p): p is Gamepad => !!p && p.connected)
    if (pads.length === 0) {
      running = false
      setGamepadPaused(false)
      state.prevButtons = {}
      state.heldDir = null
      return
    }
    if (document.hasFocus() && document.visibilityState === 'visible') {
      processActivePad(pads[0], now, dtSec, state, handlers)
    } else {
      setGamepadPaused(false)
      state.prevButtons = {}
      state.heldDir = null
    }
    raf = requestAnimationFrame(tick)
  }

  const start = () => {
    if (running) return
    running = true
    lastFrame = performance.now()
    raf = requestAnimationFrame(tick)
  }

  const onConnect = () => start()
  const onDisconnect = () => {
    const remaining = [...(navigator.getGamepads() ?? [])].some(p => !!p && p.connected)
    if (!remaining) {
      running = false
      cancelAnimationFrame(raf)
    }
  }

  const onFocusIn = (event: FocusEvent) => {
    if (!document.documentElement?.hasAttribute('data-exo-gamepad')) return
    const target = event.target
    if (!(target instanceof HTMLElement)) return
    const scope = resolveActiveScope()
    rememberScopeFocus(scope, target)
  }

  document.addEventListener('focusin', onFocusIn)
  window.addEventListener('gamepadconnected', onConnect)
  window.addEventListener('gamepaddisconnected', onDisconnect)
  if ([...(navigator.getGamepads() ?? [])].some(p => !!p && p.connected)) start()

  return () => {
    running = false
    setGamepadPaused(false)
    cancelAnimationFrame(raf)
    document.removeEventListener('focusin', onFocusIn)
    window.removeEventListener('gamepadconnected', onConnect)
    window.removeEventListener('gamepaddisconnected', onDisconnect)
  }
}
