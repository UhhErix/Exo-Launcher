/** Arrow keys on the music volume slider step by this many percent. */
export const VOLUME_KEY_STEP = 5

/** The percent change an arrow key asks for on the music volume, or null for any other key. */
export function volumeKeyStep(key: string): number | null {
  if (key === 'ArrowUp' || key === 'ArrowRight') return VOLUME_KEY_STEP
  if (key === 'ArrowDown' || key === 'ArrowLeft') return -VOLUME_KEY_STEP
  return null
}

/** Key action for Immersive volume shortcuts: M mutes/unmutes, ArrowUp/ArrowDown step by 5%. */
export function immersiveVolumeAction(key: string): 'mute' | number | null {
  if (key === 'm' || key === 'M') return 'mute'
  if (key === 'ArrowUp') return VOLUME_KEY_STEP
  if (key === 'ArrowDown') return -VOLUME_KEY_STEP
  return null
}

/** Computes the clamped 0–100 target level for an Immersive volume shortcut. */
export function nextImmersiveVolume(action: 'mute' | number, current: number, lastAudible: number): number {
  if (action === 'mute') return current > 0 ? 0 : (lastAudible > 0 ? lastAudible : 25)
  return Math.max(0, Math.min(100, Math.round(current) + action))
}
