// scripts/css-design.mjs, the CSS half of the design gate. Ways it could fail: a backdrop-filter or a literal colour
// passes; a token, #000, transparent or a system colour is flagged; a baselined literal is flagged; a baseline entry
// whose declaration is gone stays silently (the list never shrinks); tokens.css itself is checked.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { runCssDesign } from '../scripts/css-design.mjs'

function run(css: Record<string, string>, config: Record<string, unknown> = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'exo-css-design-'))
  fs.mkdirSync(path.join(dir, 'src'))
  for (const [file, text] of Object.entries(css)) fs.writeFileSync(path.join(dir, 'src', file), text)
  const out = runCssDesign({ sizes: [13], tokenFiles: ['src/tokens.css'], ...config }, { dir: 'src', cwd: dir, configPath: 'config.mjs' })
  fs.rmSync(dir, { recursive: true, force: true })
  return { code: out.code, text: out.text }
}

test('a backdrop-filter (Liquid Glass) passes: nothing in the gate refuses it', () => {
  for (const value of ['blur(12px)', 'var(--exo-control-glass)', 'none', 'blur(10px) var(--glassy-pane-tone)']) {
    assert.equal(run({ 'a.css': `.exo-x { backdrop-filter: ${value}; }` }).code, 0, value)
  }
  assert.equal(run({ 'a.css': '.exo-x { -webkit-backdrop-filter: blur(4px); }' }).code, 0)
})

test('a literal colour outside the token file fails exo/token-colour', () => {
  for (const decl of ['color: #fff', 'background: rgb(0 0 0 / 0.6)', 'border: 1px solid white', 'box-shadow: 0 1px 0 rgba(0,0,0,.4)',
    'color: color-mix(in srgb, var(--color-bad) 82%, white)', '--ink: #3a3a3a', 'color: oklch(from #3dd68c l c h)', 'filter: drop-shadow(0 0 2px hsl(0 0% 0%))']) {
    const r = run({ 'a.css': `.exo-x { ${decl}; }` })
    assert.equal(r.code, 1, decl)
    assert.match(r.text, /exo\/token-colour/, decl)
  }
})

test('tokens, the page black, transparent, system colours and pictures are not literals', () => {
  const r = run({
    'tokens.css': ':root { --exo-scrim: rgb(0 0 0 / 0.72); --color-fg: #fff; }',
    'a.css': [
      '.a { color: var(--color-fg); background: var(--exo-scrim, var(--exo-line)); }',
      '.b { background: #000; border: 1px solid transparent; outline: 1px solid CanvasText; color: currentColor; }',
      '.c { background: url("data:image/svg+xml,%3Csvg fill=\'white\'/%3E") center / 14px no-repeat; }',
      '.d { box-shadow: inset 0 0 0 var(--exo-hair) rgb(var(--exo-ink-rgb) / 0.9); }',
      '.e { transition: color var(--dur-quick); }',
    ].join('\n'),
  })
  assert.equal(r.code, 0, r.text)
})

test('a baselined literal passes; the same literal elsewhere fails; a stale entry fails', () => {
  const entry = 'src/a.css | .exo-x | color: #fff'
  assert.equal(run({ 'a.css': '.exo-x { color: #fff; }' }, { colourBaseline: [entry] }).code, 0)
  const twice = run({ 'a.css': '.exo-x { color: #fff; }\n.exo-y { color: #fff; }' }, { colourBaseline: [entry] })
  assert.equal(twice.code, 1)
  assert.match(twice.text, /a\.css:2 .*exo\/token-colour/)
  const stale = run({ 'a.css': '.exo-x { color: var(--color-fg); }' }, { colourBaseline: [entry] })
  assert.equal(stale.code, 1)
  assert.match(stale.text, /no longer matches/)
})

