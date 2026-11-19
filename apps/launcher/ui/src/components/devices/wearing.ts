import type { Wearing } from './AudioPage'
import { setUiPref, uiPref } from '../../lib/uiPrefs'

/** The headphones you picked in the Devices room (what Windows calls only "Headphones" on the jack); null until you pick. */
export function readWearing(): Wearing {
  const saved = uiPref('wearing') as Wearing
  return saved && typeof saved.name === 'string' && (saved.look === 'iem' || saved.look === 'headset' || saved.look === 'headphones') ? saved : null
}

/** What you wear, which Windows can't tell (headphones and IEMs share one plug): the model you picked, and its kind. Kept in Exo's settings. */
export function saveWearing(wearing: Wearing) {
  setUiPref('wearing', wearing)
}
