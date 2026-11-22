import test from 'node:test'
import assert from 'node:assert/strict'
import {
  detectGamepadKind,
  gamepadLabels,
  pickSpatialTarget,
  scoreDirectionCandidate,
  stepSliderValue,
  type SpatialRect,
} from '../src/lib/gamepadMath.ts'
import { focusRoomPrimary, installGamepadNav } from '../src/lib/gamepadNav.ts'

const box = (left: number, top: number, width: number, height: number): SpatialRect => ({
  left,
  top,
  right: left + width,
  bottom: top + height,
  width,
  height,
})

test('detectGamepadKind identifies PlayStation, Nintendo, and Xbox controllers', () => {
  assert.equal(detectGamepadKind('DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)'), 'playstation')
  assert.equal(detectGamepadKind('Sony Interactive Entertainment Wireless Controller'), 'playstation')
  assert.equal(detectGamepadKind('Pro Controller (Vendor: 057e Product: 2009)'), 'nintendo')
  assert.equal(detectGamepadKind('Xbox 360 Controller (XInput STANDARD GAMEPAD)'), 'xbox')
  assert.equal(detectGamepadKind('Generic USB Gamepad'), 'xbox')
})

test('gamepadLabels returns native button legends for each family', () => {
  assert.equal(gamepadLabels('playstation').confirm, '✕')
  assert.equal(gamepadLabels('playstation').action, '◻')
  assert.equal(gamepadLabels('playstation').prevRoom, 'L1')
  assert.equal(gamepadLabels('nintendo').prevTab, 'ZL')
  assert.equal(gamepadLabels('xbox').confirm, 'A')
  assert.equal(gamepadLabels('xbox').nextRoom, 'RB')
  assert.equal(gamepadLabels('xbox').view, 'View')
})

test('pickSpatialTarget prefers aligned items on the same row or column', () => {
  const origin = box(100, 100, 160, 240)
  const candidates = [
    { item: 'right-same-row', rect: box(280, 100, 160, 240) },
    { item: 'right-farther', rect: box(460, 100, 160, 240) },
    { item: 'right-off-row', rect: box(270, 380, 160, 240) },
    { item: 'above', rect: box(100, 20, 120, 40) },
    { item: 'below', rect: box(110, 380, 160, 240) },
  ]
  assert.equal(pickSpatialTarget(origin, candidates, 'right'), 'right-same-row')
  assert.equal(pickSpatialTarget(origin, candidates, 'up'), 'above')
  assert.equal(pickSpatialTarget(origin, candidates, 'down'), 'below')
  assert.equal(pickSpatialTarget(origin, candidates, 'left'), null)
})

test('pickSpatialTarget prefers cross-axis aligned column card over closer diagonal card', () => {
  const origin = box(100, 100, 160, 120)
  const candidates = [
    { item: 'diag-closer-y', rect: box(310, 240, 160, 120) },
    { item: 'same-col-below', rect: box(100, 280, 160, 120) },
  ]
  assert.equal(pickSpatialTarget(origin, candidates, 'down'), 'same-col-below')
})

test('scoreDirectionCandidate rejects opposite-direction candidates', () => {
  const origin = box(300, 200, 100, 40)
  const leftOf = box(120, 200, 100, 40)
  assert.equal(scoreDirectionCandidate(origin, leftOf, 'right'), null)
  assert.notEqual(scoreDirectionCandidate(origin, leftOf, 'left'), null)
})

test('stepSliderValue increments cleanly and clamps within bounds', () => {
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 0.4, 1), 0.5)
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 0.1, -1), 0.1)
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 4.0, 1), 4.0)
  assert.equal(stepSliderValue(0, 100, null, 50, 1), 55)
})

function splitTopLevel(input: string, delimiter: ',' | ' '): string[] {
  const parts: string[] = []
  let paren = 0
  let bracket = 0
  let start = 0
  for (let i = 0; i < input.length; i += 1) {
    const ch = input[i]
    if (ch === '(') paren += 1
    else if (ch === ')') paren = Math.max(0, paren - 1)
    else if (ch === '[') bracket += 1
    else if (ch === ']') bracket = Math.max(0, bracket - 1)
    else if (paren === 0 && bracket === 0 && (delimiter === ',' ? ch === ',' : /\s/.test(ch))) {
      const slice = input.slice(start, i).trim()
      if (slice) parts.push(slice)
      start = i + 1
    }
  }
  const tail = input.slice(start).trim()
  if (tail) parts.push(tail)
  return parts
}