test('a config with no token file fails: the colour rule cannot be switched off by leaving it out', () => {
  const r = run({ 'a.css': '.exo-x { color: var(--color-fg); }' }, { tokenFiles: undefined })
  assert.equal(r.code, 1)
  assert.match(r.text, /tokenFiles is missing/)
  assert.equal(run({ 'a.css': '.exo-x { color: var(--color-fg); }' }, { tokenFiles: [] }).code, 1)
})

test('property names are matched case-insensitively', () => {
  assert.match(run({ 'a.css': '.exo-x { COLOR: #fff; }' }).text, /exo\/token-colour/)
  assert.match(run({ 'a.css': '.exo-x { Color: #fff; }' }).text, /exo\/token-colour/)
})

test('a literal colour hidden in a var() fallback fails; a token fallback passes', () => {
  assert.equal(run({ 'a.css': '.exo-x { border-top: 1px solid var(--dc-line, rgb(255 255 255 / 0.06)); }' }).code, 1)
  assert.equal(run({ 'a.css': '.exo-x { color: var(--a, var(--b, #123456)); }' }).code, 1)
  assert.equal(run({ 'a.css': '.exo-x { border-top: 1px solid var(--dc-line, var(--exo-line)); color: var(--a, currentColor); }' }).code, 0)
})

test('colours in text-fill, stop, flood, border-image, mask-border, @property and initial-value are checked', () => {
  for (const css of ['.x { -webkit-text-fill-color: #f00; }', 'stop { stop-color: red; }', 'f { flood-color: rgb(1 2 3); }',
    '.x { border-image: linear-gradient(red, blue) 1; }', '.x { -webkit-mask-border-source: linear-gradient(#f00, #00f); }',
    '@property --a { syntax: "<color>"; inherits: false; initial-value: #fff; }', '.x { text-emphasis-color: hotpink; }']) {
    const r = run({ 'a.css': css })
    assert.equal(r.code, 1, css)
    assert.match(r.text, /exo\/token-colour/, css)
  }
  // A mask paints nothing, only its alpha: black stops are not colours.
  assert.equal(run({ 'a.css': '.x { mask-image: linear-gradient(to bottom, transparent 0, black 8%, rgb(0 0 0 / 0.3) 50%); }' }).code, 0)
})

test('a style block and style attributes in the page HTML are checked', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'exo-css-design-'))
  fs.mkdirSync(path.join(dir, 'src'))
  fs.writeFileSync(path.join(dir, 'src', 'a.css'), '.x { color: var(--color-fg); }')
  fs.writeFileSync(path.join(dir, 'config.mjs'), `export default ${JSON.stringify({ sizes: [13], tokenFiles: ['src/tokens.css'], htmlFiles: ['index.html'] })}`)
  const go = (html: string) => {
    fs.writeFileSync(path.join(dir, 'index.html'), html)
    const out = runCssDesign({ sizes: [13], tokenFiles: ['src/tokens.css'], htmlFiles: ['index.html'] }, { dir: 'src', cwd: dir, configPath: 'config.mjs' })
    return { code: out.code, text: out.text }
  }
  assert.equal(go('<style>html { background: #000 }</style><p style="color: var(--color-fg)">x</p>').code, 0)
  assert.match(go('<style>html { background: #123456 }</style>').text, /index\.html.*exo\/token-colour/)
  assert.match(go('<p style="color: #fff">x</p>').text, /exo\/token-colour/)
  fs.rmSync(dir, { recursive: true, force: true })
})

test('a disable comment needs a real reason and is listed in the output', () => {
  const css = (why: string) => `.x {
  /* design-disable-next-line exo/token-colour -- ${why} */
  color: #fff;
}`
  const bare = run({ 'a.css': css('ok') })
  assert.equal(bare.code, 1)
  assert.match(bare.text, /real reason/)
  assert.equal(run({ 'a.css': css('x') }).code, 1)
  const good = run({ 'a.css': css('a capture window cannot show the page behind it') })
  assert.equal(good.code, 0, good.text)
  assert.match(good.text, /1 design-disable comment\(s\) in use[\s\S]*a\.css:2\s+exo\/token-colour -- a capture window/)
})
