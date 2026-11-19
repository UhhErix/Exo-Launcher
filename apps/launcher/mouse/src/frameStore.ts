/**
 * The only door to this frame's own storage. It is lost when the frame's origin is cleared and is never in Exo's backups,
 * so it holds conveniences only, each by name below with why it may be lost. What the player chooses goes to Exo through
 * the host (saved.ts, the profiles). ESLint refuses the storage everywhere else.
 */
const KEYS = {
  // When the low battery and weak signal notices last showed, so they do not repeat too soon.
  'exo-mouse.alert.battery': true,
  'exo-mouse.alert.signal': true,
  // The sensor angle the test last found for each model, a starting guess the next test checks again.
  'exo-mouse.sensor-angle': true,
  // Where an older Exo Mouse kept the profiles, presets and bunny hop values: read once to move them to Exo, then removed.
  'exo-mouse.profiles.v1': true,
  'exo-mouse-hits-presets': true,
  'exo-mouse.host-bunny-hop': true,
} as const

export type FrameKey = keyof typeof KEYS

export function frameGet(key: FrameKey): string | null {
  try { return window.localStorage.getItem(key) } catch { return null }
}

export function frameSet(key: FrameKey, value: string) {
  try { window.localStorage.setItem(key, value) } catch { /* a convenience: it starts over next time */ }
}

export function frameRemove(key: FrameKey) {
  try { window.localStorage.removeItem(key) } catch { /* harmless: Exo's copy wins */ }
}
