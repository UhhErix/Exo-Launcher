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

test('saving a Riot ID writes riotId without auto-firing tracker.gg import', () => {
  assert.ok(save.includes('saveSettingsPatch({ riotId: next })'), 'the save writes riotId')
  assert.ok(!save.includes('riotHoursRefresh'), 'saving Riot ID must not auto-call riotHoursRefresh')
  assert.ok(source.includes('host.setRiotBaseline'), 'inline Starting hours editor saves via setRiotBaseline')
  assert.ok(source.includes('host.openUrl(trackerUrlFor(riotId))'), 'optional external browser link opens tracker.gg')
})
