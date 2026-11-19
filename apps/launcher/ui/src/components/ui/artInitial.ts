// The letter a placeholder shows for a name: the first letter or digit, as a whole character (an accent or an emoji
// sequence is one). One helper for every initial in Exo: a game's tile (artInitial), a server, a friend or a chat's
// letters (initials, up to three words).

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' })
const MARK = /[\p{L}\p{N}]/u

/** The first character of a word that is a letter or digit (null when it has none, an emoji or a symbol alone). */
function firstMark(word: string): string | null {
  for (const { segment } of segmenter.segment(word)) if (MARK.test(segment)) return segment
  return null
}

/** The letter a placeholder shows for art that is missing: a game's first letter, capitalised; "?" when the name has none. */
export function artInitial(name: string): string {
  return firstMark(name.trim())?.toUpperCase() ?? '?'
}

/** The letters a name shows for a server, a friend or a chat: the first letter of each of its first three words; "?" if none. */
export function initials(name: string): string {
  const letters = name.split(/\s+/).filter(Boolean).slice(0, 3).map(firstMark).filter(Boolean).join('')
  return letters ? letters.toUpperCase() : '?'
}
