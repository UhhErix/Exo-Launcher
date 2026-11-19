// What this PC can use (src/lib/canUse.ts), on simulated PCs. `npm test` (Node's runner).
//
// How it could fail: a GeForce-only row (Game Ready or Studio, Output color, Profile Inspector) shows on a Radeon or an
// Intel GPU, or AMD's and Intel's rows on a GeForce; an RTX workstation card is offered Game Ready; a PC whose card Exo
// can't reach gets any maker's row; HDR rows show on a screen without HDR, or vanish while the probe hasn't answered
// and then never return; a setting with one possible value is drawn as a menu; a controller over Bluetooth keeps rows
// it can't send; a one-device menu offers Default and that same device; a keyboard card that opens nothing comes back;
// the Riot ID row shows with no Riot Client, or hides an ID the player saved; a refused game keeps its Compress row;
// "Also in games with anti-cheat" shows while the upscaler updates it widens are off.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  compressShown, devicePicker, deviceHasPage, gpuOffers, hdrShown, isChoice, padSettable, riotIdShown, upscalerAntiCheatShown,
} from '../src/lib/canUse.ts'

/** Simulated PCs: what each probe answers on them. */
const PCS = {
  desktopNvidia: { vendor: 'nvidia', workstation: false, hdr: { supported: true }, stores: ['steam', 'epic'] },
  rtxWorkstation: { vendor: 'nvidia', workstation: true, hdr: { supported: false }, stores: ['steam'] },
  desktopAmd: { vendor: 'amd', workstation: false, hdr: { supported: false }, stores: ['steam', 'riot'] },
  laptopIntel: { vendor: 'intel', workstation: false, hdr: { supported: false }, stores: [] },
  unknownGpu: { vendor: null, workstation: false, hdr: null, stores: ['gog'] },
} as const

test('NVIDIA-only rows show on a GeForce and nowhere else', () => {
  const on = gpuOffers(PCS.desktopNvidia.vendor, PCS.desktopNvidia.workstation)
  assert.deepEqual(on, { driverType: true, outputColor: true, profileImport: true, lowLatency: false, afmf3: false, makerPanel: false })
  for (const pc of [PCS.desktopAmd, PCS.laptopIntel, PCS.unknownGpu]) {
    const offers = gpuOffers(pc.vendor, pc.workstation)
    assert.equal(offers.driverType, false, `${pc.vendor}: no Game Ready or Studio`)
    assert.equal(offers.outputColor, false, `${pc.vendor}: no NVIDIA color settings`)
    assert.equal(offers.profileImport, false, `${pc.vendor}: no Profile Inspector`)
  }
})

test('an RTX workstation card has one driver: no Game Ready or Studio choice', () => {
  const offers = gpuOffers(PCS.rtxWorkstation.vendor, PCS.rtxWorkstation.workstation)
  assert.equal(offers.driverType, false)
  assert.equal(offers.outputColor, true)
})

test("AMD's and Intel's rows are each their own maker's", () => {
  assert.deepEqual(gpuOffers('amd'), { driverType: false, outputColor: false, profileImport: false, lowLatency: false, afmf3: true, makerPanel: true })
  assert.deepEqual(gpuOffers('intel'), { driverType: false, outputColor: false, profileImport: false, lowLatency: true, afmf3: false, makerPanel: true })
})

test("a PC whose card Exo can't reach gets no maker's rows at all", () => {
  for (const vendor of [null, undefined]) assert.ok(Object.values(gpuOffers(vendor)).every(on => !on))
})

test('HDR rows only on a main screen that shows HDR', () => {
  assert.equal(hdrShown(PCS.desktopNvidia.hdr), true)
  assert.equal(hdrShown(PCS.desktopAmd.hdr), false)
  assert.equal(hdrShown(PCS.laptopIntel.hdr), false)
  // Not read yet: nothing, and the rows come once the probe says the screen can.
  assert.equal(hdrShown(PCS.unknownGpu.hdr), false)
  assert.equal(hdrShown(undefined), false)
})

test('one possible value is not a choice', () => {
  assert.equal(isChoice([60]), false)
  assert.equal(isChoice([]), false)
  assert.equal(isChoice([true, true]), false, 'the same value twice is still one')
  assert.equal(isChoice([60, 144]), true)
  assert.equal(isChoice([8, 10, 12]), true)
})

test("a controller's settings go over its cable only: Bluetooth, a dongle or an unknown link can't take them", () => {
  assert.equal(padSettable('usb'), true)
  for (const link of ['bluetooth', 'wireless', null, undefined]) assert.equal(padSettable(link), false, String(link))
})

test('a device menu only with two or more to pick from', () => {
  assert.equal(devicePicker(0), 'none', 'no microphone: say so')
  assert.equal(devicePicker(1), 'one', 'one camera: named, not a menu of Default and itself')
  assert.equal(devicePicker(2), 'menu')
  assert.equal(devicePicker(5), 'menu')
})

test('Devices draws a card only where it opens a page', () => {
  assert.equal(deviceHasPage('mouse', undefined), true)
  assert.equal(deviceHasPage('audio', undefined), true)
  assert.equal(deviceHasPage('controller', true), true, 'a pad the Controller Lab reads')
  assert.equal(deviceHasPage('controller', false), false, "one it can't")
  assert.equal(deviceHasPage('controller', undefined), false)
  assert.equal(deviceHasPage('keyboard', true), false)
})

test('the Riot ID row needs the Riot Client, or an ID already saved', () => {
  assert.equal(riotIdShown(PCS.desktopAmd.stores, ''), true)
  assert.equal(riotIdShown(PCS.desktopNvidia.stores, null), false)
  assert.equal(riotIdShown(PCS.laptopIntel.stores, '   '), false, 'blank is none')
  assert.equal(riotIdShown(PCS.laptopIntel.stores, 'Player#EUW'), true, 'a saved ID can still be taken out')
})

test("Compress in Properties: no row for a game Exo will never compress, or before it's read", () => {
  assert.equal(compressShown({ state: 'refused' }), false)
  assert.equal(compressShown(null), false)
  for (const state of ['none', 'needsConsent', 'compressed', 'compressing', 'kept'] as const) assert.equal(compressShown({ state }), true, state)
})

test('"Also in games with anti-cheat" follows the upscaler updates it widens', () => {
  assert.equal(upscalerAntiCheatShown(true), true)
  assert.equal(upscalerAntiCheatShown(undefined), true, 'on by default')
  assert.equal(upscalerAntiCheatShown(null), true)
  assert.equal(upscalerAntiCheatShown(false), false)
})
