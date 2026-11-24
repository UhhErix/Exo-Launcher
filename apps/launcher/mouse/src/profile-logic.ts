export type ProfileStep = [method: string, args: unknown[]]
export type PollingMap = Partial<Record<'Wired' | 'Wireless', number>>
export type ProfileRecord = {
  id: string
  name: string
  steps: ProfileStep[]
  savedAt: string
  games?: string[]
  isDefault?: boolean
  polling?: PollingMap
}

export const AUTO_SWITCH_DEBOUNCE_MS = 2_000
const CLICK_MODE_ULTRACLICK_BIT = 2
const ULTRACLICK_ACTUATION_STEPS = 10

export type StepMatchContext = {
  ultraclickL: boolean
  ultraclickR: boolean
}

function toActuationSteps(mm: unknown): number {
  return Math.round(Number(mm) * 100)
}

function clickModeContext(steps: readonly ProfileStep[]): StepMatchContext {
  const step = steps.find(s => s[0] === 'setClickMode')
  if (!step) return { ultraclickL: false, ultraclickR: false }
  const a = step[1]
  const modeL = Number(a[0])
  const modeR = Number(a[1])
  const intL = a.length >= 6 ? Number(a[4]) : 0
  const intR = a.length >= 6 ? Number(a[5]) : 0
  return {
    ultraclickL: modeL === 1 && (intL & CLICK_MODE_ULTRACLICK_BIT) !== 0,
    ultraclickR: modeR === 1 && (intR & CLICK_MODE_ULTRACLICK_BIT) !== 0,
  }
}

export function stepsMatch(saved: ProfileStep, current: ProfileStep, ctx?: StepMatchContext): boolean {
  if (saved[0] !== current[0]) return false
  const method = saved[0]
  const a = saved[1]
  const b = current[1]

  if (method === 'setClickMode') {
    const common = Math.min(a.length, b.length)
    if (common < 2) return false
    for (let i = 0; i < Math.min(common, 4); i++) {
      if (Number(a[i]) !== Number(b[i])) return false
    }
    if (common >= 6) {
      const modeL = Number(a[0])
      const modeR = Number(a[1])
      const intAL = Number(a[4])
      const intBL = Number(b[4])
      const intAR = Number(a[5])
      const intBR = Number(b[5])
      const matchL = modeL === 0
        ? (intAL & ~CLICK_MODE_ULTRACLICK_BIT) === (intBL & ~CLICK_MODE_ULTRACLICK_BIT)
        : intAL === intBL
      const matchR = modeR === 0
        ? (intAR & ~CLICK_MODE_ULTRACLICK_BIT) === (intBR & ~CLICK_MODE_ULTRACLICK_BIT)
        : intAR === intBR
      if (!matchL || !matchR) return false
    }
    return true
  }

  if (method === 'setTmrActuation') {
    if (a.length < 2 || b.length < 2) return false
    const thrAL = toActuationSteps(a[0])
    const thrBL = toActuationSteps(b[0])
    const thrAR = toActuationSteps(a[1])
    const thrBR = toActuationSteps(b[1])
    const matchL = ctx?.ultraclickL ? thrBL === ULTRACLICK_ACTUATION_STEPS : thrAL === thrBL
    const matchR = ctx?.ultraclickR ? thrBR === ULTRACLICK_ACTUATION_STEPS : thrAR === thrBR
    if (!matchL || !matchR) return false
    if (a.length >= 4 && b.length >= 4) {
      if (Math.round(Number(a[2])) !== Math.round(Number(b[2]))) return false
      if (Math.round(Number(a[3])) !== Math.round(Number(b[3]))) return false
    }
    return true
  }

  return JSON.stringify(a) === JSON.stringify(b)
}

export function diffSteps(target: readonly ProfileStep[], now: readonly ProfileStep[]): ProfileStep[] {
  const ctx = clickModeContext(target)
  return target.filter(step => !now.some(current => stepsMatch(step, current, ctx)))
}

export function profileMatches(target: readonly ProfileStep[], now: readonly ProfileStep[]): boolean {
  return diffSteps(target, now).length === 0
}

export function profilesEqual(a: readonly ProfileRecord[], b: readonly ProfileRecord[]): boolean {
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i++) {
    const x = a[i]!
    const y = b[i]!
    if (x.id !== y.id || x.name !== y.name || Boolean(x.isDefault) !== Boolean(y.isDefault) || x.savedAt !== y.savedAt) {
      return false
    }
    const gx = x.games ?? []
    const gy = y.games ?? []
    if (gx.length !== gy.length || gx.some((g, idx) => g !== gy[idx])) return false
    if ((x.polling?.Wired ?? null) !== (y.polling?.Wired ?? null)) return false
    if ((x.polling?.Wireless ?? null) !== (y.polling?.Wireless ?? null)) return false
    if (JSON.stringify(x.steps) !== JSON.stringify(y.steps)) return false
  }
  return true
}

export type RateLimitOptions = {
  shortWindowMs?: number
  maxShort?: number
  longWindowMs?: number
  maxLong?: number
  now?: () => number
}

export type RateLimitDecision = {
  allowed: boolean
  shortCount: number
  longCount: number
}

export class WriteRateLimiter {
  readonly #shortWindowMs: number
  readonly #maxShort: number
  readonly #longWindowMs: number
  readonly #maxLong: number
  readonly #now: () => number
  readonly #stamps = new Map<string, number[]>()

  constructor(options: RateLimitOptions = {}) {
    this.#shortWindowMs = options.shortWindowMs ?? 10_000
    this.#maxShort = options.maxShort ?? 12
    this.#longWindowMs = options.longWindowMs ?? 60_000
    this.#maxLong = options.maxLong ?? 30
    this.#now = options.now ?? (() => Date.now())
  }

