// Saving a Riot ID in Settings › Game stores no longer auto-scrapes tracker.gg (Cloudflare blocks app requests).
// Instead, players can enter starting hours directly via playtime.setRiotBaseline or open tracker.gg in their browser.
//
// How it could fail: saving a Riot ID fires host.riotHoursRefresh again; Starting hours omits setRiotBaseline or
// lacks the external tracker.gg browser link.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const source = readFileSync(new URL('../src/components/settings/StoresSection.tsx', import.meta.url), 'utf8')
const save = source.slice(source.indexOf('async function save(next: string)'), source.indexOf('return (', source.indexOf('async function save(next: string)')))

test('saving a Riot ID stays instant while Riot hours exposes sync, export import, and baseline editing', () => {
  assert.ok(save.includes('saveSettingsPatch({ riotId: next })'), 'the save writes riotId')
  assert.ok(!save.includes('riotHoursRefresh'), 'saving Riot ID must not block on riotHoursRefresh in the renderer')
  assert.ok(source.includes('host.riotHoursRefresh'), 'Riot hours exposes automatic cloud sync via riotHoursRefresh')
  assert.ok(source.includes('host.importRiotExport'), 'Riot hours exposes official Riot data export import')
  assert.ok(source.includes('host.setRiotBaseline'), 'inline Riot hours editor saves via setRiotBaseline')
})
