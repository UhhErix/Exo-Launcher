import type { TuningItem } from '../../lib/host'

/** True when a system tuning item is partially applied or off-state yet still holds a saved undo record. */
export function systemItemCanPutBack(item: Pick<TuningItem, 'on' | 'canUndo'>): boolean {
  return !item.on && item.canUndo
}
