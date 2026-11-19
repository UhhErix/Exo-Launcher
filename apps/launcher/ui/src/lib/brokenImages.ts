/**
 * The safety net under ui/ArtImg: any picture that fails to load (a Discord avatar, an emoji, a store shot) is hidden
 * (exo-shell.css › img[data-exo-broken]) instead of drawing the browser's broken-image glyph over its plate. A picture
 * that loads later (a new src) shows again. `error` and `load` do not bubble, so both are heard in the capture phase.
 */
export function installBrokenImageGuard(): void {
  document.addEventListener('error', event => {
    if (event.target instanceof HTMLImageElement) event.target.dataset.exoBroken = ''
  }, true)
  document.addEventListener('load', event => {
    if (event.target instanceof HTMLImageElement) delete event.target.dataset.exoBroken
  }, true)
}
