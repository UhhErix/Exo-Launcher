// errorWords: the one mapping from a failure to the player's words. Ways it could fail: a raw exception, a stack frame,
// a code or a path reaches the page; "Something went wrong" reaches the page; a common Windows failure (access denied,
// a file in use, a full disk, a declined prompt) or no network says nothing about what to do; a plain sentence from the
// host is rewritten; the original words are lost, so Copy details and the log have nothing to say.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { GENERIC, errorText, plainError } from '../src/lib/errorWords.ts'

test('common Windows failures say what happened and what to do', () => {
  assert.match(errorText(new Error("Access to the path 'C:\\Games\\x' is denied.")), /^Windows refused access\. Try again, and allow/)
  assert.match(errorText('System.UnauthorizedAccessException: nope'), /^Windows refused access/)
  assert.match(errorText('The process cannot access the file because it is being used by another process.'), /^A file is in use\. Close the game/)
  assert.match(errorText('There is not enough space on the disk.'), /^The drive is full\. Free up some space/)
  assert.match(errorText('ENOSPC: no space left on device'), /^The drive is full/)
  assert.match(errorText('The operation was canceled by the user.'), /^Windows’ prompt was declined/)
})

test('no network, a slow answer, a busy or broken service and a lapsed sign-in each get their own words', () => {
  assert.match(errorText(new TypeError('Failed to fetch')), /^Exo couldn’t reach the internet\. Check your connection/)
  assert.match(errorText('getaddrinfo ENOTFOUND api.example'), /couldn’t reach the internet/)
  assert.match(errorText('Exo did not answer in time. Try again.'), /^That is taking too long\. Try again\.$/)
  assert.match(errorText('The request was canceled or timed out.'), /taking too long/)
  assert.match(errorText('429: Too Many Requests'), /^Too many tries in a row/)
  assert.match(errorText('401: Unauthorized'), /^You were signed out\. Sign in again/)
  assert.match(errorText('503 Service Unavailable'), /^The service isn’t answering/)
  assert.match(errorText('The launcher and UI versions do not match. Restart Exo.'), /^Exo needs a restart/)
})

test('vague words and raw internals become the caller’s own sentence', () => {
  const fallback = 'Exo couldn’t remove it. Try again.'
  for (const raw of [
    'Something went wrong. Try again.',
    'Unknown error',
    'Error',
    "TypeError: Cannot read properties of undefined (reading 'id')",
    'System.InvalidOperationException: Sequence contains no elements\n   at System.Linq.Enumerable.First()',
    'HRESULT 0x80004005',
    'Could not open C:\\Users\\Someone\\AppData\\x.json',
    '{"code":50013,"message":"Missing Permissions"}',
    'x'.repeat(200),
    '',
    null,
    undefined,
  ]) assert.equal(errorText(raw, fallback), fallback, String(raw))
  assert.equal(errorText(null), GENERIC)
})

test('a plain sentence from the host is kept, and finished', () => {
  assert.equal(errorText('Steam is not running.'), 'Steam is not running.')
  assert.equal(errorText('Its store is offline'), 'Its store is offline.')
  assert.equal(errorText({ ok: false, message: 'That game is already installing.' }), 'That game is already installing.')
})

test('what was really said is kept for the log and Copy details', () => {
  const raw = 'IOException: The process cannot access the file because it is being used by another process.'
  assert.deepEqual(plainError(new Error(raw)), { text: 'A file is in use. Close the game or app using it, then try again.', detail: raw })
  assert.equal(plainError('Steam is not running.').detail, null)
  assert.equal(plainError('Something went wrong').detail, 'Something went wrong')
})
