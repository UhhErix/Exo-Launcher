import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { FinalmouseHidClient } from '@openmouse/protocol/drivers/finalmouse/hid'
import {
  buildFinalmouseReport,
  decodeFinalmouseReport,
  encodeFinalmouseClickMode,
  FINALMOUSE_COMMAND,
  FINALMOUSE_INTENT_TRIGGER,
  FINALMOUSE_REPORT,
  FINALMOUSE_TMR,
  finalmouseIsUltraclick,
} from '@openmouse/protocol/finalmouse'
import {
  buildClickModeArgs,
  clickChoiceOf,
  finalmouseIntentSupport,
  finalmouseUltraclickSupport,
  nextIntentForChoice,
  ULTRACLICK_ACTUATION_STEPS,
} from '../src/finalmouse-state.ts'

function slxInput(command: number, payload: number[]): Uint8Array {
  return new Uint8Array([2 + payload.length, command, payload.length, ...payload])
}

function slxStatus(overrides: Partial<MouseStatus> = {}): MouseStatus {
  return {
    brand: 'Finalmouse',
    name: 'Finalmouse Starlight X',
    dpi: 1600,
    pollingRateHz: 1000,
    connectionType: 'Wireless',
    finalmouseIsSlx: true,
    finalmouseClickModeL: 1,
    finalmouseClickModeR: 1,
    finalmouseClickReleaseL: 0,
    finalmouseClickReleaseR: 0,
    finalmouseClickIntentL: null,
    finalmouseClickIntentR: null,
    finalmouseTmrThrL: 25,
    finalmouseTmrThrR: 25,
    finalmouseTmrHystL: 220,
    finalmouseTmrHystR: 220,
    firmware: ['Mouse 1.2.8-2d88a14', 'Dongle RF 1.2.8-2d88a14'],
    ...overrides,
  } as MouseStatus
}

test('CMD 31 encodes and decodes 2-, 4-, and 6-byte click mode payloads in [R, L] wire order', () => {
  assert.deepEqual([...encodeFinalmouseClickMode(1, 0)], [1, 0])
  assert.deepEqual([...encodeFinalmouseClickMode(1, 0, 2, 1)], [1, 0, 2, 1])
  assert.deepEqual([...encodeFinalmouseClickMode(1, 1, 0, 0, 2, 2)], [1, 1, 0, 0, 2, 2])
  assert.deepEqual([...encodeFinalmouseClickMode(1, 1, 1, 2, 3, 0)], [1, 1, 1, 2, 3, 0])

  const report = buildFinalmouseReport(FINALMOUSE_COMMAND.clickMode, encodeFinalmouseClickMode(1, 1, 0, 0, 2, 3))
  assert.equal(report.length, 63)
  assert.deepEqual([...report.slice(0, 9)], [8, 0x9f, 6, 1, 1, 0, 0, 2, 3])

  assert.deepEqual(
    decodeFinalmouseReport(FINALMOUSE_REPORT.slxInput, slxInput(31, [1, 1, 0, 0, 2, 2])),
    { clickModeR: 1, clickModeL: 1, clickReleaseR: 0, clickReleaseL: 0, clickIntentR: 2, clickIntentL: 2 },
  )
  assert.deepEqual(
    decodeFinalmouseReport(FINALMOUSE_REPORT.slxInput, slxInput(31, [0, 1, 2, 1, 1, 3])),
    { clickModeR: 0, clickModeL: 1, clickReleaseR: 2, clickReleaseL: 1, clickIntentR: 1, clickIntentL: 3 },
  )
  assert.deepEqual(
    decodeFinalmouseReport(FINALMOUSE_REPORT.slxInput, slxInput(31, [1, 0, 2, 0])),
    { clickModeR: 1, clickModeL: 0, clickReleaseR: 2, clickReleaseL: 0 },
  )
  assert.deepEqual(
    decodeFinalmouseReport(FINALMOUSE_REPORT.slxInput, slxInput(53, [0x0a, 0x00, 0x0a, 0x00, 0xdc, 0x00, 0xdc, 0x00, 0xa0, 0x00, 0xa0, 0x00])),
    { tmrThrR: 10, tmrThrL: 10, tmrHystR: 220, tmrHystL: 220 },
  )
})

