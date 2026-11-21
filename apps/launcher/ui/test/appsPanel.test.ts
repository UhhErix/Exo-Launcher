import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')
const appsPanel = read('../src/components/apps/AppsPanel.tsx')
const appsCss = read('../src/styles/apps.css')

test('AppsPanel uses flex column layout without hardcoded grid rows', () => {
  assert.match(appsCss, /\.exo-apps\s*\{[\s\S]*?display:\s*flex;[\s\S]*?flex-direction:\s*column;/m)
  assert.doesNotMatch(appsCss, /grid-template-rows:\s*auto auto minmax\(0,\s*1fr\)/)
  assert.match(appsCss, /\.exo-apps-list\s*\{[\s\S]*?flex:\s*1 1 0;/m)
})

test('Startup view refreshes on reopen, groups Broken / On / Off with Switch toggles, and preserves cached icons', () => {
  assert.match(appsPanel, /function mergeIcons\(/)
  assert.doesNotMatch(appsPanel, /function useStartup\(active: boolean\)[\s\S]*?if \(!active \|\| read\.current\) return/m)
  assert.match(appsPanel, /Starts with Windows/)
  assert.match(appsPanel, /Turned off/)
  assert.match(appsPanel, /Clean up broken/)
  assert.match(appsPanel, /<Switch checked=\{true\}[\s\S]*?onChange=\{\(\) => void toggle\(item\.id, item\.ids, false\)\}/)
  assert.match(appsPanel, /item\.ids\.map\(id => errors\[id\]\)\.find\(Boolean\)/)
})

test('WindowsAppsView auto-selects Every account when picked packages are not for this user', () => {
  assert.match(appsPanel, /const needsAllAccounts = \(/)
  assert.match(appsPanel, /setEveryone\(result\.admin\)/)
})
