/** Arrow keys on the music volume slider step by this many percent. */
export const VOLUME_KEY_STEP = 5

/** The percent change an arrow key asks for on the music volume, or null for any other key. */
export function volumeKeyStep(key: string): number | null {
  if (key === 'ArrowUp' || key === 'ArrowRight') return VOLUME_KEY_STEP
  if (key === 'ArrowDown' || key === 'ArrowLeft') return -VOLUME_KEY_STEP
  return null
}
