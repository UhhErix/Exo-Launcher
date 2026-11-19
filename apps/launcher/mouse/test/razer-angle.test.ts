// Razer's sensor angle (src/razer-angle.ts) against the reports Synapse sent a Viper V3 Pro (1532:00C0) and the replies
// it got, taken from a USB capture. `npm test`; `npm run check` runs it too.
//
// How it could fail: the angle's sign is encoded backwards or as an offset rather than a signed byte; the two bytes
// before it, the data size, class or command drift from what Synapse sent; the CRC covers the wrong bytes; a reply's
// angle decodes with the wrong sign; a value outside the range is sent; another Razer model (or another brand) gets the
// row; a mouse that does not answer the read shows a number it never reported; a read that answers zeros makes a good
// write look failed; a write the mouse refused looks taken.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { decodeRazerResponse, encodeRazerRequest, razerChecksum } from '@openmouse/protocol/razer'
import {
  RAZER_READ_ANGLE, attachRazerAngle, decodeRazerAngle, hasRazerAngle, razerSetAngleCommand, type RazerAngleStatus, type RazerCommand,
} from '../src/razer-angle.ts'

/** A 90-byte report: its first bytes, zeros, then the CRC at 88 and a zero. */
function report(head: string, crc: number): Uint8Array {
  const bytes = new Uint8Array(90)
  bytes.set(head.match(/../g)!.map(h => parseInt(h, 16)))
  bytes[88] = crc
  return bytes
}

/** Synapse's set and the mouse's reply, as captured: degrees, the transaction id Synapse used, the report, the reply. */
const CAPTURED = [
  { degrees: 10, tx: 0x00, set: report('0000000000030b1401010a', 0x16), reply: report('0200000000030b1401010a', 0x16) },
  { degrees: -10, tx: 0x01, set: report('0001000000030b140101f6', 0xea), reply: report('0201000000030b140101f6', 0xea) },
  { degrees: 30, tx: 0x06, set: report('0006000000030b1401011e', 0x02), reply: report('0206000000030b1401011e', 0x02) },
  { degrees: 0, tx: 0x07, set: report('0007000000030b14010100', 0x1c), reply: report('0207000000030b14010100', 0x1c) },
]

test('the set report is byte for byte what Synapse sent, CRC included', () => {
  for (const { degrees, tx, set } of CAPTURED) {
    assert.deepEqual(encodeRazerRequest(razerSetAngleCommand(degrees), tx), set, `${degrees}°`)
    assert.equal(razerChecksum(set), set[88], `${degrees}° CRC`)
  }
})

test("the mouse's replies decode back to the angle that was sent", () => {
  for (const { degrees, reply } of CAPTURED) {
    const args = decodeRazerResponse(reply, razerSetAngleCommand(degrees))
    assert.equal(decodeRazerAngle(args), degrees)
  }
})

test('every whole degree in the range goes out and comes back the same, signed', () => {
  for (let d = -30; d <= 30; d++) {
    const packet = encodeRazerRequest(razerSetAngleCommand(d), 0x1f)
    assert.equal(packet[10], d & 0xff)
    assert.equal(decodeRazerAngle(packet.slice(8, 11)), d)
  }
})

test('nothing outside -30 to +30, or between degrees, is ever built', () => {
  for (const bad of [-31, 31, 0.5, Number.NaN, 127, -128]) assert.throws(() => razerSetAngleCommand(bad), RangeError)
})

test('the read is the set with the get bit and no angle', () => {
  assert.deepEqual(RAZER_READ_ANGLE, { commandClass: 0x0b, commandId: 0x94, dataSize: 0x03, args: [0x01, 0x01] })
  assert.equal(decodeRazerAngle([0x01, 0x01]), null)
})

test('only the Viper V3 Pro, on its cable and its receiver, has the angle', () => {
  assert.ok(hasRazerAngle({ vendorId: 0x1532, productId: 0x00c0 }))
  assert.ok(hasRazerAngle({ vendorId: 0x1532, productId: 0x00c1 }))
  for (const productId of [0x00a5, 0x00a6, 0x0078, 0x00b6, 0x00c2]) assert.ok(!hasRazerAngle({ vendorId: 0x1532, productId }))
  assert.ok(!hasRazerAngle({ vendorId: 0x046d, productId: 0x00c0 }))
})

/** A Razer client as OpenMouse has it: the device, a status read and a one-command exchange. */
function fakeClient(productId: number, answer: (command: RazerCommand) => Uint8Array) {
  const sent: RazerCommand[] = []
  const client = {
    device: { vendorId: 0x1532, productId } as HIDDevice,
    readStatus: async () => ({ brand: 'Razer', name: 'Viper V3 Pro' }),
    request: async (command: RazerCommand) => { sent.push(command); return answer(command) },
  } as unknown as { readStatus(): Promise<RazerAngleStatus>; setAngleTuning?: (d: number) => Promise<number> }
  return { client, sent }
}

const unsupported = () => { throw new Error('Class 0x0b command 0x94 is not supported by this mouse.') }

test('another model is left as it was: no angle in its status, no setter', async () => {
  const { client, sent } = fakeClient(0x00a6, unsupported)
  attachRazerAngle(client)
  assert.equal(client.setAngleTuning, undefined)
  assert.equal((await client.readStatus()).angleTuning, undefined)
  assert.equal(sent.length, 0)
})

test('the angle is read from the mouse once per connection, and a set is read back', async () => {
  let held = -10
  const { client, sent } = fakeClient(0x00c0, c => {
    if (c.commandId === 0x14) held = (c.args![2] << 24) >> 24
    return Uint8Array.of(0x01, 0x01, held & 0xff)
  })
  attachRazerAngle(client)
  const first = await client.readStatus()
  assert.equal(first.angleTuning, -10)
  assert.equal(first.razerAngleUnread, undefined)
  await client.readStatus()
  assert.equal(sent.filter(c => c.commandId === 0x94).length, 1)
  assert.equal(await client.setAngleTuning!(12), 12)
  assert.deepEqual(sent.at(-2), razerSetAngleCommand(12))
  assert.equal((await client.readStatus()).angleTuning, 12)
})

test("a mouse that doesn't answer the read says so, and shows what Exo set", async () => {
  const { client } = fakeClient(0x00c1, c => (c.commandId === 0x94 ? unsupported() : Uint8Array.of(0x01, 0x01, c.args![2])))
  attachRazerAngle(client)
  const before = await client.readStatus()
  assert.equal(before.angleTuning, 0)
  assert.equal(before.razerAngleUnread, true)
  await client.setAngleTuning!(-7)
  assert.equal((await client.readStatus()).angleTuning, -7)
})

test('a read that answers zeros does not fail a write the mouse took; the row stops trusting it', async () => {
  const { client } = fakeClient(0x00c0, () => new Uint8Array(3))
  attachRazerAngle(client)
  assert.equal((await client.readStatus()).angleTuning, 0)
  assert.equal(await client.setAngleTuning!(5), 5)
  const after = await client.readStatus()
  assert.equal(after.angleTuning, 5)
  assert.equal(after.razerAngleUnread, true)
})

test('a set the mouse refuses fails, and the angle shown stays', async () => {
  const { client } = fakeClient(0x00c0, c => {
    if (c.commandId === 0x14) throw new Error('Class 0x0b command 0x14 returned status 0x03.')
    return Uint8Array.of(0x01, 0x01, 0x03)
  })
  attachRazerAngle(client)
  await client.readStatus()
  await assert.rejects(client.setAngleTuning!(9))
  assert.equal((await client.readStatus()).angleTuning, 3)
})