  tryAcquire(deviceKey: string): RateLimitDecision {
    const t = this.#now()
    const longCutoff = t - this.#longWindowMs
    const shortCutoff = t - this.#shortWindowMs
    const existing = (this.#stamps.get(deviceKey) ?? []).filter(ts => ts > longCutoff)
    const shortCount = existing.filter(ts => ts > shortCutoff).length
    const longCount = existing.length
    if (shortCount >= this.#maxShort || longCount >= this.#maxLong) {
      this.#stamps.set(deviceKey, existing)
      return { allowed: false, shortCount, longCount }
    }
    existing.push(t)
    this.#stamps.set(deviceKey, existing)
    return { allowed: true, shortCount: shortCount + 1, longCount: longCount + 1 }
  }

  reset(deviceKey?: string): void {
    if (deviceKey === undefined) this.#stamps.clear()
    else this.#stamps.delete(deviceKey)
  }
}

export type AutoSwitchContext<P extends ProfileRecord> = {
  profiles: () => readonly P[]
  resolveSteps: (profile: P) => ProfileStep[]
  currentSteps: () => ProfileStep[]
  apply: (profile: P, diff: ProfileStep[], notice: string | null) => void
  gameTitle: (id: string) => string | undefined
}

export type TimerScheduler = {
  set: (fn: () => void, ms: number) => unknown
  clear: (handle: unknown) => void
}

const DEFAULT_SCHEDULER: TimerScheduler = {
  set: (fn, ms) => globalThis.setTimeout(fn, ms),
  clear: handle => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>),
}

export class ProfileAutoSwitcher<P extends ProfileRecord = ProfileRecord> {
  readonly #debounceMs: number
  readonly #timers: TimerScheduler
  #running: string[] = []
  #activeProfileId: string | null = null
  #switchedForGame = false
  #pendingTimer: unknown = null
  #context: AutoSwitchContext<P> | null = null

  constructor(options: { debounceMs?: number; timers?: TimerScheduler } = {}) {
    this.#debounceMs = options.debounceMs ?? AUTO_SWITCH_DEBOUNCE_MS
    this.#timers = options.timers ?? DEFAULT_SCHEDULER
  }

  get runningGames(): readonly string[] {
    return this.#running
  }

  get activeProfileId(): string | null {
    return this.#activeProfileId
  }

  attach(context: AutoSwitchContext<P>): void {
    this.#context = context
  }

  detach(): void {
    this.#context = null
    this.cancelPending()
  }

  setActiveProfile(profileId: string | null, fromGame = false): void {
    this.cancelPending()
    this.#activeProfileId = profileId
    this.#switchedForGame = fromGame
  }

  cancelPending(): void {
    if (this.#pendingTimer !== null) {
      this.#timers.clear(this.#pendingTimer)
      this.#pendingTimer = null
    }
  }

  findRunningProfile(profiles: readonly P[]): { profile: P; gameId: string } | null {
    for (let i = this.#running.length - 1; i >= 0; i--) {
      const gameId = this.#running[i]!
      const match = profiles.find(p => !p.isDefault && p.games?.includes(gameId))
      if (match) return { profile: match, gameId }
    }
    return null
  }

  onGameRunState(gameId: string, isRunning: boolean): void {
    if (!gameId) return
    const wasRunning = this.#running.includes(gameId)
    if (isRunning === wasRunning) return
    this.#running = isRunning
      ? [...this.#running, gameId]
      : this.#running.filter(id => id !== gameId)
    const ctx = this.#context
    if (!ctx) return
    const profiles = ctx.profiles()
    const isBound = profiles.some(p => !p.isDefault && p.games?.includes(gameId))
    if (!isBound) return
    this.#scheduleEvaluation(isRunning ? 'start' : 'stop')
  }

  syncInitialRunning(): void {
    const ctx = this.#context
    if (!ctx) return
    const found = this.findRunningProfile(ctx.profiles())
    if (!found) return
    this.#scheduleEvaluation('start')
  }

  #scheduleEvaluation(reason: 'start' | 'stop'): void {
    this.cancelPending()
    this.#pendingTimer = this.#timers.set(() => {
      this.#pendingTimer = null
      this.#commitEvaluation(reason)
    }, this.#debounceMs)
  }

  #commitEvaluation(reason: 'start' | 'stop'): void {
    const ctx = this.#context
    if (!ctx) return
    const profiles = ctx.profiles()
    const bound = this.findRunningProfile(profiles)
    if (bound) {
      const target = bound.profile
      if (this.#activeProfileId === target.id) return
      const diff = diffSteps(ctx.resolveSteps(target), ctx.currentSteps())
      this.#activeProfileId = target.id
      this.#switchedForGame = true
      if (diff.length === 0) return
      const title = ctx.gameTitle(bound.gameId) ?? 'your game'
      ctx.apply(target, diff, `${target.name} mouse profile on for ${title}.`)
      return
    }
    if (reason !== 'stop' && !this.#switchedForGame) return
    const fallback = profiles.find(p => p.isDefault)
    if (!fallback) return
    if (this.#activeProfileId === fallback.id) {
      this.#switchedForGame = false
      return
    }
    const diff = diffSteps(ctx.resolveSteps(fallback), ctx.currentSteps())
    this.#activeProfileId = fallback.id
    this.#switchedForGame = false
    if (diff.length === 0) return
    ctx.apply(fallback, diff, `${fallback.name} mouse profile on.`)
  }
}

export const sharedAutoSwitcher = new ProfileAutoSwitcher<ProfileRecord>({ debounceMs: AUTO_SWITCH_DEBOUNCE_MS })

export function notifyManualMouseWrite(): void {
  sharedAutoSwitcher.setActiveProfile(null, false)
}
