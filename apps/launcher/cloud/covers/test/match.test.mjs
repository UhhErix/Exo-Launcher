/**
 * The title rules that pick a cover: the table mirrors ExoLauncher.Tests/CoverTitleMatchTests.cs.
 *   node --test cloud/covers/test
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { acceptScore, isJunkName, normalizeTitle, scoreTitleMatch } from '../src/match.js'

test('equal titles match exactly, whatever the case, punctuation or spacing', () => {
  assert.equal(scoreTitleMatch('portal 2', 'portal 2'), 100)
  assert.equal(scoreTitleMatch(normalizeTitle("Sid Meier's Civilization VI"), normalizeTitle('Sid Meier’s Civilization® VI')), 100)
  assert.equal(scoreTitleMatch('gravity rush', 'gravityrush'), 100)
})

test('different games score below the accept threshold', () => {
  const pairs = [
    ['dark souls', 'darkness souls'],
    ['metro exodus', 'metro 2033'],
    ['halo infinite', 'halo infinity'],
    ['need for speed heat', 'need for speed hot pursuit'],
    ['war', 'warhammer'],
    ['the witcher', 'the witcher 3 wild hunt'],
    ['tomb raider', 'rise of the tomb raider'],
    ['dying light', 'dying light 2 stay human'],
    ['dying light', 'dying light the following'],
    ['need for speed', 'need for speed heat'],
  ]
  for (const [want, got] of pairs) {
    assert.ok(scoreTitleMatch(want, got) < acceptScore(want), `'${want}' must not take '${got}' as its cover`)
  }
})

test('an edition of the same game still matches high', () => {
  assert.equal(scoreTitleMatch('assassins creed origins', 'assassins creed origins gold edition'), 92)
  assert.equal(scoreTitleMatch('assassins creed origins gold edition', 'assassins creed origins'), 92)
})

test('accept thresholds: 82 for two words, 90 for one', () => {
  assert.equal(acceptScore('doom'), 90)
  assert.equal(acceptScore('doom eternal'), 82)
})

test('empty titles never match', () => {
  assert.equal(scoreTitleMatch('', 'portal'), 0)
  assert.equal(scoreTitleMatch('portal', ''), 0)
})

test('junk store results are dropped', () => {
  assert.equal(isJunkName('Hades Soundtrack', 'hades'), true)
  assert.equal(isJunkName('Hades', 'hades'), false)
})

// The same table is in ExoLauncher.Tests/CoverRefetchSafetyTests.cs (ARealGameIsNotJunk... and AResultWithAJunkWord...).
const REAL_GAMES = ['Observer: System Redux', 'Democracy 4', 'Demonologist', 'Pandemonium', 'Tools Up!', 'DLC Quest', "Dr. Demo's Tools"]
const JUNK_RESULTS = [
  ['Hades Soundtrack', 'Hades'], ['Hades Soundtracks', 'Hades'], ['Hades - Demo', 'Hades'], ['Hades Demos', 'Hades'],
  ['Hades-Demo', 'Hades'], ['Hades (Dedicated Server)', 'Hades'], ['Hades: Playtest', 'Hades'], ['DLC Quest', 'Hades'],
  ['Hades OST', 'Hades'], ['Hades SDK', 'Hades'], ['Hades Tools', 'Hades'], ['Hades/Trailer', 'Hades'],
]

test('a real game with a junk word inside its own name is not junk, whatever the punctuation or plural', () => {
  for (const title of REAL_GAMES) assert.equal(isJunkName(title, normalizeTitle(title)), false, title)
})

test('a result with a junk word the title lacks is junk, singular or plural, split by any punctuation', () => {
  for (const [result, wanted] of JUNK_RESULTS) assert.equal(isJunkName(result, normalizeTitle(wanted)), true, result)
})
