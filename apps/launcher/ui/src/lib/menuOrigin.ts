/** The corner a popup grows from, from where it was placed against what opened it: "top-left" when it hangs below the
 *  trigger's left edge, "bottom-right" when it rises above the trigger's right side. CSS maps it to transform-origin
 *  (exo-shell.css, data-origin). Pure, so the shared positioning helper (menuFit.ts) and its test share it. */
export function menuOrigin(at: { x: number; y: number }, from: { left: number; right: number; top: number; bottom: number }): string {
  return `${at.y >= from.top - 1 ? 'top' : 'bottom'}-${at.x >= from.left - 1 ? 'left' : 'right'}`
}
