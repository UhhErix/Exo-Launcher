/**
 * What Exo Mouse tells the rest of Exo: a notice to show (a game profile came
 * on, the battery is low, the signal is weak) and the mouse's battery, for the
 * title bar. Exo Mouse posts these from its frame; the Devices relay hands
 * them here.
 */
export type MouseState = {
  name: string; battery: number | null; charging: boolean; low: boolean
  /** Its maker as Exo Mouse's driver knows it ("Pulsar"), which Windows often doesn't. */
  maker?: string | null
  /** Its product render (OpenMouse's image server only), whose file name picks its card's picture in Devices; null when there is none. */
  image?: string | null
  /** Through a receiver (true), on its cable (false), or not known. */
  wireless?: boolean | null
}

const noticeListeners = new Set<(text: string) => void>()
const stateListeners = new Set<(state: MouseState | null) => void>()
let current: MouseState | null = null

export function publishMouseNotice(text: string) {
  for (const listener of noticeListeners) listener(text)
}

/** Exo Mouse posts its state after every status read; only a change reaches the title bar. */
export function publishMouseState(state: MouseState | null) {
  if (state === current || (state && current && state.name === current.name && state.maker === current.maker && state.battery === current.battery &&
    state.charging === current.charging && state.low === current.low && state.image === current.image && state.wireless === current.wireless)) return
  current = state
  for (const listener of stateListeners) listener(state)
}

export function onMouseNotice(listener: (text: string) => void) {
  noticeListeners.add(listener)
  return () => { noticeListeners.delete(listener) }
}

export function onMouseState(listener: (state: MouseState | null) => void) {
  stateListeners.add(listener)
  listener(current)
  return () => { stateListeners.delete(listener) }
}