function findMatchingParen(input: string, openIdx: number): number {
  let depth = 0
  for (let i = openIdx; i < input.length; i += 1) {
    if (input[i] === '(') depth += 1
    else if (input[i] === ')') {
      depth -= 1
      if (depth === 0) return i
    }
  }
  return -1
}

type SimpleAttrs = Record<string, string | undefined>

class FakeElement {
  tagName: string
  classList: Set<string>
  attrs: SimpleAttrs = {}
  dataset: Record<string, string | undefined> = {}
  children: FakeElement[] = []
  parentElement: FakeElement | null = null
  hidden = false
  disabled = false
  isConnected = true
  scrollLeft = 0
  scrollTop = 0
  scrollHeight = 100
  clientHeight = 100
  rect = { left: 0, top: 0, width: 200, height: 60, right: 200, bottom: 60 }
  scrollToCalls: Array<{ left?: number; top?: number; behavior?: ScrollBehavior }> = []
  scrollIntoViewCalls = 0
  clicked = 0
  private ownerDoc: FakeDocument

  constructor(doc: FakeDocument, tagName: string, classes: string[] = [], attrs: SimpleAttrs = {}) {
    this.ownerDoc = doc
    this.tagName = tagName.toUpperCase()
    this.classList = new Set(classes)
    this.attrs = { ...attrs }
  }

  append(...nodes: FakeElement[]): this {
    for (const node of nodes) {
      node.parentElement = this
      node.isConnected = true
      this.children.push(node)
    }
    return this
  }

  getAttribute(name: string): string | null {
    if (name === 'data-exo-gamepad') return this.dataset.exoGamepad ?? this.attrs[name] ?? null
    return this.attrs[name] ?? null
  }

  hasAttribute(name: string): boolean {
    if (name === 'hidden') return this.hidden
    if (name === 'disabled') return this.disabled
    if (name === 'data-exo-gamepad') return this.dataset.exoGamepad !== undefined || this.attrs[name] !== undefined
    return name in this.attrs && this.attrs[name] !== undefined
  }

  getBoundingClientRect() {
    return this.rect
  }

  focus() {
    this.ownerDoc.activeElement = this
    this.ownerDoc.dispatch('focusin', { target: this })
  }

  click() {
    this.clicked += 1
  }

  scrollTo(opts: { left?: number; top?: number; behavior?: ScrollBehavior }) {
    this.scrollToCalls.push(opts)
    if (typeof opts.left === 'number') this.scrollLeft = opts.left
  }

  scrollIntoView() {
    this.scrollIntoViewCalls += 1
  }

  contains(node: unknown): boolean {
    if (!(node instanceof FakeElement)) return false
    let cur: FakeElement | null = node
    while (cur) {
      if (cur === this) return true
      cur = cur.parentElement
    }
    return false
  }

