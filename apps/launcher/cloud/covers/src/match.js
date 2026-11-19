/**
 * Title matching for the cover cache: the same rules as CoverArtService.ScoreTitleMatch and NormalizeTitleKey in the
 * Launcher (ExoLauncher/Services/CoverArtService.cs), so a game gets the same cover from the cache as from the Launcher.
 * The table in test/match.test.mjs is the one ExoLauncher.Tests/CoverTitleMatchTests.cs checks; keep the two in step.
 */

const EDITION_WORDS = new Set([
  'edition', 'gold', 'deluxe', 'goty', 'game', 'of', 'the', 'year', 'complete', 'definitive', 'remastered',
  'remaster', 'enhanced', 'anniversary', 'ultimate', 'standard', 'premium', 'collection', 'directors', 'cut',
  'legendary', 'windows', 'steam', 'gog', 'epic',
])

/** Words that mark a store result that is not the game itself. Whole words only (Observer, Democracy and Pandemonium are games), singular or plural. */
const JUNK = new Set(['soundtrack', 'ost', 'dlc', 'server', 'dedicated', 'demo', 'trailer', 'playtest', 'sdk', 'tool'])

/**
 * The words of a title for the junk test: lower case, apostrophes dropped, anything that is not a letter or digit splits.
 * ExoLauncher's CoverArtService.StoreNameWords does exactly this, and test/match.test.mjs and
 * ExoLauncher.Tests/CoverRefetchSafetyTests.cs hold the same table.
 */
export function junkWords(text) {
  return String(text ?? '').toLowerCase().replace(/['’]/gu, '').split(/[^\p{L}\p{N}]+/u).filter(Boolean)
}

/** The junk word this word is (plurals count), or null. */
function junkBase(word) {
  if (JUNK.has(word)) return word
  return word.endsWith('s') && JUNK.has(word.slice(0, -1)) ? word.slice(0, -1) : null
}

/** Lower case, letters and digits and single spaces only (NormalizeTitleKey). */
export function normalizeTitle(title) {
  return String(title ?? '')
    .replace(/[™®©]/gu, '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N} ]/gu, '')
    .replace(/\s+/gu, ' ')
    .trim()
}

/** `longer` is `shorter` plus a space and only edition words. */
function editionTail(longer, shorter) {
  return longer.length > shorter.length + 1 &&
    longer.startsWith(shorter) &&
    longer[shorter.length] === ' ' &&
    longer.slice(shorter.length + 1).split(' ').every(word => EDITION_WORDS.has(word))
}

/** 0 to 100: how surely `got` (a store's title, normalized) is the game `want` (normalized). */
export function scoreTitleMatch(want, got) {
  if (!want || !got) return 0
  if (want === got) return 100
  if (want.replaceAll(' ', '').length >= 8 && want.replaceAll(' ', '') === got.replaceAll(' ', '')) return 100
  const minLength = Math.min(want.length, got.length)
  if (minLength >= 10 && (editionTail(got, want) || editionTail(want, got))) return 92
  if (got.includes(want) || want.includes(got)) return 50
  const wantWords = want.split(' ').filter(Boolean)
  const gotWords = got.split(' ').filter(Boolean)
  if (!wantWords.length || !gotWords.length) return 0
  const hits = wantWords.filter(word => gotWords.includes(word)).length
  return Math.trunc((100 * hits) / Math.max(wantWords.length, gotWords.length))
}

/** Score a store name must reach: 82 for two or more words, 90 for one (AcceptSteamTitleScore). */
export function acceptScore(normalized) {
  return normalized.split(' ').filter(Boolean).length >= 2 ? 82 : 90
}

/**
 * A store's search result that is not the game itself (soundtracks, DLC, demos, tools): it has a junk word as a whole word
 * (plural too) that the title being looked up does not have. So "DLC Quest" and "Tools Up!" find themselves, and "Hades
 * Soundtrack" or "Hades Demos" never cover "Hades". `name` is the store's own title, `wanted` the title looked up.
 */
export function isJunkName(name, wanted = '') {
  const wantedBases = new Set(junkWords(wanted).map(junkBase).filter(Boolean))
  return junkWords(name).some((word) => {
    const base = junkBase(word)
    return base !== null && !wantedBases.has(base)
  })
}
