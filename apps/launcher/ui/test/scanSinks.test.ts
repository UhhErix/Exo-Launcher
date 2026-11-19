// security/attacks/bridge/xss/scan-sinks.mjs, the raw-HTML sink gate in npm run check. Ways it could fail: a new
// sink passes because some other sink is allow-listed; an allow-listed sink that changed (now fed by other text)
// still passes; an entry whose line is gone stays forever; an entry without a reason is accepted.
import { test as nodeTest } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const script = path.resolve(import.meta.dirname, '../../security/attacks/bridge/xss/scan-sinks.mjs')
const skip = fs.existsSync(script) ? false : 'Internal sink scanner is not in this source snapshot'
const test = (name: string, run: () => void) => nodeTest(name, { skip }, run)
const SAFE = '<svg dangerouslySetInnerHTML={{ __html: GLYPH }} />'

function scan(files: Record<string, string>, allow: unknown[]) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'exo-sinks-'))
  fs.mkdirSync(path.join(dir, 'src', 'brand'), { recursive: true })
  for (const [file, text] of Object.entries(files)) fs.writeFileSync(path.join(dir, 'src', file), text)
  fs.writeFileSync(path.join(dir, 'allow.json'), JSON.stringify(allow))
  const out = spawnSync(process.execPath, [script, path.join(dir, 'src'), path.join(dir, 'allow.json')], { encoding: 'utf8' })
  fs.rmSync(dir, { recursive: true, force: true })
  return { code: out.status, text: out.stdout + out.stderr }
}
const entry = { file: 'brand/icons.tsx', line: SAFE, why: 'The glyph is SVG path data written at build time.' }

test('an allow-listed sink passes', () => {
  assert.equal(scan({ 'brand/icons.tsx': `  ${SAFE}\n` }, [entry]).code, 0)
})

test('every kind of sink outside the list fails', () => {
  for (const line of ['el.innerHTML = text', 'eval(code)', 'new Function(body)', '<iframe srcDoc={page} />', 'el.insertAdjacentHTML("beforeend", x)', 'const href = "javascript:void 0"']) {
    const r = scan({ 'brand/icons.tsx': `  ${SAFE}\n`, 'other.tsx': line }, [entry])
    assert.equal(r.code, 1, line)
    assert.match(r.text, /other\.tsx:1/, line)
  }
})

test('the same sink in another file, or a changed line, is not covered by the entry', () => {
  assert.equal(scan({ 'brand/icons.tsx': `  ${SAFE}\n`, 'copy.tsx': SAFE }, [entry]).code, 1)
  const changed = scan({ 'brand/icons.tsx': '<svg dangerouslySetInnerHTML={{ __html: props.text }} />' }, [entry])
  assert.equal(changed.code, 1)
  assert.match(changed.text, /STALE/)
})

test('an entry with no reason is refused', () => {
  const r = scan({ 'brand/icons.tsx': SAFE }, [{ file: entry.file, line: SAFE }])
  assert.notEqual(r.code, 0)
})

test('the sinks a reviewer found missing also fail', () => {
  for (const line of ['el.innerHTML += text', 'el.outerHTML += text', 'doc.setHTMLUnsafe(html)', 'const f = Function("return 1")', 'window.Function(body)',
    'e["innerHTML"] = text', "e['innerHTML'] += text", 'setTimeout("run()", 10)', "setInterval('tick()', 10)", 'Document.parseHTMLUnsafe(x)']) {
    const r = scan({ 'brand/icons.tsx': `  ${SAFE}\n`, 'other.tsx': line }, [entry])
    assert.equal(r.code, 1, line)
    assert.match(r.text, /other\.tsx:1/, line)
  }
  // Not sinks: a comparison, a function value, a callback timer, a method called Function-like.
  for (const line of ['if (el.innerHTML === text) go()', 'setTimeout(() => run(), 10)', 'const x = el.innerHTML', 'const f = functionName(1)']) {
    assert.equal(scan({ 'brand/icons.tsx': `  ${SAFE}\n`, 'other.tsx': line }, [entry]).code, 0, line)
  }
})

test('a second copy of an allowed line is a new sink', () => {
  const r = scan({ 'brand/icons.tsx': `  ${SAFE}\n  ${SAFE}\n` }, [entry])
  assert.equal(r.code, 1)
  assert.match(r.text, /icons\.tsx:2/)
})

test('a reason must be a real sentence', () => {
  for (const why of ['ok', 'safe', 'trusted constants only', 'this one is fine because no user text reaches it']) {
    const r = scan({ 'brand/icons.tsx': SAFE }, [{ file: entry.file, line: SAFE, why }])
    assert.notEqual(r.code, 0, why)
    assert.match(r.text, /real sentence/, why)
  }
})
