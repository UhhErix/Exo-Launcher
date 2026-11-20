/**
 * Immersive's rules that are not drawing (Immersive.tsx): whether it was on this session, and when the Bar
 * comes back over it. `test/musicImmersive.test.ts`.
 */

/** On or off, for this run of Exo only: a player made again (another service) opens as it was left, and a restart
    opens the room as usual. Never saved: full window is a moment, not a setting. */
let on = false
export const immersiveWasOn = () => on
export function rememberImmersive(next: boolean) { on = next }

/** The key that takes Now playing full window and back, in every service (shortcuts.ts); Esc also leaves. */
export const IMMERSIVE_KEY = 'F'

/** A service's shortcuts with Immersive's key added. The service's own key wins, so one never loses a shortcut it has. */
export function withImmersiveKey<T>(keys: Record<string, T>, immersive: T): Record<string, T> {
  return IMMERSIVE_KEY in keys ? keys : { ...keys, [IMMERSIVE_KEY]: immersive }
}

/** How long the Bar stays over Immersive after the pointer was last near it or a key was pressed. */
export const BAR_IDLE_MS = 2500
/** How far down from the window's top a moving pointer brings the Bar back: the Bar's band and a little under it. */
export const BAR_REVEAL_PX = 120

export type BarWake = { kind: 'pointer'; y: number } | { kind: 'key' }

/** Whether this brings the Bar back over Immersive: any key, or the pointer moving near the window's top. */
export function wakesBar(event: BarWake): boolean {
  return event.kind === 'key' || event.y >= 0 && event.y <= BAR_REVEAL_PX
}

/**
 * Whether the Bar may go: it stays while the pointer is on it, while a key has put focus in it, and while something
 * is open (a menu, Ctrl+K), so nothing in use ever vanishes under the hand.
 */
export function barMayHide(state: { hovered: boolean; focused: boolean; open: boolean }): boolean {
  return !state.hovered && !state.focused && !state.open
}
