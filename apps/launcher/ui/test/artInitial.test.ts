// The placeholder letters (src/components/ui/artInitial.ts). `npm test` (Node's own runner, which reads TypeScript as it is).
//
// How it could fail: a name that starts with a symbol or an emoji shows that symbol as its letter (or crashes on an empty
// name); an accented or two-code-unit letter splits in half; an unnamed game shows nothing instead of "?"; the three
// helpers that used to differ (one letter, up to three words) drift apart again.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { artInitial, initials } from '../src/components/ui/artInitial.ts'

test('a game shows its first letter, capitalised, and skips a leading symbol or emoji', () => {
  assert.equal(artInitial('hades'), 'H')
  assert.equal(artInitial('  élan vital'), 'É')
  assert.equal(artInitial('🎮 Doom'), 'D')
  assert.equal(artInitial('(Half-Life)'), 'H')
  assert.equal(artInitial('2077'), '2')
})

test('a name with no letter or digit, or none at all, shows ?', () => {
  assert.equal(artInitial(''), '?')
  assert.equal(artInitial('   '), '?')
  assert.equal(artInitial('!!! 🎮'), '?')
})

test('a server or friend shows the first letter of its first three words', () => {
  assert.equal(initials('Discord Team Hub'), 'DTH')
  assert.equal(initials('Exo'), 'E')
  assert.equal(initials('a b c d e'), 'ABC')
  assert.equal(initials('😀 Party'), 'P')
  assert.equal(initials(''), '?')
})