  matchesSingle(part: string): boolean {
    const s = part.trim()
    if (!s) return false
    const tokens = splitTopLevel(s, ' ')
    if (tokens.length > 1) {
      const leaf = tokens[tokens.length - 1]
      if (!this.matchesSingle(leaf)) return false
      let cur = this.parentElement
      let idx = tokens.length - 2
      while (cur && idx >= 0) {
        if (cur.matchesSingle(tokens[idx])) idx -= 1
        cur = cur.parentElement
      }
      return idx < 0
    }
    let rest = s
    const tagMatch = rest.match(/^[a-zA-Z0-9_-]+/)
    if (tagMatch) {
      if (this.tagName !== tagMatch[0].toUpperCase()) return false
      rest = rest.slice(tagMatch[0].length)
    }
    while (rest.length > 0) {
      if (rest.startsWith(':not(')) {
        const end = findMatchingParen(rest, 4)
        if (end < 0) return false
        const inner = rest.slice(5, end)
        if (this.matches(inner)) return false
        rest = rest.slice(end + 1)
      } else if (rest.startsWith(':popover-open')) {
        if (!this.hasAttribute('popover-open')) return false
        rest = rest.slice(':popover-open'.length)
      } else if (rest.startsWith(':disabled')) {
        if (!this.disabled) return false
        rest = rest.slice(':disabled'.length)
      } else if (rest.startsWith('.')) {
        const m = rest.match(/^\.([a-zA-Z0-9_-]+)/)
        if (!m || !this.classList.has(m[1])) return false
        rest = rest.slice(m[0].length)
      } else if (rest.startsWith('[')) {
        const end = rest.indexOf(']')
        if (end < 0) return false
        const inside = rest.slice(1, end)
        const eq = inside.indexOf('=')
        if (eq >= 0) {
          const k = inside.slice(0, eq)
          const v = inside.slice(eq + 1).replace(/^"|"$/g, '')
          if (this.getAttribute(k) !== v) return false
        } else if (!this.hasAttribute(inside)) {
          return false
        }
        rest = rest.slice(end + 1)
      } else {
        return false
      }
    }
    return true
  }

  matches(selector: string): boolean {
    return splitTopLevel(selector, ',').some(part => this.matchesSingle(part))
  }

  closest<T extends FakeElement = FakeElement>(selector: string): T | null {
    let cur: FakeElement | null = this
    while (cur) {
      if (cur.matches(selector)) return cur as T
      cur = cur.parentElement
    }
    return null
  }

  querySelector<T extends FakeElement = FakeElement>(selector: string): T | null {
    return this.querySelectorAll<T>(selector)[0] ?? null
  }

  querySelectorAll<T extends FakeElement = FakeElement>(selector: string): T[] {
    const out: T[] = []
    const walk = (node: FakeElement) => {
      for (const child of node.children) {
        if (child.matches(selector)) out.push(child as T)
        walk(child)
      }
    }
    walk(this)
    return out
  }
}

class FakeDocument {
  documentElement: FakeElement
  body: FakeElement
  activeElement: FakeElement | null = null
  visibilityState = 'visible'
  private listeners = new Map<string, Set<(e: unknown) => void>>()

  constructor() {
    this.documentElement = new FakeElement(this, 'html')
    this.body = new FakeElement(this, 'body')
    this.documentElement.append(this.body)
  }

  hasFocus() {
    return true
  }

  addEventListener(type: string, fn: (e: unknown) => void) {
    let set = this.listeners.get(type)
    if (!set) {
      set = new Set()
      this.listeners.set(type, set)
    }
    set.add(fn)
  }

  removeEventListener(type: string, fn: (e: unknown) => void) {
    this.listeners.get(type)?.delete(fn)
  }

  dispatch(type: string, event: unknown) {
    for (const fn of this.listeners.get(type) ?? []) fn(event)
  }

  querySelector(selector: string) {
    return this.body.querySelector(selector)
  }

  querySelectorAll(selector: string) {
    return this.body.querySelectorAll(selector)
  }
}

function makePad(buttons: Record<number, boolean>, axes = [0, 0, 0, 0]): Gamepad {
  const btns = Array.from({ length: 16 }, (_, i) => ({
    pressed: Boolean(buttons[i]),
    touched: Boolean(buttons[i]),
    value: buttons[i] ? 1 : 0,
  }))
  return {
    id: 'Xbox 360 Controller',
    index: 0,
    connected: true,
    timestamp: 0,
    mapping: 'standard',
    axes,
    buttons: btns as unknown as GamepadButton[],
    hapticActuators: [],
    vibrationActuator: null as unknown as GamepadHapticActuator,
  }
}

