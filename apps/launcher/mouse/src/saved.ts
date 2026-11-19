import { call, tell } from './exo-hid'
import { frameGet, frameRemove } from './frameStore'
import { makeSaved, type Reply } from './savedStore'

export type { SavedKey } from './savedStore'

/** Exo Mouse's presets and bunny hop values, kept by Exo through the host (savedStore.ts). */
const shared = makeSaved(
  { read: () => call('exo.presets.read') as Promise<Reply>, write: data => call('exo.presets.write', { data }) as Promise<Reply> },
  { get: frameGet, remove: frameRemove },
  text => tell({ notice: text }),
)

export const loadSaved = shared.load
export const savedValue = shared.value
export const saveValue = shared.save
