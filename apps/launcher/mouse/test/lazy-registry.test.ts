// Proves the per-vendor lazy registry matches the monolithic registry on every supported device filter and special shape.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { clientSupportScore, createSupportedClient, deviceBrand } from '@openmouse/protocol/drivers/registry'
import { loadRegistryForDevices } from '@openmouse/protocol/drivers/registry-lazy'
import { SUPPORTED_HID_FILTERS } from '@openmouse/protocol/drivers/vendors'

const REPORT_IDS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 0x09, 0x0e, 0x0f, 0x10, 0x11, 0x20, 0x51, 0xa1, 0xb3, 0xb4, 0xb5]
const ALL_PAGES = [0x01, 0x08, 0x0a, 0x0c, 0x8c, 0xff07, 0xff, 0xff00, 0xff01, 0xff02, 0xff05, 0xff0a, 0xff1c, 0xff42, 0xff43, 0xff55, 0xff60, 0xffa0, 0xffa1, 0xffc0, 0xffc1, 0xffc2, 0xffff]

function rep(reportId: number, len = 16): HIDReportInfo {
  return { reportId, items: [{ reportSize: 8, reportCount: len }] } as unknown as HIDReportInfo
}

function col(usagePage: number, usage: number): HIDCollectionInfo {
  return {
    usagePage,
    usage,
    type: 1,
    children: [],
    featureReports: REPORT_IDS.map(id => rep(id)),
    inputReports: REPORT_IDS.map(id => rep(id)),
    outputReports: REPORT_IDS.map(id => rep(id)),
  } as unknown as HIDCollectionInfo
}

function deviceFor(filter: HIDDeviceFilter, productName = 'probe'): HIDDevice {
  const pages = filter.usagePage !== undefined ? [filter.usagePage] : ALL_PAGES
  const usage = filter.usage ?? 1
  return {
    vendorId: filter.vendorId ?? 0,
    productId: filter.productId ?? 0,
    productName,
    opened: false,
    collections: pages.map(page => col(page, usage)),
  } as unknown as HIDDevice
}

test('empty or unknown devices load no support', async () => {
  const empty = await loadRegistryForDevices([])
  const dummy = deviceFor({ vendorId: 0x0001, productId: 0x0001 })
  assert.equal(empty.clientSupportScore(dummy), 0)
  assert.equal(empty.createSupportedClient(dummy), null)

  const unknown = await loadRegistryForDevices([dummy])
  assert.equal(unknown.clientSupportScore(dummy), 0)
  assert.equal(unknown.createSupportedClient(dummy), null)
})

test('lazy registry matches monolithic registry score, client class, and brand across all picker filters', async () => {
  const devices: HIDDevice[] = SUPPORTED_HID_FILTERS.map(f => deviceFor(f))
  // Named / usage-specific shapes tested in protocol registry suite.
  devices.push(deviceFor({ vendorId: 0x1d57, productId: 0xfa60, usagePage: 0x0b, usage: 0 }, 'Delux M800 Mini'))
  devices.push({ vendorId: 0x1d57, productId: 0xfb02, productName: 'USB Gaming Mouse', opened: false, collections: [] } as unknown as HIDDevice)
  devices.push(deviceFor({ vendorId: 0x1b80, productId: 0xb579, usagePage: 0xff00, usage: 1 }))

  const lazy = await loadRegistryForDevices(devices)
  let matched = 0
  for (const device of devices) {
    const expectedScore = clientSupportScore(device)
    assert.equal(lazy.clientSupportScore(device), expectedScore, `score mismatch for ${device.vendorId.toString(16)}:${device.productId.toString(16)}`)
    const expectedClient = createSupportedClient(device)
    const actualClient = lazy.createSupportedClient(device)
    if (expectedClient === null) {
      assert.equal(actualClient, null)
    } else {
      matched++
      assert.ok(actualClient !== null, `missing client for ${device.vendorId.toString(16)}:${device.productId.toString(16)}`)
      assert.equal(actualClient.constructor, expectedClient.constructor)
      assert.equal(lazy.deviceBrand(actualClient), deviceBrand(expectedClient))
    }
  }
  assert.ok(matched > 20, 'expected to verify many real filter entries')
})
