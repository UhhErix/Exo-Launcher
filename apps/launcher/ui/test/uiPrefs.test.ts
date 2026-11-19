// The page's choices kept by Exo (src/lib/uiPrefs.ts), with a made-up page storage and a made-up settings.set.
//
// How it could fail: a choice an older Exo kept only in the page's storage is lost on the update; its old copy is removed
// for a key Exo already had (from another PC) or before Exo saved the moved value, so a failed save loses it; a moved
// value goes as an ordinary change and overwrites a newer one on the account; one choice Exo refuses (too large) blocks
// every other one on every start; a change made before Exo's settings arrive never reaches them; a save Exo refuses
// undoes the choice on the page.
import { test } from 'node:test'
import assert from 'node:assert/strict'

class Storage {
  items = new Map<string, string>()
  getItem(key: string) { return this.items.get(key) ?? null }
  setItem(key: string, value: string) { this.items.set(key, value) }
  removeItem(key: string) { this.items.delete(key) }
}

const page = new Storage()
;(globalThis as { window?: unknown }).window = { localStorage: page, sessionStorage: new Storage() }
// What an older Exo kept in the page itself.
page.setItem('exo.driverBranch', 'studio')
page.setItem('exo.discord.goLivePreview', 'hidden')
page.setItem('exo.discordPrefs', JSON.stringify({ streamerMode: true, fontSize: 18, cameraId: 'cam-1' }))
page.setItem('exo.shadersOrder.2', JSON.stringify(['steam:730']))
page.setItem('exo.devices.headphones', 'not json')

const prefs = await import('../src/lib/uiPrefs.ts')

type Patch = Record<string, unknown>
/** A host that keeps what it is sent, refuses a value over its limit, and fails on demand. */
function host(had: Patch, fail = new Set<string>()) {
  const kept: Patch = { ...had }
  const saves: Patch[] = []
  const moves: Patch[] = []
  const answer = (patch: Patch, log: Patch[], onlyNew: boolean) => {
    log.push(patch)
    const [key, value] = Object.entries(patch)[0]
    if (fail.has(key) || JSON.stringify(value).length > 200) return Promise.reject(new Error('refused'))
    if (!onlyNew || kept[key] === undefined) kept[key] = value
    return Promise.resolve({ uiPrefs: { ...kept } })
  }
  return { kept, saves, moves, to: { save: (patch: Patch) => answer(patch, saves, false), move: (patch: Patch) => answer(patch, moves, true) } }
}

test('old choices move one key at a time and never as changes; only copies that moved are removed', async () => {
  prefs.setUiPref('voiceChat', false)
  // driverBranch came from Exo (this PC's own value): its old copy is left alone. shadersOrder's move is refused.
  const exo = host({ driverBranch: 'game' }, new Set(['shadersOrder']))

  await prefs.loadUiPrefs({ driverBranch: 'game', unknown: 1 }, exo.to)

  assert.equal(prefs.uiPref('driverBranch'), 'game')
  assert.equal(prefs.uiPref('goLivePreview'), false)
  assert.deepEqual(prefs.uiPref('discord'), { streamerMode: true, fontSize: 18 })
  assert.equal(prefs.uiPref('discordCamera'), 'cam-1')
  assert.equal(prefs.uiPref('wearing'), undefined)
  // Every moved choice went as a move, alone; the one made before Exo answered went as a change.
  assert.ok(exo.moves.every(patch => Object.keys(patch).length === 1))
  assert.deepEqual(exo.moves.map(patch => Object.keys(patch)[0]).sort(), ['discord', 'discordCamera', 'goLivePreview', 'shadersOrder'])
  assert.deepEqual(exo.saves, [{ voiceChat: false }])
  // Moved and saved: gone. Refused: kept for the next start. Exo had it: untouched.
  assert.equal(page.getItem('exo.discord.goLivePreview'), null)
  assert.equal(page.getItem('exo.discordPrefs'), null)
  assert.equal(page.getItem('exo.shadersOrder.2'), JSON.stringify(['steam:730']))
  assert.equal(page.getItem('exo.driverBranch'), 'studio')
})

test('a save Exo refuses keeps the choice on the page for this run', async () => {
  const exo = host({}, new Set(['goLiveQuality']))
  await prefs.loadUiPrefs({}, exo.to)

  prefs.setUiPref('goLiveQuality', { height: 1440, fps: 60, sound: true })
  await new Promise(resolve => setImmediate(resolve))

  assert.deepEqual(prefs.uiPref('goLiveQuality'), { height: 1440, fps: 60, sound: true })
  assert.equal(exo.kept.goLiveQuality, undefined)
  assert.deepEqual(JSON.parse(page.getItem('exo.uiPrefs') ?? '{}').goLiveQuality, { height: 1440, fps: 60, sound: true })
})

test('only the keys Exo keeps are read from its settings, and a choice goes to Exo by its key alone', async () => {
  const exo = host({})
  await prefs.loadUiPrefs({ driverBranch: 'studio', somethingElse: true }, exo.to)
  let heard = 0
  const off = prefs.onUiPrefs(() => { heard++ })

  prefs.setUiPref('shadersOrder', ['steam:570'])
  off()

  assert.equal(prefs.uiPref('driverBranch'), 'studio')
  assert.equal(JSON.parse(page.getItem('exo.uiPrefs') ?? '{}').somethingElse, undefined)
  assert.deepEqual(exo.saves.at(-1), { shadersOrder: ['steam:570'] })
  assert.equal(heard, 1)
})
