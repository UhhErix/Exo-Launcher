// SPDX-License-Identifier: MIT

export interface NativeHost {
  __rune_commit: (opsJson: string) => void
  __rune_get_rect: (id: number) => [number, number, number, number, number, number]
  __rune_get_computed_prop: (id: number, prop: string) => string
  __rune_viewport: () => [number, number]
  __rune_now_ms: () => number
  __rune_log: (level: string, msg: string) => void
  __rune_set_timer: (id: number, delayMs: number, repeat: boolean) => void
  __rune_clear_timer: (id: number) => void
  __exo_rpc_post: (msgJson: string) => void
  __exo_mouse_post: (msgJson: string) => void
  __rune_boot_search?: string
  __rune_boot_mode?: string
}

const g = globalThis as unknown as Record<string, unknown> & NativeHost

function callHost<K extends keyof NativeHost>(k: K): NativeHost[K] {
  return g[k]
}

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'

function polyBtoa(input: string): string {
  const s = String(input)
  let out = ''
  for (let i = 0; i < s.length; i += 3) {
    const a = s.charCodeAt(i) & 0xff
    const b = i + 1 < s.length ? s.charCodeAt(i + 1) & 0xff : 0
    const c = i + 2 < s.length ? s.charCodeAt(i + 2) & 0xff : 0
    const n = (a << 16) | (b << 8) | c
    out += B64[(n >> 18) & 63]
    out += B64[(n >> 12) & 63]
    out += i + 1 < s.length ? B64[(n >> 6) & 63] : '='
    out += i + 2 < s.length ? B64[n & 63] : '='
  }
  return out
}

function polyAtob(input: string): string {
  const s = String(input).replace(/[\t\n\f\r ]+/g, '').replace(/=+$/, '')
  let out = ''
  let buf = 0
  let bits = 0
  for (let i = 0; i < s.length; i++) {
    const v = B64.indexOf(s[i])
    if (v < 0) continue
    buf = (buf << 6) | v
    bits += 6
    if (bits >= 8) {
      bits -= 8
      out += String.fromCharCode((buf >> bits) & 0xff)
    }
  }
  return out
}

