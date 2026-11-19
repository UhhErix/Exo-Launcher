import type { host, Tuning, TuningItem, TuningKey } from '../../lib/host'

/** True when a system tuning item is partially applied or off-state yet still holds a saved undo record. */
export function systemItemCanPutBack(item: Pick<TuningItem, 'on' | 'canUndo'>): boolean {
  return !item.on && item.canUndo
}

/** Sends a single system tuning change (`on` or `off`) for `key` through the host bridge. */
export function sendSystemTuning(
  key: TuningKey,
  on: boolean,
  bridge: Pick<typeof host, 'systemTuning'>,
): Promise<Tuning> {
  return bridge.systemTuning(on ? 'on' : 'off', key)
}
