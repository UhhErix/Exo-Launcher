import type { FrameGenMod } from '../../lib/host'
import type { LineWord } from './GameLine'

/** The page's own apostrophe (’) in words the host sends with the straight one, so one line never mixes the two. */
const curly = (text: string) => text.replace(/'/g, '’')

/**
 * The Frame generation line's word for Exo's own mod: why it is broken, what Exo did by itself, else the mod and
 * release in the game. One plain sentence each, never a file name: another mod over Exo's loader is named when Exo
 * knows it.
 */
export function frameGenWord(state: FrameGenMod): LineWord | null {
  if (state.state === 'native') return { text: 'Built into the game' }
  if (state.state === 'mod') return { text: curly(state.mod ?? 'A mod') }
  if (state.state === 'note') return { text: curly(state.note ?? '') }
  if (state.state === 'broken') {
    if (state.reason === 'replaced') return { text: `${curly(state.mod ?? 'Another mod')} replaced Exo’s loader`, bad: true }
    if (state.reason === 'changed') return { text: 'Exo’s files were changed', bad: true }
    if (state.reason === 'gone') return { text: 'This game no longer has DLSS Frame Generation', bad: true }
    return { text: 'Exo’s files were removed', bad: true }
  }
  if (state.note) return { text: curly(state.note) }
  if (state.state === 'off') return { text: 'Off' }
  return state.state === 'on' && state.version ? { text: curly(state.version) } : null
}
