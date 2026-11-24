import assert from 'node:assert/strict'
import test from 'node:test'
import {
  analyzeFile,
  evaluateRatchet,
  isAllowedComment,
  scanComments,
  stripComments,
  tightenRatchet,
} from './check-comments.mjs'

test('ignores URLs inside strings, CSS url(), and JS regex literals', () => {
  const js = [
    'const u = "https://getexo.app/help"',
    'const rx = /[a-z][a-z0-9+.-]{1,15}:\\/\\//i',
    'const rep = value.replace(/foo\\/bar/g, "baz//qux")',
    'const tpl = `prefix ${`nested ${a} // not` + "http://x"} // also not`',
  ].join('\n')
  assert.equal(analyzeFile(js, 'a.js').commentLineCount, 0)

  const css = [
    '.hero { background: url(https://getexo.app/bg.png); }',
    '.note::before { content: "/* literal */"; }',
  ].join('\n')
  assert.equal(analyzeFile(css, 'a.css').commentLineCount, 0)
})

test('ignores C# verbatim strings, raw string literals, and interpolated strings', () => {
  const cs = [
    'var path = @"C:\\Users\\a//b /* c */";',
    'var raw = """',
    '  // inside raw string',
    '  /* still raw */',
    '""";',
    'var interp = $"url: https://{host}/v1 // ok";',
  ].join('\n')
  assert.equal(analyzeFile(cs, 'A.cs').commentLineCount, 0)
})

test('ignores PowerShell hashes inside strings and detects real PS comments', () => {
  const ps = [
    '$u = "https://getexo.app/#anchor"',
    '$h = @"\n# not a comment\n"@',
    'Write-Output $u # inline ps comment',
    '<#\nblock\n#>',
  ].join('\n')
  const info = analyzeFile(ps, 'a.ps1')
  assert.equal(info.commentLineCount, 4)
})

test('allows machine-interpreted directives and license headers only', () => {
  assert.equal(isAllowedComment('// Generated from ExoBridgeProtocol.cs.', 1), true)
  assert.equal(isAllowedComment('/* design-disable-next-line exo/token-colour -- native thumb */', 5), true)
  assert.equal(isAllowedComment('// eslint-disable-next-line react-hooks/exhaustive-deps', 10), true)
  assert.equal(isAllowedComment('// @ts-expect-error', 10), true)
  assert.equal(isAllowedComment('// SPDX-License-Identifier: MIT', 1), true)
  assert.equal(isAllowedComment('#!/usr/bin/env node', 1), true)
  assert.equal(isAllowedComment('#!/usr/bin/env node', 3), false)
  assert.equal(isAllowedComment('// Regular prose explanation', 4), false)
})

test('strips JS, JSX, and inline comments while preserving regexes and empty catches', () => {
  const tsx = [
    '// file header',
    'const RE = /[a-z]:\\/\\//i, // regex trailing comment',
    '  VAL = 2',
    'export function Demo() {',
    '  try { doWork() } catch { /* pointer gone */ }',
    '  return (',
    '    <>',
    '      {/* radio group comment */}',
    '      <div />',
    '    </>',
    '  )',
    '}',
  ].join('\n')

  const stripped = stripComments(tsx, 'Demo.tsx')
  assert.equal(analyzeFile(stripped, 'Demo.tsx').commentLineCount, 0)
  assert.match(stripped, /\/\[a-z\]:\\\/\\\//)
  assert.match(stripped, /catch \{\}/)
  assert.doesNotMatch(stripped, /radio group comment/)
  assert.doesNotMatch(stripped, /\{\/\*/)
})

test('ignores apostrophes in JSX text so trailing comments are detected and stripped', () => {
  const tsx = [
    'export function Empty({ missing }: { missing: boolean }) {',
    '  if (missing) return <p>Exo Shaders isn\'t installed</p>',
    '  // trailing comment after JSX apostrophe',
    '  /** doc after JSX apostrophe */',
    '  return <div>won\'t fail</div>',
    '}',
  ].join('\n')

  assert.equal(analyzeFile(tsx, 'Empty.tsx').commentLineCount, 2)
  const stripped = stripComments(tsx, 'Empty.tsx')
  assert.equal(analyzeFile(stripped, 'Empty.tsx').commentLineCount, 0)
  assert.match(stripped, /isn't installed/)
  assert.doesNotMatch(stripped, /trailing comment/)
})

test('preserves allowlisted directives during stripComments', () => {
  const src = [
    '// Generated from Foo.cs',
    '// Tautology to delete',
    'export const x = 1',
  ].join('\n')
  const out = stripComments(src, 'wire.ts')
  assert.match(out, /\/\/ Generated from Foo\.cs/)
  assert.doesNotMatch(out, /Tautology/)
  assert.equal(scanComments(out, 'wire.ts').filter((c) => !c.allowed).length, 0)
})

test('enforces strict ratchet semantics and rejects increases on update', () => {
  const ok = evaluateRatchet({ 'a.ts': 4 }, { 'a.ts': 4 })
  assert.equal(ok.ok, true)

  const newFile = evaluateRatchet({ 'a.ts': 4, 'b.ts': 1 }, { 'a.ts': 4 })
  assert.equal(newFile.ok, false)
  assert.equal(newFile.unbaselined[0].rel, 'b.ts')

  const grown = evaluateRatchet({ 'a.ts': 6 }, { 'a.ts': 4 })
  assert.equal(grown.ok, false)
  assert.equal(grown.increased[0].rel, 'a.ts')

  const shrunk = evaluateRatchet({ 'a.ts': 2 }, { 'a.ts': 4 })
  assert.equal(shrunk.ok, false)
  assert.equal(shrunk.stale[0].rel, 'a.ts')

  const tightened = tightenRatchet({ 'a.ts': 2, 'b.ts': 5, 'new.ts': 1 }, { 'a.ts': 4, 'b.ts': 3 })
  assert.equal(tightened.ok, false)
  assert.deepEqual(tightened.next, { 'a.ts': 2, 'b.ts': 3 })
})

test('fallback TSX scanner handles JSX apostrophes without node_modules TypeScript', () => {
  const tsx = [
    'export function Empty({ missing }: { missing: boolean }) {',
    '  if (missing) return <p>Exo Shaders isn\'t installed</p>',
    '  // trailing comment after JSX apostrophe',
    '  return <div>won\'t fail</div>',
    '}',
  ].join('\n')
  const found = scanComments(tsx, 'Empty.tsx', { fallback: true }).filter((c) => !c.allowed)
  assert.equal(found.length, 1)
  assert.equal(found[0].startLine, 3)
})

test('scans Rust and C/C++ comments while ignoring strings, raw strings, and lifetimes', () => {
  const rs = [
    'fn demo<\'a>(s: &\'a str) -> &\'a str {',
    '  let raw = r#"not // a comment /* nested */"#;',
    '  /* outer /* nested */ still comment */',
    '  // rust line comment',
    '  s',
    '}',
  ].join('\n')
  assert.equal(analyzeFile(rs, 'demo.rs').commentLineCount, 2)

  const cc = [
    'const char* url = "https://example.com//not";',
    '// cpp line comment',
    '/* block comment */',
  ].join('\n')
  assert.equal(analyzeFile(cc, 'demo.cc').commentLineCount, 2)
})
