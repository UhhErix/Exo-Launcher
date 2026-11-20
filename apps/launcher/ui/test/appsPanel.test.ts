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

test('Startup view refreshes on reopen, handles Turn on/Turn off states, and preserves cached icons', () => {
  assert.match(appsPanel, /function mergeIcons\(/)
  assert.doesNotMatch(appsPanel, /function useStartup\(active: boolean\)[\s\S]*?if \(!active \|\| read\.current\) return/m)
  assert.match(appsPanel, /const turningOn = !item\.enabled/)
  assert.match(appsPanel, /\{turningOn \? 'Turn on' : 'Turn off'\}/)
  assert.match(appsPanel, /item\.ids\.map\(id => errors\[id\]\)\.find\(Boolean\)/)
})
