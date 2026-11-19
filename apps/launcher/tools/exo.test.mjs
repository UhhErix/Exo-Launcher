import { test } from 'node:test'
import assert from 'node:assert/strict'
import { parseBoard, errorLines, parseGitStatus } from './exo.mjs'

test('board rows carry their tier and drop the header and rule', () => {
  const md = '## Tier 1: biggest\n\n| ID | Idea | Owner | Status |\n| --- | --- | --- | --- |\n| O6 | Glass | launcher | doing (x) |\n\n## Tier 2: next\n| L9 | Thing | browser | todo |\n'
  assert.deepEqual(parseBoard(md), [
    { id: 'O6', idea: 'Glass', owner: 'launcher', status: 'doing (x)', tier: 1 },
    { id: 'L9', idea: 'Thing', owner: 'browser', status: 'todo', tier: 2 },
  ])
})

test('error lines keep failures and drop warnings, capped', () => {
  const out = 'ok\nwarning CS1: meh\nsrc/a.ts(3,1): error TS2322: bad\n  failed: 2\nfailed: 0\n' + 'error x\n'.repeat(20)
  const lines = errorLines(out, 5)
  assert.equal(lines.length, 5)
  assert.equal(lines[0], 'src/a.ts(3,1): error TS2322: bad')
  assert.equal(lines[1], 'failed: 2')
  assert.ok(!lines.some(l => /warning/.test(l)))
})

test('git porcelain v2 gives branch, head, ahead, behind and dirty count', () => {
  const s = '# branch.oid 0123456789abcdef\n# branch.head launcher/batch1\n# branch.upstream source/x\n# branch.ab +3 -1\n1 .M N... 1 1 1 a b f.ts\n? new.txt\n'
  assert.deepEqual(parseGitStatus(s), { branch: 'launcher/batch1', head: '01234567', ahead: 3, behind: 1, dirty: 2 })
})