function withGamepadDom(run: (ctx: {
  doc: FakeDocument
  setPad: (pad: Gamepad | null) => void
  stepFrame: (ms?: number) => void
}) => void) {
  const prev = {
    document: globalThis.document,
    window: globalThis.window,
    navigator: Object.getOwnPropertyDescriptor(globalThis, 'navigator'),
    HTMLElement: globalThis.HTMLElement,
    HTMLInputElement: globalThis.HTMLInputElement,
    getComputedStyle: globalThis.getComputedStyle,
    requestAnimationFrame: globalThis.requestAnimationFrame,
    cancelAnimationFrame: globalThis.cancelAnimationFrame,
    CustomEvent: globalThis.CustomEvent,
  }

  const doc = new FakeDocument()
  let activePad: Gamepad | null = makePad({})
  const rafQueue: Array<(now: number) => void> = []
  let now = 100
  const winListeners = new Map<string, Set<(e: unknown) => void>>()

  const win = {
    addEventListener(type: string, fn: (e: unknown) => void) {
      let set = winListeners.get(type)
      if (!set) {
        set = new Set()
        winListeners.set(type, set)
      }
      set.add(fn)
    },
    removeEventListener(type: string, fn: (e: unknown) => void) {
      winListeners.get(type)?.delete(fn)
    },
    dispatchEvent() {
      return true
    },
    matchMedia() {
      return { matches: false }
    },
  }

  Object.assign(globalThis, {
    document: doc,
    window: win,
    HTMLElement: FakeElement,
    HTMLInputElement: class extends FakeElement {},
    getComputedStyle: () => ({ visibility: 'visible', display: 'block', overflowY: 'auto', overflowX: 'auto' }),
    requestAnimationFrame: (cb: (ts: number) => void) => {
      rafQueue.push(cb)
      return rafQueue.length
    },
    cancelAnimationFrame: () => {},
    CustomEvent: class {
      type: string
      detail: unknown
      constructor(type: string, init?: { detail?: unknown }) {
        this.type = type
        this.detail = init?.detail
      }
    },
  })
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true,
    value: {
      getGamepads: () => (activePad ? [activePad] : []),
    },
  })

  const stepFrame = (dt = 16) => {
    now += dt
    const current = rafQueue.splice(0, rafQueue.length)
    for (const cb of current) cb(now)
  }

  try {
    run({ doc, setPad: pad => { activePad = pad }, stepFrame })
  } finally {
    globalThis.document = prev.document
    globalThis.window = prev.window
    globalThis.HTMLElement = prev.HTMLElement
    globalThis.HTMLInputElement = prev.HTMLInputElement
    globalThis.getComputedStyle = prev.getComputedStyle
    globalThis.requestAnimationFrame = prev.requestAnimationFrame
    globalThis.cancelAnimationFrame = prev.cancelAnimationFrame
    globalThis.CustomEvent = prev.CustomEvent
    if (prev.navigator) Object.defineProperty(globalThis, 'navigator', prev.navigator)
  }
}

