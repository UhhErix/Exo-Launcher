// Saving a Riot ID in Settings › Game stores imports its lifetime hours once (riot-lifetime). The import is the page's
// call after the save, so it lives in StoresSection.tsx's RiotIdRow; removing a Riot ID does not import.
//
// How it could fail: the save stops asking tracker.gg (the one-time import never runs); the import runs for a removed
// Riot ID (empty); the import is called before the ID is saved, so it reads the old ID.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const source = readFileSync(new URL('../src/components/settings/StoresSection.tsx', import.meta.url), 'utf8')
const save = source.slice(source.indexOf('async function save(next: string)'), source.indexOf('return (', source.indexOf('async function save(next: string)')))

test('saving a Riot ID triggers the one-time tracker.gg import after the ID is saved', () => {
  const saved = save.indexOf('saveSettingsPatch({ riotId: next })')
  const imported = save.indexOf('host.riotHoursRefresh(next)')
  assert.ok(saved >= 0, 'the save writes riotId')
  assert.ok(imported > saved, 'the import runs after the ID is saved')
  assert.ok(save.slice(0, imported).includes('if (next) {'), 'the import runs only for a saved ID, not a removal')
})
