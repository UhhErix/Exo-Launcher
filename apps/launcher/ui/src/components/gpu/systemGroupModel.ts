import type { TuningItem } from '../../lib/host'

/** True when a system tuning item is partially applied or off-state yet still holds a saved undo record. */
export function systemItemCanPutBack(item: Pick<TuningItem, 'on' | 'canUndo'>): boolean {
  return !item.on && item.canUndo
}

/** True when any Best settings system item has a pending restart or requires a restart to apply. */
export function systemTuningNeedsRestart(items: readonly Pick<TuningItem, 'on' | 'restart' | 'needsRestart'>[]): boolean {
  return items.some(item => item.restart || (!item.on && item.needsRestart !== false))
}