test('focusRoomPrimary resolves active room scopes and restores WeakMap focus memory', () => {
  withGamepadDom(({ doc }) => {
    const libraryRoom = new FakeElement(doc, 'section', ['exo-room'])
    const libPlay = new FakeElement(doc, 'button')
    const deckTrack = new FakeElement(doc, 'div', ['exo-deck-track'], { 'data-h-track': '' })
    deckTrack.rect = { left: 0, top: 0, width: 600, height: 300, right: 600, bottom: 300 }
    const card0 = new FakeElement(doc, 'div', ['exo-deck-card', 'is-active'])
    const card0Btn = new FakeElement(doc, 'button', ['exo-deck-card-btn'])
    card0Btn.rect = { left: 450, top: 40, width: 160, height: 220, right: 610, bottom: 260 }
    const card1 = new FakeElement(doc, 'div', ['exo-deck-card'])
    const card1Btn = new FakeElement(doc, 'button', ['exo-deck-card-btn'])
    card1Btn.rect = { left: 650, top: 40, width: 160, height: 220, right: 810, bottom: 260 }
    card0.append(card0Btn)
    card1.append(card1Btn)
    deckTrack.append(card0, card1)
    libraryRoom.append(libPlay, deckTrack)

    const gamePage = new FakeElement(doc, 'article', ['exo-game-page'])
    const playBtn = new FakeElement(doc, 'button', ['exo-play'])
    const secondBtn = new FakeElement(doc, 'button')
    gamePage.append(playBtn, secondBtn)

    const storePage = new FakeElement(doc, 'div', ['exo-store-page-room'])
    const sgpCta = new FakeElement(doc, 'div', ['exo-sgp-cta'])
    const sgpPlay = new FakeElement(doc, 'button', ['exo-play'])
    sgpCta.append(sgpPlay)
    storePage.append(sgpCta)

    const settingsRoom = new FakeElement(doc, 'section', ['exo-room', 'exo-set-room'])
    const nav = new FakeElement(doc, 'div', ['exo-settings-nav-pages'])
    const navGeneral = new FakeElement(doc, 'button', [], { 'aria-current': 'page' })
    const navAbout = new FakeElement(doc, 'button')
    nav.append(navGeneral, navAbout)
    settingsRoom.append(nav)

    const devicesSub = new FakeElement(doc, 'main', ['exo-devices', 'is-device'])
    const devBackWrap = new FakeElement(doc, 'header', ['exo-devices-back'])
    const devBack = new FakeElement(doc, 'button')
    devBackWrap.append(devBack)
    devicesSub.append(devBackWrap)

    const cleanup = installGamepadNav({
      onRoomStep: () => {},
      onSearchToggle: () => {},
      onSettingsToggle: () => {},
      onBackFallback: () => {},
    })
    try {
      doc.body.append(libraryRoom)
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, card0Btn)
      assert.ok(deckTrack.scrollToCalls.length > 0)

      doc.body.append(gamePage)
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, playBtn)
      doc.documentElement.dataset.exoGamepad = 'xbox'
      secondBtn.focus()
      gamePage.hidden = true

      doc.body.append(storePage)
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, sgpPlay)
      storePage.hidden = true

      settingsRoom.hidden = false
      doc.body.append(settingsRoom)
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, navGeneral)
      settingsRoom.hidden = true

      doc.body.append(devicesSub)
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, devBack)
      devicesSub.hidden = true

      gamePage.hidden = false
      assert.equal(focusRoomPrimary(), true)
      assert.equal(doc.activeElement, secondBtn)
    } finally {
      cleanup()
    }
  })
})

test('initial D-pad focuses item 0 without skipping and maps View, Start, LB/RB', () => {
  withGamepadDom(({ doc, setPad, stepFrame }) => {
    const room = new FakeElement(doc, 'section', ['exo-room'])
    const track = new FakeElement(doc, 'div', ['exo-deck-track'], { 'data-h-track': '' })
    track.rect = { left: 0, top: 0, width: 600, height: 300, right: 600, bottom: 300 }
    const card0 = new FakeElement(doc, 'div', ['exo-deck-card', 'is-active'])
    const btn0 = new FakeElement(doc, 'button', ['exo-deck-card-btn'])
    btn0.rect = { left: 20, top: 20, width: 140, height: 220, right: 160, bottom: 240 }
    const card1 = new FakeElement(doc, 'div', ['exo-deck-card'])
    const btn1 = new FakeElement(doc, 'button', ['exo-deck-card-btn'])
    btn1.rect = { left: 200, top: 20, width: 140, height: 220, right: 340, bottom: 240 }
    card0.append(btn0)
    card1.append(btn1)
    track.append(card0, card1)
    room.append(track)
    doc.body.append(room)

    const calls: string[] = []
    const cleanup = installGamepadNav({
      onRoomStep: d => calls.push(`room:${d}`),
      onSearchToggle: () => calls.push('search'),
      onSettingsToggle: () => calls.push('settings'),
      onBackFallback: () => calls.push('back'),
    })

    setPad(makePad({ 15: true }))
    stepFrame()
    assert.equal(doc.activeElement, btn0)

    setPad(makePad({}))
    stepFrame()
    setPad(makePad({ 15: true }))
    stepFrame()
    assert.equal(doc.activeElement, btn1)

    setPad(makePad({ 8: true }))
    stepFrame()
    setPad(makePad({ 9: true }))
    stepFrame()
    setPad(makePad({ 4: true }))
    stepFrame()
    setPad(makePad({ 5: true }))
    stepFrame()

    assert.deepEqual(calls, ['search', 'settings', 'room:-1', 'room:1'])
    cleanup()
  })
})