test('CMD 31 encoder rejects malformed intent trigger and partial payloads', () => {
  assert.throws(() => encodeFinalmouseClickMode(1, 1, 0, 0, 4, 0), /intent trigger/)
  assert.throws(() => encodeFinalmouseClickMode(1, 1, 0, 0, -1, 0), /intent trigger/)
  assert.throws(() => encodeFinalmouseClickMode(1, 1, 0, 0, 2), /intent trigger/)
  assert.throws(() => encodeFinalmouseClickMode(1, 1, undefined, undefined, 2, 2), /release points/)
})

test('FinalmouseHidClient writes 6-byte CMD 31 in [R, L] order and exposes readback in MouseStatus', async () => {
  const writes: Array<{ reportId: number; bytes: number[] }> = []
  let listener: ((event: HIDInputReportEvent) => void) | null = null
  const device = {
    vendorId: 0x361d,
    productId: 0x0300,
    productName: 'Finalmouse SLX',
    collections: [{ usagePage: 0xff00, usage: 1 }],
    opened: true,
    async open() {},
    async close() {},
    addEventListener(_type: string, handler: (event: HIDInputReportEvent) => void) { listener = handler },
    removeEventListener() {},
    async sendReport(reportId: number, data: BufferSource) {
      const bytes = data instanceof ArrayBuffer
        ? new Uint8Array(data)
        : new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
      writes.push({ reportId, bytes: [...bytes.slice(0, 10)] })
    },
  } as unknown as HIDDevice

  const client = new FinalmouseHidClient(device)
  await client.open()
  const emit = (cmd: number, payload: number[]) =>
    listener?.({ reportId: FINALMOUSE_REPORT.slxInput, data: slxInput(cmd, payload) } as HIDInputReportEvent)

  emit(16, [0x40, 0x06])
  emit(17, [0xe8, 0x03])
  emit(31, [1, 1, 0, 1, 3, 2])
  emit(53, [0x0a, 0x00, 0x0a, 0x00, 0xdc, 0x00, 0xdc, 0x00])

  const status = await client.readStatus()
  assert.equal(status.finalmouseClickIntentL, 2)
  assert.equal(status.finalmouseClickIntentR, 3)
  assert.equal(status.finalmouseClickReleaseL, 1)
  assert.equal(status.finalmouseClickReleaseR, 0)

  writes.length = 0
  await client.setClickMode(1, 1, 2, 0, 3, 1)
  assert.equal(writes.length, 1)
  assert.equal(writes[0].reportId, FINALMOUSE_REPORT.slx)
  assert.deepEqual(writes[0].bytes.slice(0, 9), [8, 0x9f, 6, 1, 1, 0, 2, 1, 3])
})

