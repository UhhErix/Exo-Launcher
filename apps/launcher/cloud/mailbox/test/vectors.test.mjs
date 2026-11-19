/**
 * The Worker against Exo.Identity's shared vectors (Exo.Identity/Vectors/exo-identity-vectors.json), the file every
 * client (C#, and Swift or Kotlin on a phone) is built to: the login and media messages the Worker checks are the
 * bytes the vectors sign, it takes the vectors' low-s login and refuses its high-s twin.
 *   node --test cloud/mailbox/test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { mediaMessage } from '../src/media.js'
import { authMessage, signedIn } from '../src/worker.js'

const vectors = JSON.parse(readFileSync(new URL('../../../Exo.Identity/Vectors/exo-identity-vectors.json', import.meta.url), 'utf8'))
const bytes = hex => Buffer.from(hex, 'hex')
const sha = data => createHash('sha256').update(data).digest()

test('a box id is the first 16 bytes of SHA-256 of its public key', () => {
  const { boxPublicKey, boxId } = vectors.mailboxAuth
  for (const { publicKey, boxId: id } of [...vectors.sync.boxes, { publicKey: boxPublicKey, boxId }])
    assert.equal(sha(bytes(publicKey)).subarray(0, 16).toString('hex'), id)
})

test('the login the Worker checks is the vectors\' login message', () => {
  const { boxId, challenge, message } = vectors.mailboxAuth
  assert.equal(Buffer.from(authMessage(boxId, bytes(challenge))).toString('hex'), message)
})

test('the vectors\' low-s login opens the box; its high-s twin does not', async () => {
  const { boxPublicKey, boxId, challenge, message } = vectors.mailboxAuth
  const { publicKey, lowS, highS } = vectors.highS
  assert.equal(publicKey, boxPublicKey)
  assert.equal(vectors.highS.message, message)
  const login = signature => new Uint8Array([0x20, ...bytes(boxPublicKey), ...bytes(signature)])
  assert.equal(await signedIn(boxId, bytes(challenge), login(lowS)), true)
  assert.equal(await signedIn(boxId, bytes(challenge), login(highS)), false)
  assert.equal(await signedIn(boxId, bytes(challenge).map(b => b ^ 1), login(lowS)), false)
})

test('the media requests the Worker checks are the vectors\' signed messages', () => {
  const { boxId, putSigned, getSigned } = vectors.media
  for (const { method, name, time, body, signed } of [putSigned, getSigned]) {
    const message = mediaMessage(method, bytes(boxId), name, time, sha(bytes(body ?? '')))
    assert.equal(Buffer.from(message).toString('hex'), signed, method)
  }
})
