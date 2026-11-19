// suites/layout.mjs › walk, which opens each sheet, menu and page and looks at it. Ways it could pass with nothing
// looked at: a step whose selector was renamed (open returns false or throws) is skipped quietly; a step this PC
// cannot try is skipped without a word; a step that did not open is not closed again and the next one starts on top.
//   node --test tools/e2e/lib/walk.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { walk, notHere } from '../suites/layout.mjs'

function fakes() {
  const looked = [], warned = [], closed = []
  const t = { k: { settle: async () => {}, layout: async (_c, label) => { looked.push(label); return [] } }, shell: {} }
  const c = { evidence: {}, warn: (reason) => warned.push(reason), shot: async () => {} }
  return { t, c, looked, warned, closed }
}

test('a step that does not open fails the walk and is closed again', async () => {
  const { t, c, looked, closed } = fakes()
  const failures = await walk(t, c, [
    ['renamed', async () => false, async () => closed.push('renamed')],
    ['throws', async () => { throw new Error('no such element') }],
    ['opens', async () => true, async () => closed.push('opens')],
  ])
  assert.deepEqual(failures, ['renamed did not open', 'throws did not open'])
  assert.deepEqual(looked, ['opens'])
  assert.deepEqual(closed, ['renamed', 'opens'])
  assert.equal(c.evidence['throws.error'], 'no such element')
})

test('a step this PC has nothing for is said in the report, not failed and not silent', async () => {
  const { t, c, looked, warned } = fakes()
  const failures = await walk(t, c, [['discord-forum', async () => notHere('the test server has no forum')]])
  assert.deepEqual(failures, [])
  assert.deepEqual(looked, [])
  assert.deepEqual(warned, ['discord-forum not tried: the test server has no forum'])
})

test("what a step's layout finds is the walk's failure", async () => {
  const { t, c } = fakes()
  t.k.layout = async (_c, label) => [`${label}: a button is cut`]
  assert.deepEqual(await walk(t, c, [['sheet', async () => true]]), ['sheet: a button is cut'])
})