class PolyURLSearchParams {
  private entriesList: [string, string][] = []
  constructor(init?: string | Record<string, string> | [string, string][]) {
    if (typeof init === 'string') {
      const q = init.startsWith('?') ? init.slice(1) : init
      if (q) {
        for (const part of q.split('&')) {
          if (!part) continue
          const eq = part.indexOf('=')
          const k = decodeURIComponent((eq >= 0 ? part.slice(0, eq) : part).replace(/\+/g, ' '))
          const v = eq >= 0 ? decodeURIComponent(part.slice(eq + 1).replace(/\+/g, ' ')) : ''
          this.entriesList.push([k, v])
        }
      }
    } else if (Array.isArray(init)) {
      for (const [k, v] of init) this.entriesList.push([String(k), String(v)])
    } else if (init && typeof init === 'object') {
      for (const [k, v] of Object.entries(init)) this.entriesList.push([k, String(v)])
    }
  }
  get(key: string): string | null {
    const f = this.entriesList.find(([k]) => k === key)
    return f ? f[1] : null
  }
  getAll(key: string): string[] {
    return this.entriesList.filter(([k]) => k === key).map(([, v]) => v)
  }
  has(key: string): boolean {
    return this.entriesList.some(([k]) => k === key)
  }
  set(key: string, value: string): void {
    this.delete(key)
    this.entriesList.push([String(key), String(value)])
  }
  append(key: string, value: string): void {
    this.entriesList.push([String(key), String(value)])
  }
  delete(key: string): void {
    this.entriesList = this.entriesList.filter(([k]) => k !== key)
  }
  entries(): IterableIterator<[string, string]> {
    return this.entriesList[Symbol.iterator]()
  }
  [Symbol.iterator](): IterableIterator<[string, string]> {
    return this.entries()
  }
  toString(): string {
    return this.entriesList.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`).join('&')
  }
}

class PolyURL {
  href: string
  protocol = 'https:'
  host = 'exo-launcher.local'
  hostname = 'exo-launcher.local'
  origin = 'https://exo-launcher.local'
  pathname = '/'
  search = ''
  hash = ''
  searchParams: PolyURLSearchParams
  constructor(url: string, base?: string) {
    const raw = base && !/^[a-z]+:/i.test(url) ? `${base.replace(/\/$/, '')}/${url.replace(/^\//, '')}` : url
    this.href = raw
    const m = raw.match(/^([a-z][a-z0-9+.-]*:)\/\/([^/?#]+)([^?#]*)(\?[^#]*)?(#.*)?$/i)
    if (m) {
      this.protocol = m[1]
      this.host = m[2]
      this.hostname = m[2].split(':')[0]
      this.origin = `${this.protocol}//${this.host}`
      this.pathname = m[3] || '/'
      this.search = m[4] || ''
      this.hash = m[5] || ''
    }
    this.searchParams = new PolyURLSearchParams(this.search)
  }
  toString(): string {
    return this.href
  }
}

class PolyTextEncoder {
  encode(str = ''): Uint8Array {
    const bytes: number[] = []
    for (let i = 0; i < str.length; i++) {
      let cp = str.charCodeAt(i)
      if (cp >= 0xd800 && cp <= 0xdbff && i + 1 < str.length) {
        const next = str.charCodeAt(++i)
        cp = 0x10000 + ((cp - 0xd800) << 10) + (next - 0xdc00)
      }
      if (cp < 0x80) bytes.push(cp)
      else if (cp < 0x800) bytes.push(0xc0 | (cp >> 6), 0x80 | (cp & 63))
      else if (cp < 0x10000) bytes.push(0xe0 | (cp >> 12), 0x80 | ((cp >> 6) & 63), 0x80 | (cp & 63))
      else bytes.push(0xf0 | (cp >> 18), 0x80 | ((cp >> 12) & 63), 0x80 | ((cp >> 6) & 63), 0x80 | (cp & 63))
    }
    return new Uint8Array(bytes)
  }
}

class PolyTextDecoder {
  decode(input?: ArrayBufferView | ArrayBuffer): string {
    if (!input) return ''
    const bytes = input instanceof ArrayBuffer
      ? new Uint8Array(input)
      : new Uint8Array(input.buffer, input.byteOffset, input.byteLength)
    let out = ''
    for (let i = 0; i < bytes.length; ) {
      const b0 = bytes[i++]
      if (b0 < 0x80) {
        out += String.fromCharCode(b0)
      } else if ((b0 & 0xe0) === 0xc0 && i < bytes.length) {
        out += String.fromCharCode(((b0 & 0x1f) << 6) | (bytes[i++] & 0x3f))
      } else if ((b0 & 0xf0) === 0xe0 && i + 1 < bytes.length) {
        out += String.fromCharCode(((b0 & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f))
      } else if ((b0 & 0xf8) === 0xf0 && i + 2 < bytes.length) {
        const cp = ((b0 & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f)
        const u = cp - 0x10000
        out += String.fromCharCode(0xd800 + (u >> 10), 0xdc00 + (u & 0x3ff))
      }
    }
    return out
  }
}

export class PolyEvent {
  type: string
  bubbles: boolean
  cancelable: boolean
  defaultPrevented = false
  propagationStopped = false
  target: unknown = null
  currentTarget: unknown = null
  timeStamp: number
  constructor(type: string, init?: { bubbles?: boolean; cancelable?: boolean }) {
    this.type = type
    this.bubbles = Boolean(init?.bubbles)
    this.cancelable = Boolean(init?.cancelable)
    this.timeStamp = nowMs()
  }
  preventDefault(): void {
    this.defaultPrevented = true
  }
  stopPropagation(): void {
    this.propagationStopped = true
  }
  stopImmediatePropagation(): void {
    this.propagationStopped = true
  }
}

export class PolyCustomEvent extends PolyEvent {
  detail: unknown
  constructor(type: string, init?: { detail?: unknown; bubbles?: boolean; cancelable?: boolean }) {
    super(type, init)
    this.detail = init?.detail
  }
}

export class PolyMessageEvent extends PolyEvent {
  data: unknown
  source: unknown
  origin: string
  constructor(type: string, init?: { data?: unknown; source?: unknown; origin?: string }) {
    super(type)
    this.data = init?.data
    this.source = init?.source ?? null
    this.origin = init?.origin ?? ''
  }
}

type Listener = (ev: unknown) => void

export class PolyEventTarget {
  private _listeners = new Map<string, Set<Listener>>()
  addEventListener(type: string, fn: Listener | null): void {
    if (!fn) return
    let set = this._listeners.get(type)
    if (!set) {
      set = new Set()
      this._listeners.set(type, set)
    }
    set.add(fn)
  }
  removeEventListener(type: string, fn: Listener | null): void {
    if (!fn) return
    this._listeners.get(type)?.delete(fn)
  }
  dispatchEvent(ev: PolyEvent): boolean {
    if (!ev.target) ev.target = this
    ev.currentTarget = this
    const set = this._listeners.get(ev.type)
    if (set) {
      for (const fn of [...set]) {
        try {
          fn(ev)
        } catch (err) {
          logError(`event ${ev.type}`, err)
        }
      }
    }
    return !ev.defaultPrevented
  }
}

export function nowMs(): number {
  const fn = callHost('__rune_now_ms')
  return typeof fn === 'function' ? fn() : Date.now()
}

export function logError(context: string, err: unknown): void {
  const fn = callHost('__rune_log')
  const msg = err instanceof Error ? `${err.message}\n${err.stack ?? ''}` : String(err)
  if (typeof fn === 'function') fn('error', `[${context}] ${msg}`)
}

export function logInfo(msg: string): void {
  const fn = callHost('__rune_log')
  if (typeof fn === 'function') fn('info', msg)
}

const timers = new Map<number, { fn: () => void; repeat: boolean }>()
let nextTimerId = 1

function hostSetTimeout(fn: (...args: unknown[]) => void, delay = 0, ...args: unknown[]): number {
  const id = nextTimerId++
  timers.set(id, { fn: () => fn(...args), repeat: false })
  const hostFn = callHost('__rune_set_timer')
  if (typeof hostFn === 'function') hostFn(id, Math.max(0, Number(delay) || 0), false)
  return id
}

function hostSetInterval(fn: (...args: unknown[]) => void, delay = 16, ...args: unknown[]): number {
  const id = nextTimerId++
  timers.set(id, { fn: () => fn(...args), repeat: true })
  const hostFn = callHost('__rune_set_timer')
  if (typeof hostFn === 'function') hostFn(id, Math.max(4, Number(delay) || 16), true)
  return id
}

function hostClearTimer(id: number | undefined): void {
  if (id == null) return
  timers.delete(id)
  const hostFn = callHost('__rune_clear_timer')
  if (typeof hostFn === 'function') hostFn(id)
}

const rafCallbacks = new Map<number, (t: number) => void>()
let nextRafId = 1

function hostRaf(cb: (t: number) => void): number {
  const id = nextRafId++
  rafCallbacks.set(id, cb)
  return id
}

function hostCancelRaf(id: number): void {
  rafCallbacks.delete(id)
}

export function flushRaf(): void {
  if (rafCallbacks.size === 0) return
  const t = nowMs()
  const list = [...rafCallbacks.entries()]
  rafCallbacks.clear()
  for (const [, cb] of list) {
    try {
      cb(t)
    } catch (err) {
      logError('raf', err)
    }
  }
}

export function fireTimer(id: number): void {
  const entry = timers.get(id)
  if (!entry) return
  if (!entry.repeat) timers.delete(id)
  try {
    entry.fn()
  } catch (err) {
    logError(`timer:${id}`, err)
  }
}

export type MutationOp =
  | { op: 'create'; id: number; tag: string; props: Record<string, unknown> }
  | { op: 'createText'; id: number; text: string }
  | { op: 'append'; parent: number; child: number }
  | { op: 'insertBefore'; parent: number; child: number; before: number }
  | { op: 'remove'; parent: number; child: number }
  | { op: 'props'; id: number; props: Record<string, unknown> }
  | { op: 'text'; id: number; text: string }
  | { op: 'clear'; id: number }
  | { op: 'styleProp'; id: number; name: string; value: string | null }
  | { op: 'className'; id: number; value: string }
  | { op: 'attr'; id: number; name: string; value: string | null }
  | { op: 'scroll'; id: number; top: number; left: number }

let pendingOps: MutationOp[] = []
let nextNodeId = 10
export const nodeById = new Map<number, RuneElement | RuneText>()

export function allocNodeId(): number {
  return nextNodeId++
}

export function enqueueOp(op: MutationOp): void {
  pendingOps.push(op)
}

export function flushOps(): void {
  if (pendingOps.length === 0) return
  const batch = pendingOps
  pendingOps = []
  const structural = batch.some(
    o => o.op === 'append' || o.op === 'insertBefore' || o.op === 'remove' || o.op === 'text' || o.op === 'clear',
  )
  const fn = callHost('__rune_commit')
  if (typeof fn === 'function') {
    fn(JSON.stringify(batch))
  }
  if (structural) notifyMutationObservers()
}

function kebabCase(prop: string): string {
  if (prop.startsWith('--')) return prop
  return prop.replace(/[A-Z]/g, m => `-${m.toLowerCase()}`)
}

function camelCase(prop: string): string {
  return prop.replace(/-([a-z])/g, (_, c: string) => c.toUpperCase())
}

function matchesSimpleSelector(el: RuneElement, sel: string): boolean {
  const s = sel.trim()
  if (!s || s === '*') return true
  let rest = s
  const tagMatch = rest.match(/^[a-zA-Z][a-zA-Z0-9-]*/)
  if (tagMatch) {
    if (el.tagName.toLowerCase() !== tagMatch[0].toLowerCase()) return false
    rest = rest.slice(tagMatch[0].length)
  }
  while (rest.length > 0) {
    if (rest[0] === '#') {
      const m = rest.match(/^#([a-zA-Z0-9_-]+)/)
      if (!m || el.id !== m[1]) return false
      rest = rest.slice(m[0].length)
    } else if (rest[0] === '.') {
      const m = rest.match(/^\.([a-zA-Z0-9_-]+)/)
      if (!m || !el.classList.contains(m[1])) return false
      rest = rest.slice(m[0].length)
    } else if (rest[0] === '[') {
      const close = rest.indexOf(']')
      if (close < 0) return false
      const body = rest.slice(1, close).trim()
      rest = rest.slice(close + 1)
      const eq = body.indexOf('=')
      if (eq < 0) {
        if (!el.hasAttribute(body)) return false
      } else {
        const attrName = body.slice(0, eq).trim()
        const attrVal = body.slice(eq + 1).trim().replace(/^['"]|['"]$/g, '')
        if (el.getAttribute(attrName) !== attrVal) return false
      }
    } else if (rest.startsWith(':not(')) {
      const close = rest.indexOf(')')
      if (close < 0) return false
      const inner = rest.slice(5, close)
      rest = rest.slice(close + 1)
      if (matchesSimpleSelector(el, inner)) return false
    } else if (rest.startsWith(':')) {
      const m = rest.match(/^:[a-zA-Z-]+(\([^)]*\))?/)
      if (!m) break
      rest = rest.slice(m[0].length)
    } else {
      break
    }
  }
  return true
}

function matchesSelector(el: RuneElement, selectorList: string, scopeEl?: RuneElement): boolean {
  for (const raw of selectorList.split(',')) {
    const trimmed = raw.trim()
    if (!trimmed) continue
    if (trimmed.startsWith(':scope >')) {
      const sub = trimmed.slice(':scope >'.length).trim()
      if (el.parentElement === scopeEl && matchesSimpleSelector(el, sub)) return true
      continue
    }
    const parts = trimmed.split(/\s+>\s+|\s+/).filter(Boolean)
    if (parts.length === 1) {
      if (matchesSimpleSelector(el, parts[0])) return true
    } else if (parts.length > 1) {
      if (!matchesSimpleSelector(el, parts[parts.length - 1])) continue
      let cur = el.parentElement
      let idx = parts.length - 2
      while (cur && idx >= 0) {
        if (matchesSimpleSelector(cur, parts[idx])) idx--
        cur = cur.parentElement
      }
      if (idx < 0) return true
    }
  }
  return false
}

export class RuneText extends PolyEventTarget {
  readonly nodeId: number
  readonly nodeType = 3
  readonly nodeName = '#text'
  parentNode: RuneElement | null = null
  private _text: string
  constructor(id: number, text: string) {
    super()
    this.nodeId = id
    this._text = text
    nodeById.set(id, this)
  }
  get textContent(): string {
    return this._text
  }
  set textContent(v: string) {
    this._text = String(v ?? '')
    enqueueOp({ op: 'text', id: this.nodeId, text: this._text })
  }
  get nodeValue(): string {
    return this._text
  }
  set nodeValue(v: string) {
    this.textContent = v
  }
}

export class RuneElement extends PolyEventTarget {
  readonly nodeId: number
  readonly nodeType = 1
  readonly tagName: string
  readonly nodeName: string
  parentNode: RuneElement | null = null
  childNodes: (RuneElement | RuneText)[] = []
  attributesMap = new Map<string, string>()
  styleMap = new Map<string, string>()
  reactHandlers = new Map<string, (ev: unknown) => void>()
  private _className = ''
  private _value = ''
  private _checked = false
  private _disabled = false
  private _hidden = false
  private _scrollTop = 0
  private _scrollLeft = 0
  readonly classList: {
    add: (...names: string[]) => void
    remove: (...names: string[]) => void
    toggle: (name: string, force?: boolean) => boolean
    contains: (name: string) => boolean
  }
  readonly dataset: Record<string, string | undefined>
  readonly style: Record<string, unknown> & {
    setProperty: (name: string, value: string | null) => void
    getPropertyValue: (name: string) => string
    removeProperty: (name: string) => string
  }

  constructor(id: number, tag: string) {
    super()
    this.nodeId = id
    this.tagName = tag.toUpperCase()
    this.nodeName = this.tagName
    nodeById.set(id, this)

    const self = this
    this.classList = {
      add(...names: string[]) {
        const set = new Set(self._className.split(/\s+/).filter(Boolean))
        let changed = false
        for (const n of names) {
          if (n && !set.has(n)) {
            set.add(n)
            changed = true
          }
        }
        if (changed) self.className = [...set].join(' ')
      },
      remove(...names: string[]) {
        const set = new Set(self._className.split(/\s+/).filter(Boolean))
        let changed = false
        for (const n of names) {
          if (set.delete(n)) changed = true
        }
        if (changed) self.className = [...set].join(' ')
      },
      toggle(name: string, force?: boolean): boolean {
        const has = this.contains(name)
        const want = force === undefined ? !has : Boolean(force)
        if (want) this.add(name)
        else this.remove(name)
        return want
      },
      contains(name: string): boolean {
        return self._className.split(/\s+/).includes(name)
      },
    }

    this.dataset = new Proxy({} as Record<string, string | undefined>, {
      get(_t, prop) {
        if (typeof prop !== 'string') return undefined
        return self.attributesMap.get(`data-${kebabCase(prop)}`)
      },
      set(_t, prop, value) {
        if (typeof prop === 'string') {
          self.setAttribute(`data-${kebabCase(prop)}`, String(value))
        }
        return true
      },
      deleteProperty(_t, prop) {
        if (typeof prop === 'string') {
          self.removeAttribute(`data-${kebabCase(prop)}`)
        }
        return true
      },
    })

    const styleTarget = {
      setProperty(name: string, value: string | null) {
        const v = value == null ? '' : String(value)
        if (!v) {
          self.styleMap.delete(name)
          enqueueOp({ op: 'styleProp', id: self.nodeId, name, value: null })
        } else {
          self.styleMap.set(name, v)
          enqueueOp({ op: 'styleProp', id: self.nodeId, name, value: v })
        }
      },
      getPropertyValue(name: string): string {
        return self.styleMap.get(name) ?? ''
      },
      removeProperty(name: string): string {
        const prev = self.styleMap.get(name) ?? ''
        self.styleMap.delete(name)
        enqueueOp({ op: 'styleProp', id: self.nodeId, name, value: null })
        return prev
      },
    }
    this.style = new Proxy(styleTarget as Record<string, unknown>, {
      get(target, prop) {
        if (prop in target) return target[prop as string]
        if (typeof prop === 'string') {
          return self.styleMap.get(kebabCase(prop)) ?? self.styleMap.get(prop) ?? ''
        }
        return undefined
      },
      set(target, prop, value) {
        if (typeof prop === 'string' && !(prop in target)) {
          const cssName = kebabCase(prop)
          const v = value == null ? '' : String(value)
          if (!v) {
            self.styleMap.delete(cssName)
            enqueueOp({ op: 'styleProp', id: self.nodeId, name: cssName, value: null })
          } else {
            self.styleMap.set(cssName, v)
            enqueueOp({ op: 'styleProp', id: self.nodeId, name: cssName, value: v })
          }
          return true
        }
        return false
      },
    }) as RuneElement['style']
  }

  get parentElement(): RuneElement | null {
    return this.parentNode
  }
  get children(): RuneElement[] {
    return this.childNodes.filter((c): c is RuneElement => c instanceof RuneElement)
  }
  get childElementCount(): number {
    return this.children.length
  }
  get firstChild(): RuneElement | RuneText | null {
    return this.childNodes[0] ?? null
  }
  get lastChild(): RuneElement | RuneText | null {
    return this.childNodes[this.childNodes.length - 1] ?? null
  }
  get nextSibling(): RuneElement | RuneText | null {
    if (!this.parentNode) return null
    const i = this.parentNode.childNodes.indexOf(this)
    return i >= 0 ? (this.parentNode.childNodes[i + 1] ?? null) : null
  }
  get previousSibling(): RuneElement | RuneText | null {
    if (!this.parentNode) return null
    const i = this.parentNode.childNodes.indexOf(this)
    return i > 0 ? (this.parentNode.childNodes[i - 1] ?? null) : null
  }
  get id(): string {
    return this.attributesMap.get('id') ?? ''
  }
  set id(v: string) {
    this.setAttribute('id', v)
  }
  get className(): string {
    return this._className
  }
  set className(v: string) {
    const next = String(v ?? '')
    if (next === this._className) return
    this._className = next
    this.attributesMap.set('class', next)
    enqueueOp({ op: 'className', id: this.nodeId, value: next })
  }
  get value(): string {
    return this._value
  }
  set value(v: string) {
    this._value = String(v ?? '')
    this.attributesMap.set('value', this._value)
    enqueueOp({ op: 'attr', id: this.nodeId, name: 'value', value: this._value })
  }
  get checked(): boolean {
    return this._checked
  }
  set checked(v: boolean) {
    this._checked = Boolean(v)
    enqueueOp({ op: 'attr', id: this.nodeId, name: 'checked', value: this._checked ? 'true' : null })
  }
  get disabled(): boolean {
    return this._disabled
  }
  set disabled(v: boolean) {
    this._disabled = Boolean(v)
    enqueueOp({ op: 'attr', id: this.nodeId, name: 'disabled', value: this._disabled ? 'true' : null })
  }
  get hidden(): boolean {
    return this._hidden
  }
  set hidden(v: boolean) {
    this._hidden = Boolean(v)
    enqueueOp({ op: 'attr', id: this.nodeId, name: 'hidden', value: this._hidden ? 'true' : null })
  }
  get scrollTop(): number {
    return this._scrollTop
  }
  set scrollTop(v: number) {
    this._scrollTop = Math.max(0, Number(v) || 0)
    enqueueOp({ op: 'scroll', id: this.nodeId, top: this._scrollTop, left: this._scrollLeft })
  }
  get scrollLeft(): number {
    return this._scrollLeft
  }
  set scrollLeft(v: number) {
    this._scrollLeft = Math.max(0, Number(v) || 0)
    enqueueOp({ op: 'scroll', id: this.nodeId, top: this._scrollTop, left: this._scrollLeft })
  }
  get textContent(): string {
    return this.childNodes.map(c => c.textContent).join('')
  }
  set textContent(v: string) {
    this.childNodes = []
    enqueueOp({ op: 'clear', id: this.nodeId })
    if (v) {
      const t = new RuneText(allocNodeId(), String(v))
      t.parentNode = this
      this.childNodes.push(t)
      enqueueOp({ op: 'createText', id: t.nodeId, text: t.textContent })
      enqueueOp({ op: 'append', parent: this.nodeId, child: t.nodeId })
    }
  }
  get innerText(): string {
    return this.textContent
  }
  set innerText(v: string) {
    this.textContent = v
  }

  getAttribute(name: string): string | null {
    if (name === 'class') return this._className || null
    return this.attributesMap.get(name) ?? null
  }
  setAttribute(name: string, value: string): void {
    const str = String(value)
    if (name === 'class') {
      this.className = str
      return
    }
    this.attributesMap.set(name, str)
    enqueueOp({ op: 'attr', id: this.nodeId, name, value: str })
  }
  removeAttribute(name: string): void {
    if (name === 'class') {
      this.className = ''
      return
    }
    this.attributesMap.delete(name)
    enqueueOp({ op: 'attr', id: this.nodeId, name, value: null })
  }
  hasAttribute(name: string): boolean {
    if (name === 'class') return this._className.length > 0
    return this.attributesMap.has(name)
  }

  appendChild(child: RuneElement | RuneText): RuneElement | RuneText {
    if (child.parentNode) child.parentNode.removeChild(child)
    child.parentNode = this
    this.childNodes.push(child)
    enqueueOp({ op: 'append', parent: this.nodeId, child: child.nodeId })
    return child
  }
  append(...items: (RuneElement | RuneText | string)[]): void {
    for (const item of items) {
      if (typeof item === 'string') {
        const t = new RuneText(allocNodeId(), item)
        enqueueOp({ op: 'createText', id: t.nodeId, text: item })
        this.appendChild(t)
      } else {
        this.appendChild(item)
      }
    }
  }
  insertBefore(child: RuneElement | RuneText, before: RuneElement | RuneText | null): RuneElement | RuneText {
    if (!before) return this.appendChild(child)
    if (child.parentNode) child.parentNode.removeChild(child)
    const idx = this.childNodes.indexOf(before)
    child.parentNode = this
    if (idx >= 0) {
      this.childNodes.splice(idx, 0, child)
      enqueueOp({ op: 'insertBefore', parent: this.nodeId, child: child.nodeId, before: before.nodeId })
    } else {
      this.childNodes.push(child)
      enqueueOp({ op: 'append', parent: this.nodeId, child: child.nodeId })
    }
    return child
  }
  removeChild(child: RuneElement | RuneText): RuneElement | RuneText {
    const idx = this.childNodes.indexOf(child)
    if (idx >= 0) {
      this.childNodes.splice(idx, 1)
      child.parentNode = null
      enqueueOp({ op: 'remove', parent: this.nodeId, child: child.nodeId })
    }
    return child
  }
  remove(): void {
    this.parentNode?.removeChild(this)
  }
  contains(other: unknown): boolean {
    let cur = other as RuneElement | RuneText | null
    while (cur) {
      if (cur === this) return true
      cur = cur.parentNode
    }
    return false
  }
  matches(selector: string): boolean {
    return matchesSelector(this, selector)
  }
  closest(selector: string): RuneElement | null {
    let cur: RuneElement | null = this
    while (cur) {
      if (matchesSelector(cur, selector)) return cur
      cur = cur.parentElement
    }
    return null
  }
  querySelectorAll(selector: string): RuneElement[] {
    const out: RuneElement[] = []
    const walk = (el: RuneElement) => {
      for (const child of el.childNodes) {
        if (child instanceof RuneElement) {
          if (matchesSelector(child, selector, this)) out.push(child)
          walk(child)
        }
      }
    }
    walk(this)
    return out
  }
  querySelector(selector: string): RuneElement | null {
    return this.querySelectorAll(selector)[0] ?? null
  }

  private _rect(): [number, number, number, number, number, number] {
    flushOps()
    const fn = callHost('__rune_get_rect')
    if (typeof fn === 'function') {
      const r = fn(this.nodeId)
      if (Array.isArray(r) && r.length >= 6) return r as [number, number, number, number, number, number]
    }
    return [0, 0, 0, 0, 0, 0]
  }
  get offsetLeft(): number { return this._rect()[4] }
  get offsetTop(): number { return this._rect()[5] }
  get offsetWidth(): number { return this._rect()[2] }
  get offsetHeight(): number { return this._rect()[3] }
  get clientWidth(): number { return this._rect()[2] }
  get clientHeight(): number { return this._rect()[3] }
  get scrollWidth(): number { return this._rect()[2] }
  get scrollHeight(): number { return this._rect()[3] }
  getBoundingClientRect() {
    const [x, y, w, h] = this._rect()
    return { x, y, left: x, top: y, width: w, height: h, right: x + w, bottom: y + h, toJSON() { return this } }
  }

  focus(): void {
    activeElementRef = this
    dispatchSyntheticEvent(this.nodeId, 'focus', {})
  }
  blur(): void {
    if (activeElementRef === this) activeElementRef = null
    dispatchSyntheticEvent(this.nodeId, 'blur', {})
  }
  click(): void {
    dispatchSyntheticEvent(this.nodeId, 'click', {})
  }
  scrollIntoView(): void {}
  scrollTo(x?: number | { top?: number; left?: number }, y?: number): void {
    if (typeof x === 'object' && x) {
      if (typeof x.top === 'number') this.scrollTop = x.top
      if (typeof x.left === 'number') this.scrollLeft = x.left
    } else {
      if (typeof x === 'number') this.scrollLeft = x
      if (typeof y === 'number') this.scrollTop = y
    }
  }
  setPointerCapture(): void {}
  releasePointerCapture(): void {}
}

let activeElementRef: RuneElement | null = null

const resizeObservers = new Set<{ cb: (entries: unknown[]) => void; targets: Set<RuneElement> }>()
class PolyResizeObserver {
  private entry: { cb: (entries: unknown[]) => void; targets: Set<RuneElement> }
  constructor(cb: (entries: unknown[]) => void) {
    this.entry = { cb, targets: new Set() }
    resizeObservers.add(this.entry)
  }
  observe(el: RuneElement): void {
    this.entry.targets.add(el)
  }
  unobserve(el: RuneElement): void {
    this.entry.targets.delete(el)
  }
  disconnect(): void {
    this.entry.targets.clear()
    resizeObservers.delete(this.entry)
  }
}

export function notifyResizeObservers(): void {
  for (const obs of [...resizeObservers]) {
    if (obs.targets.size === 0) continue
    const entries = [...obs.targets].map(target => {
      const r = target.getBoundingClientRect()
      return { target, contentRect: r, borderBoxSize: [{ inlineSize: r.width, blockSize: r.height }] }
    })
    try {
      obs.cb(entries)
    } catch (err) {
      logError('ResizeObserver', err)
    }
  }
}

const mutationObservers = new Set<{ cb: (records: unknown[]) => void; active: boolean }>()
class PolyMutationObserver {
  private entry: { cb: (records: unknown[]) => void; active: boolean }
  constructor(cb: (records: unknown[]) => void) {
    this.entry = { cb, active: false }
    mutationObservers.add(this.entry)
  }
  observe(): void {
    this.entry.active = true
  }
  disconnect(): void {
    this.entry.active = false
    mutationObservers.delete(this.entry)
  }
  takeRecords(): unknown[] {
    return []
  }
}

let mutationScheduled = false
function notifyMutationObservers(): void {
  if (mutationScheduled || mutationObservers.size === 0) return
  mutationScheduled = true
  Promise.resolve().then(() => {
    mutationScheduled = false
    for (const obs of [...mutationObservers]) {
      if (!obs.active) continue
      try {
        obs.cb([])
      } catch (err) {
        logError('MutationObserver', err)
      }
    }
  })
}

const htmlEl = new RuneElement(1, 'html')
const bodyEl = new RuneElement(2, 'body')
const rootEl = new RuneElement(3, 'div')
rootEl.setAttribute('id', 'root')
htmlEl.childNodes.push(bodyEl)
bodyEl.parentNode = htmlEl
bodyEl.childNodes.push(rootEl)
rootEl.parentNode = bodyEl
pendingOps = []

class PolyDocument extends PolyEventTarget {
  readonly documentElement = htmlEl
  readonly body = bodyEl
  readonly head = new RuneElement(4, 'head')
  hidden = false
  visibilityState = 'visible'
  get activeElement(): RuneElement | null {
    return activeElementRef ?? bodyEl
  }
  getElementById(id: string): RuneElement | null {
    if (id === 'root') return rootEl
    return htmlEl.querySelector(`#${id}`)
  }
  createElement(tag: string): RuneElement {
    const id = allocNodeId()
    const el = new RuneElement(id, tag)
    enqueueOp({ op: 'create', id, tag: tag.toLowerCase(), props: {} })
    return el
  }
  createElementNS(_ns: string, tag: string): RuneElement {
    return this.createElement(tag)
  }
  createTextNode(text: string): RuneText {
    const id = allocNodeId()
    const t = new RuneText(id, text)
    enqueueOp({ op: 'createText', id, text })
    return t
  }
  querySelector(sel: string): RuneElement | null {
    return htmlEl.querySelector(sel)
  }
  querySelectorAll(sel: string): RuneElement[] {
    return htmlEl.querySelectorAll(sel)
  }
}

export const documentShim = new PolyDocument()

const webviewBus = new PolyEventTarget()
const chromeWebview = {
  postMessage(msg: unknown) {
    const fn = callHost('__exo_rpc_post')
    if (typeof fn === 'function') {
      fn(typeof msg === 'string' ? msg : JSON.stringify(msg))
    }
  },
  addEventListener(type: string, fn: Listener) {
    webviewBus.addEventListener(type, fn)
  },
  removeEventListener(type: string, fn: Listener) {
    webviewBus.removeEventListener(type, fn)
  },
  releaseBuffer(_buf: ArrayBuffer) {},
}

export const windowShim = new PolyEventTarget() as PolyEventTarget & Record<string, unknown>

const parentProxy = {
  postMessage(msg: unknown, _targetOrigin?: string) {
    const fn = callHost('__exo_mouse_post')
    if (typeof fn === 'function') {
      fn(typeof msg === 'string' ? msg : JSON.stringify(msg))
    }
  },
}

const memoryStorage = () => {
  const map = new Map<string, string>()
  return {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => { map.set(k, String(v)) },
    removeItem: (k: string) => { map.delete(k) },
    clear: () => { map.clear() },
    get length() { return map.size },
    key: (i: number) => [...map.keys()][i] ?? null,
  }
}

function getComputedStyleShim(el: RuneElement) {
  return {
    getPropertyValue(prop: string): string {
      const inlineVal = el.styleMap.get(prop)
      if (inlineVal) return inlineVal
      const fn = callHost('__rune_get_computed_prop')
      if (typeof fn === 'function') return fn(el.nodeId, prop) || ''
      return ''
    },
    get paddingLeft(): string { return this.getPropertyValue('padding-left') || '0px' },
    get paddingRight(): string { return this.getPropertyValue('padding-right') || '0px' },
    get paddingTop(): string { return this.getPropertyValue('padding-top') || '0px' },
    get paddingBottom(): string { return this.getPropertyValue('padding-bottom') || '0px' },
    get fontSize(): string { return this.getPropertyValue('font-size') || '13px' },
  }
}

function matchMediaShim(query: string) {
  const evalQuery = (): boolean => {
    const vp = typeof callHost('__rune_viewport') === 'function' ? callHost('__rune_viewport')() : [1280, 800]
    const w = vp[0]
    const maxW = query.match(/max-width:\s*(\d+)px/)
    if (maxW) return w <= Number(maxW[1])
    const minW = query.match(/min-width:\s*(\d+)px/)
    if (minW) return w >= Number(minW[1])
    if (query.includes('prefers-color-scheme: dark')) return true
    if (query.includes('prefers-reduced-motion: reduce')) return false
    return false
  }
  return {
    matches: evalQuery(),
    media: query,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
  }
}

const REACT_EVENT_MAP: Record<string, string> = {
  click: 'onClick',
  dblclick: 'onDoubleClick',
  mousedown: 'onMouseDown',
  mouseup: 'onMouseUp',
  mousemove: 'onMouseMove',
  mouseenter: 'onMouseEnter',
  mouseleave: 'onMouseLeave',
  pointerdown: 'onPointerDown',
  pointerup: 'onPointerUp',
  pointermove: 'onPointerMove',
  keydown: 'onKeyDown',
  keyup: 'onKeyUp',
  input: 'onInput',
  change: 'onChange',
  focus: 'onFocus',
  blur: 'onBlur',
  submit: 'onSubmit',
  wheel: 'onWheel',
}

export function dispatchSyntheticEvent(nodeId: number, type: string, payload: Record<string, unknown>): void {
  const target = nodeById.get(nodeId)
  if (!(target instanceof RuneElement)) return

  if (type === 'input' || type === 'change') {
    if (typeof payload.value === 'string') {
      target.value = payload.value
    }
    if (typeof payload.checked === 'boolean') {
      target.checked = payload.checked
    }
  }

  const ev = new PolyEvent(type, { bubbles: type !== 'mouseenter' && type !== 'mouseleave' && type !== 'focus' && type !== 'blur', cancelable: true }) as PolyEvent & Record<string, unknown>
  Object.assign(ev, payload)
  ev.target = target
  ev.nativeEvent = ev

  const reactProp = REACT_EVENT_MAP[type]
  let cur: RuneElement | null = target
  while (cur) {
    ev.currentTarget = cur
    if (reactProp) {
      const handler = cur.reactHandlers.get(reactProp)
      if (typeof handler === 'function') {
        try {
          handler(ev)
        } catch (err) {
          logError(`react:${reactProp}`, err)
        }
      }
      if (type === 'input') {
        const onChange = cur.reactHandlers.get('onChange')
        if (typeof onChange === 'function') {
          try {
            onChange(ev)
          } catch (err) {
            logError('react:onChange', err)
          }
        }
      }
    }
    cur.dispatchEvent(ev)
    if (!ev.bubbles || ev.propagationStopped) break
    cur = cur.parentElement
  }

  if (ev.bubbles && !ev.propagationStopped) {
    documentShim.dispatchEvent(ev)
    windowShim.dispatchEvent(ev)
  }
}

export function installDomGlobals(): void {
  const bootSearch = typeof g.__rune_boot_search === 'string' ? g.__rune_boot_search : ''
  const loc = {
    href: `https://exo-launcher.local/${bootSearch}`,
    origin: 'https://exo-launcher.local',
    protocol: 'https:',
    host: 'exo-launcher.local',
    hostname: 'exo-launcher.local',
    pathname: '/',
    search: bootSearch,
    hash: '',
    ancestorOrigins: ['https://exo-launcher.local'],
  }

  const perf = {
    now: nowMs,
    timeOrigin: 0,
  }

  const nav: Record<string, unknown> = {
    userAgent: 'ExoRuneReact/0.1 (Windows NT 10.0; Win64; x64)',
    language: 'en-US',
    languages: ['en-US', 'en'],
    platform: 'Win32',
    clipboard: {
      writeText: async () => undefined,
      readText: async () => '',
    },
  }

  const cons = {
    log: (...args: unknown[]) => logInfo(args.map(String).join(' ')),
    info: (...args: unknown[]) => logInfo(args.map(String).join(' ')),
    warn: (...args: unknown[]) => logError('warn', args.map(String).join(' ')),
    error: (...args: unknown[]) => logError('error', args.map(String).join(' ')),
    debug: (...args: unknown[]) => logInfo(args.map(String).join(' ')),
  }

  const pipeConnected = Boolean((g as Record<string, unknown>).__rune_pipe_connected)
  const bootMode = typeof g.__rune_boot_mode === 'string' ? g.__rune_boot_mode : 'mock'
  const chromeObj = pipeConnected ? { webview: chromeWebview } : undefined
  const parentRef = bootMode === 'live' ? parentProxy : windowShim

  Object.assign(windowShim, {
    window: windowShim,
    self: windowShim,
    globalThis,
    parent: parentRef,
    top: parentRef,
    document: documentShim,
    location: loc,
    navigator: nav,
    performance: perf,
    console: cons,
    postMessage(data: unknown, _targetOrigin?: string) {
      windowShim.dispatchEvent(new PolyMessageEvent('message', { data, source: windowShim, origin: loc.origin }))
    },
    ...(chromeObj ? { chrome: chromeObj } : {}),
    setTimeout: hostSetTimeout,
    clearTimeout: hostClearTimer,
    setInterval: hostSetInterval,
    clearInterval: hostClearTimer,
    requestAnimationFrame: hostRaf,
    cancelAnimationFrame: hostCancelRaf,
    queueMicrotask: (cb: () => void) => Promise.resolve().then(cb),
    getComputedStyle: getComputedStyleShim,
    matchMedia: matchMediaShim,
    ResizeObserver: PolyResizeObserver,
    MutationObserver: PolyMutationObserver,
    Event: PolyEvent,
    CustomEvent: PolyCustomEvent,
    MessageEvent: PolyMessageEvent,
    EventTarget: PolyEventTarget,
    HTMLElement: RuneElement,
    Element: RuneElement,
    Node: RuneElement,
    SVGElement: RuneElement,
    HTMLInputElement: RuneElement,
    HTMLButtonElement: RuneElement,
    HTMLDivElement: RuneElement,
    HTMLSpanElement: RuneElement,
    URL: PolyURL,
    URLSearchParams: PolyURLSearchParams,
    TextEncoder: PolyTextEncoder,
    TextDecoder: PolyTextDecoder,
    btoa: polyBtoa,
    atob: polyAtob,
    localStorage: memoryStorage(),
    sessionStorage: memoryStorage(),
    devicePixelRatio: 1,
    crypto: {
      randomUUID: () => 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = (Math.random() * 16) | 0
        return (c === 'x' ? r : (r & 3) | 8).toString(16)
      }),
      getRandomValues: (arr: Uint8Array) => {
        for (let i = 0; i < arr.length; i++) arr[i] = (Math.random() * 256) | 0
        return arr
      },
    },
    structuredClone: <T>(v: T): T => JSON.parse(JSON.stringify(v)),
    get innerWidth(): number {
      return typeof callHost('__rune_viewport') === 'function' ? callHost('__rune_viewport')()[0] : 1280
    },
    get innerHeight(): number {
      return typeof callHost('__rune_viewport') === 'function' ? callHost('__rune_viewport')()[1] : 800
    },
  })

  for (const [k, v] of Object.entries(windowShim)) {
    if (!(k in g) || k === 'console' || k === 'setTimeout' || k === 'clearTimeout' || k === 'setInterval' || k === 'clearInterval') {
      try {
        g[k] = v
      } catch {
        // non-configurable global
      }
    }
  }
  g.window = windowShim
  g.self = windowShim
  g.document = documentShim
  g.location = loc
  g.navigator = nav
  g.performance = perf
  if (chromeObj) g.chrome = chromeObj
  else delete g.chrome
  g.btoa = polyBtoa
  g.atob = polyAtob
  g.URL = PolyURL
  g.URLSearchParams = PolyURLSearchParams
  g.TextEncoder = PolyTextEncoder
  g.TextDecoder = PolyTextDecoder
  g.Event = PolyEvent
  g.CustomEvent = PolyCustomEvent
  g.MessageEvent = PolyMessageEvent
  g.EventTarget = PolyEventTarget
  g.ResizeObserver = PolyResizeObserver
  g.MutationObserver = PolyMutationObserver
  g.getComputedStyle = getComputedStyleShim
  g.matchMedia = matchMediaShim
  g.requestAnimationFrame = hostRaf
  g.cancelAnimationFrame = hostCancelRaf
  g.setTimeout = hostSetTimeout
  g.clearTimeout = hostClearTimer
  g.setInterval = hostSetInterval
  g.clearInterval = hostClearTimer
  g.queueMicrotask = (cb: () => void) => Promise.resolve().then(cb)
  g.HTMLElement = RuneElement
  g.Element = RuneElement
  g.Node = RuneElement
  g.SVGElement = RuneElement
  if (typeof g.Intl === 'undefined') {
    g.Intl = {
      NumberFormat: class {
        format(n: number) { return String(n) }
      },
      DateTimeFormat: class {
        format(d: Date | number) { return new Date(d).toISOString().slice(0, 10) }
        resolvedOptions() { return { timeZone: 'UTC', locale: 'en-US' } }
      },
      Collator: class {
        compare(a: string, b: string) { return a < b ? -1 : a > b ? 1 : 0 }
      },
      RelativeTimeFormat: class {
        format(v: number, u: string) { return `${v} ${u}` }
      },
      ListFormat: class {
        format(arr: string[]) { return arr.join(', ') }
      },
    }
  }

  g.__rune_on_event = (nodeId: number, type: string, payloadJson: string) => {
    let payload: Record<string, unknown> = {}
    try {
      payload = payloadJson ? (JSON.parse(payloadJson) as Record<string, unknown>) : {}
    } catch {
      payload = {}
    }
    dispatchSyntheticEvent(nodeId, type, payload)
  }

  g.__rune_on_timer = (id: number) => {
    fireTimer(id)
  }

  g.__rune_on_raf = () => {
    flushRaf()
  }

  g.__rune_on_resize = () => {
    notifyResizeObservers()
    windowShim.dispatchEvent(new PolyEvent('resize'))
  }

  g.__rune_on_rpc_message = (msgJson: string) => {
    webviewBus.dispatchEvent(new PolyMessageEvent('message', { data: msgJson }))
  }

  g.__rune_on_mouse_message = (msgJson: string) => {
    let data: unknown = msgJson
    try {
      data = JSON.parse(msgJson)
    } catch {
      data = msgJson
    }
    windowShim.dispatchEvent(new PolyMessageEvent('message', { data, source: parentProxy, origin: loc.origin }))
  }
}

export { camelCase, kebabCase, rootEl }
