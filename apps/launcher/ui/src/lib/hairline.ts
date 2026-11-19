/**
 * How wide the one line is, in CSS pixels, at a device pixel ratio: whole device pixels, never less than one, as the
 * browser draws a 1px border. At 125% that is 0.8px (one device pixel); at 200%, 1px (two).
 */
export function hairlineWidth(ratio: number): number {
  const scale = Number.isFinite(ratio) && ratio > 0 ? ratio : 1
  return Math.max(1, Math.floor(scale + 0.001)) / scale
}

/**
 * Keeps --exo-hair (tokens.css) at exactly that width, through a change of screen, Windows scale or zoom. A shadow ring
 * drawn 1px wide is 1.25 or 1.5 device pixels on a scaled screen: it lands on half pixels, so some sides come out
 * thicker than others and all of them soft.
 */
export function installHairline(): void {
  let watch: MediaQueryList | undefined
  const set = () => {
    const ratio = window.devicePixelRatio || 1
    document.documentElement.style.setProperty('--exo-hair', `${hairlineWidth(ratio)}px`)
    watch?.removeEventListener('change', set)
    watch = window.matchMedia(`(resolution: ${ratio}dppx)`)
    watch.addEventListener('change', set)
  }
  set()
}
