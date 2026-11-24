import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { MENU_ITEM_SELECTOR } from '../src/lib/menuKeys.ts'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

test('menu keyboard selector and menuKeys skip aria-disabled items', () => {
  assert.equal(MENU_ITEM_SELECTOR, '[role^="menuitem"]:not([aria-disabled="true"])')
  const menuSrc = read('../src/components/ui/Menu.tsx')
  assert.match(menuSrc, /querySelectorAll<HTMLElement>\(MENU_ITEM_SELECTOR\)/)
  const keysSrc = read('../src/lib/menuKeys.ts')
  assert.match(keysSrc, /getAttribute\('aria-disabled'\)\s*!==\s*'true'/)
})

test('ConfirmButton morph animates real scaleX ratio from previous width and counter-scales label', () => {
  const src = read('../src/components/ui/ConfirmButton.tsx')
  assert.match(src, /from\s*\/\s*to/)
  assert.match(src, /scaleX\(\$\{ratio\}\)/)
  assert.match(src, /scaleX\(\$\{1\s*\/\s*ratio\}\)/)
  assert.match(src, /\.exo-confirm-label/)
})