test('Ultraclick bit preserves bit-0 Intent Trigger preference across mode transitions', () => {
  assert.equal(FINALMOUSE_TMR.ultraclickActuationMm, 0.1)
  assert.equal(ULTRACLICK_ACTUATION_STEPS, 10)
  assert.equal(finalmouseIsUltraclick(0), false)
  assert.equal(finalmouseIsUltraclick(1), false)
  assert.equal(finalmouseIsUltraclick(2), true)
  assert.equal(finalmouseIsUltraclick(3), true)

  assert.equal(clickChoiceOf(0, 2), 'mechanical')
  assert.equal(clickChoiceOf(1, 0), 'analog')
  assert.equal(clickChoiceOf(1, 1), 'analog')
  assert.equal(clickChoiceOf(1, 2), 'ultraclick')
  assert.equal(clickChoiceOf(1, 3), 'ultraclick')

  assert.equal(nextIntentForChoice('ultraclick', FINALMOUSE_INTENT_TRIGGER.on), 2)
  assert.equal(nextIntentForChoice('ultraclick', FINALMOUSE_INTENT_TRIGGER.off), 3)
  assert.equal(nextIntentForChoice('ultraclick', null), 2)
  assert.equal(nextIntentForChoice('analog', 2), 0)
  assert.equal(nextIntentForChoice('analog', 3), 1)
  assert.equal(nextIntentForChoice('analog', null), null)
  assert.equal(nextIntentForChoice('mechanical', 3), 1)
  assert.equal(nextIntentForChoice('mechanical', 2), 0)
  assert.equal(nextIntentForChoice('mechanical', null), null)

  assert.deepEqual(buildClickModeArgs(1, 1, 0, 0, 2, 2), [1, 1, 0, 0, 2, 2])
  assert.deepEqual(buildClickModeArgs(1, 1, 0, 0, null, null), [1, 1, 0, 0])
  assert.deepEqual(buildClickModeArgs(1, 0, null, null, null, null), [1, 0])
})

test('firmware gating matches xpanel requirements for Intent Trigger (1.2.0) and Ultraclick (1.2.8 mouse / 1.2.0 dongle)', () => {
  const ready = slxStatus({ finalmouseClickIntentL: 0, finalmouseClickIntentR: 0 })
  assert.deepEqual(finalmouseIntentSupport(ready), { supported: true, needs: null })
  assert.deepEqual(finalmouseUltraclickSupport(ready), { supported: true, needs: null })

  const wiredReady = slxStatus({ connectionType: 'Wired', firmware: ['Mouse 1.2.8-2d88a14'], finalmouseClickIntentL: 2, finalmouseClickIntentR: 2 })
  assert.deepEqual(finalmouseIntentSupport(wiredReady), { supported: true, needs: null })
  assert.deepEqual(finalmouseUltraclickSupport(wiredReady), { supported: true, needs: null })

  const intentOnly = slxStatus({ firmware: ['Mouse 1.2.0-abc', 'Dongle RF 1.2.0-abc'], finalmouseClickIntentL: 0, finalmouseClickIntentR: 0 })
  assert.deepEqual(finalmouseIntentSupport(intentOnly), { supported: true, needs: null })
  assert.deepEqual(finalmouseUltraclickSupport(intentOnly), { supported: false, needs: 'Needs mouse firmware 1.2.8' })

  const oldBoth = slxStatus({ firmware: ['Mouse 1.1.1-5289564', 'Dongle RF 1.1.1-5289564'] })
  assert.deepEqual(finalmouseIntentSupport(oldBoth), { supported: false, needs: 'Needs mouse and dongle firmware 1.2.0' })
  assert.deepEqual(finalmouseUltraclickSupport(oldBoth), { supported: false, needs: 'Needs mouse 1.2.8 and dongle 1.2.0' })

  const oldDongle = slxStatus({ firmware: ['Mouse 1.2.8-2d88a14', 'Dongle RF 1.1.1-5289564'] })
  assert.deepEqual(finalmouseIntentSupport(oldDongle), { supported: false, needs: 'Needs dongle firmware 1.2.0' })
  assert.deepEqual(finalmouseUltraclickSupport(oldDongle), { supported: false, needs: 'Needs dongle firmware 1.2.0' })

  const oldMouseNewDongle = slxStatus({ firmware: ['Mouse 1.1.1-5289564', 'Dongle RF 1.2.8-2d88a14'] })
  assert.deepEqual(finalmouseIntentSupport(oldMouseNewDongle), { supported: false, needs: 'Needs mouse firmware 1.2.0' })
  assert.deepEqual(finalmouseUltraclickSupport(oldMouseNewDongle), { supported: false, needs: 'Needs mouse firmware 1.2.8' })
})
