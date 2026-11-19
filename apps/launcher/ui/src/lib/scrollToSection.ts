import { shouldReduceMotion } from './motionPreference'

/**
 * Scrolls a settings sheet's column so a section's top lands at the column's scroll-padding-top (smoothly unless
 * motion is reduced): a search result, a deep link or "open at" landing on its section. Only the column moves;
 * scrollIntoView would also scroll the sheet's clipped ancestors. False when the section is not there (yet).
 */
export function scrollToSection(pane: HTMLElement | null, selector: string, smooth = true): boolean {
  const section = pane?.querySelector(selector)
  if (!pane || !section) return false
  const padding = parseFloat(getComputedStyle(pane).scrollPaddingTop) || 0
  // A sheet still settling in (exo-sheet-in scales 0.98 to 1) draws its column scaled: measure in its own space.
  const scale = pane.getBoundingClientRect().height / pane.offsetHeight || 1
  const offset = (section.getBoundingClientRect().top - pane.getBoundingClientRect().top) / scale - pane.clientTop - padding
  const top = Math.max(0, Math.min(pane.scrollHeight - pane.clientHeight, pane.scrollTop + offset))
  pane.scrollTo({ top, behavior: smooth && !shouldReduceMotion() ? 'smooth' : 'auto' })
  return true
}
