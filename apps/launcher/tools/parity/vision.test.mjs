import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  DEFAULT_VISION_MODEL,
  RUBRIC_DIMENSIONS,
  buildRetryPrompt,
  buildVisionPrompt,
  checkGeminiAvailability,
  computePairHash,
  estimateCallCostUsd,
  mergeVisionIntoReport,
  parseAndValidateFindings,
  runVisionPass,
} from './vision.mjs'

function makeSampleReport(reactShot, nativeShot, extraItems = []) {
  return {
    generatedAt: '2026-11-23T00:00:00Z',
    summary: {
      counts: { total: 1 + extraItems.length, CRITICAL: 0, HIGH: 1, MEDIUM: 0, LOW: 0, PASS: 0, SKIP: 0 },
      checks: { openFail: 0, menuFail: 0, cursorFail: 0, glassFail: 0, glassLate: 0, pixelFail: 1 },
      avgMeanAbsDiff: 18.4,
    },
    items: [
      {
        id: 'library-homedeck',
        room: 'library',
        name: 'Library Home Deck',
        kind: 'room',
        priority: 1,
        scene: 'library',
        reactShot,
        nativeShot,
        severity: 'HIGH',
        score: 75,
        issues: ['pixel diff 18.4 (max cell 55)'],
        checks: {
          opened: { status: 'pass', react: true, native: true, detail: 'opened in both' },
          menus: { status: 'na', equal: true, reactItems: [], nativeItems: [], missing: [], extra: [] },
          cursors: { status: 'pass', checked: 2, matched: 2, mismatches: [] },
          glass: { status: 'pass', reactCount: 2, nativeCount: 2, appearedLate: false, detail: 'react=2, native=2' },
          pixels: {
            status: 'fail',
            meanAbsDiff: 18.4,
            maxCellDiff: 55,
            worstCells: [{ row: 0, col: 0, x0: 0, x1: 240, y0: 0, y1: 180, score: 55 }],
            sheet: null,
          },
        },
      },
      ...extraItems,
    ],
  }
}

function makeMockCli({ findingsByCall = [], usage = { prompt_tokens: 4000, output_tokens: 600, thoughts_tokens: 200, total_tokens: 4800 } } = {}) {
  const calls = []
  let idx = 0
  const runCli = async (cliPath, args) => {
    calls.push({ cliPath, args })
    if (args[0] === 'whoami') {
      return {
        code: 0,
        enoent: false,
        stdout: JSON.stringify({
          credentials: {
            'api-key': { source: 'keyring', value: 'AQ******HQ' },
            'access-token': { source: 'unset', value: '(not set)' },
          },
        }),
        stderr: '',
      }
    }
    const resp = findingsByCall[Math.min(idx, findingsByCall.length - 1)]
    idx++
    const text = typeof resp === 'string' ? resp : JSON.stringify(resp ?? [])
    return {
      code: 0,
      enoent: false,
      stdout: JSON.stringify({ model: DEFAULT_VISION_MODEL, text, usage }),
      stderr: '',
    }
  }
  return { runCli, calls }
}

test('buildVisionPrompt includes all 9 rubric dimensions, state context, pixel ground truth, and JSON schema', () => {
  const prompt = buildVisionPrompt(
    { id: 'store-discover', name: 'Store Discover room', room: 'store', kind: 'room' },
    { meanAbsDiff: 22.1, maxCellDiff: 61.5, worstCells: [{ x0: 240, x1: 480, y0: 180, y1: 360, score: 61.5 }] },
    { hasOverlaySheet: true },
  )
  assert.ok(prompt.includes('store-discover'))
  assert.ok(prompt.includes('meanAbsDiff=22.1'))
  assert.ok(prompt.includes('Image 3 is a 2x2 comparison sheet'))
  assert.ok(prompt.includes('Lucide icons at 1.5px stroke'))
  assert.ok(prompt.includes('Liquid Glass'))
  assert.ok(prompt.includes('"fix_hint"'))
  assert.ok(prompt.includes('"bbox"'))
  for (const dim of RUBRIC_DIMENSIONS) {
    assert.ok(prompt.includes(dim), `missing rubric dimension: ${dim}`)
  }
  const retry = buildRetryPrompt(prompt, 'item[0].severity must be blocker|major|minor')
  assert.ok(retry.includes('PREVIOUS OUTPUT WAS INVALID'))
})

test('parseAndValidateFindings accepts valid JSON and fenced JSON and rejects malformed entries', () => {
  const valid = JSON.stringify([
    {
      area: 'Top bar search capsule',
      severity: 'major',
      react: 'Frosted Liquid Glass pill 220x32 with Lucide Search icon',
      native: 'Solid dark pill 260x36 without blur',
      fix_hint: 'Match BarCapsule dimensions and apply glass surface in shell.rs',
      bbox: [12, 8, 260, 40],
    },
  ])
  const ok1 = parseAndValidateFindings(valid)
  assert.equal(ok1.ok, true)
  assert.equal(ok1.findings.length, 1)
  assert.deepEqual(ok1.findings[0].bbox, [12, 8, 260, 40])

  const okFenced = parseAndValidateFindings('```json\n' + valid + '\n```')
  assert.equal(okFenced.ok, true)
  assert.equal(okFenced.findings.length, 1)

  assert.equal(parseAndValidateFindings('not json').ok, false)
  assert.equal(parseAndValidateFindings('{}').ok, false)
  assert.equal(
    parseAndValidateFindings(
      JSON.stringify([{ area: 'x', severity: 'urgent', react: 'a', native: 'b', fix_hint: 'c', bbox: [0, 0, 10, 10] }]),
    ).ok,
    false,
  )
  assert.equal(
    parseAndValidateFindings(
      JSON.stringify([{ area: 'x', severity: 'minor', react: 'a', native: 'b', fix_hint: 'c', bbox: [0, 0, -5, 10] }]),
    ).ok,
    false,
  )
  assert.equal(
    parseAndValidateFindings(
      JSON.stringify([{ area: 'x', severity: 'minor', react: '', native: 'b', fix_hint: 'c', bbox: [0, 0, 10, 10] }]),
    ).ok,
    false,
  )
})

test('runVisionPass retries once on invalid JSON and caches by image pair hash (hit and miss)', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'exo-vision-test-'))
  try {
    const reactPng = join(dir, 'react.png')
    const nativePng = join(dir, 'native.png')
    const cacheDir = join(dir, 'cache')
    await writeFile(reactPng, Buffer.from('react-image-v1'))
    await writeFile(nativePng, Buffer.from('native-image-v1'))

    const sampleFinding = [
      {
        area: 'Hero title',
        severity: 'minor',
        react: '20px semibold Goeist',
        native: '18px regular',
        fix_hint: 'Use 20px semibold token in library.rs',
        bbox: [40, 120, 400, 48],
      },
    ]

    const mock1 = makeMockCli({ findingsByCall: ['{not valid json', sampleFinding] })
    const report = makeSampleReport(reactPng, nativePng)
    const run1 = await runVisionPass({
      report,
      outDir: dir,
      cacheDir,
      runCli: mock1.runCli,
      env: {},
      logger: () => {},
    })
    assert.equal(run1.status, 'ok')
    assert.equal(run1.calls, 2)
    assert.equal(run1.retries, 1)
    assert.equal(run1.cacheHits, 0)
    assert.equal(run1.perState['library-homedeck'].findings.length, 1)
    assert.ok(run1.totalCostUsd > 0)

    const merged = mergeVisionIntoReport(report, run1)
    assert.equal(merged.items[0].severity, 'HIGH')
    assert.equal(merged.items[0].checks.pixels.meanAbsDiff, 18.4)
    assert.equal(merged.items[0].vision.findings.length, 1)

    const mock2 = makeMockCli({ findingsByCall: [[]] })
    const run2 = await runVisionPass({
      report,
      outDir: dir,
      cacheDir,
      runCli: mock2.runCli,
      env: {},
      logger: () => {},
    })
    assert.equal(run2.status, 'ok')
    assert.equal(run2.calls, 0)
    assert.equal(run2.cacheHits, 1)
    assert.equal(run2.totalCostUsd, 0)
    assert.equal(run2.perState['library-homedeck'].status, 'cached')
    assert.equal(run2.perState['library-homedeck'].findings.length, 1)

    await writeFile(nativePng, Buffer.from('native-image-v2-changed'))
    const h1 = run1.perState['library-homedeck'].imageHash
    const h2 = await computePairHash(reactPng, nativePng)
    assert.notEqual(h1, h2)

    const mock3 = makeMockCli({ findingsByCall: [[]] })
    const run3 = await runVisionPass({
      report,
      outDir: dir,
      cacheDir,
      runCli: mock3.runCli,
      env: {},
      logger: () => {},
    })
    assert.equal(run3.calls, 1)
    assert.equal(run3.cacheHits, 0)
    assert.equal(run3.perState['library-homedeck'].status, 'ok')
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})

test('runVisionPass enforces hard stop at budget limit', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'exo-vision-budget-'))
  try {
    const r1 = join(dir, 'r1.png')
    const n1 = join(dir, 'n1.png')
    const r2 = join(dir, 'r2.png')
    const n2 = join(dir, 'n2.png')
    await writeFile(r1, Buffer.from('r1'))
    await writeFile(n1, Buffer.from('n1'))
    await writeFile(r2, Buffer.from('r2'))
    await writeFile(n2, Buffer.from('n2'))

    const report = makeSampleReport(r1, n1, [
      {
        id: 'store-discover',
        room: 'store',
        name: 'Store Discover',
        kind: 'room',
        priority: 1,
        scene: 'store',
        reactShot: r2,
        nativeShot: n2,
        severity: 'MEDIUM',
        score: 40,
        issues: [],
        checks: {
          opened: { status: 'pass', react: true, native: true, detail: 'ok' },
          menus: { status: 'na', equal: true, reactItems: [], nativeItems: [], missing: [], extra: [] },
          cursors: { status: 'na', checked: 0, matched: 0, mismatches: [] },
          glass: { status: 'pass', reactCount: 1, nativeCount: 1, appearedLate: false, detail: 'ok' },
          pixels: { status: 'warn', meanAbsDiff: 10, maxCellDiff: 20, worstCells: [], sheet: null },
        },
      },
    ])

    const expensiveUsage = { prompt_tokens: 100_000, output_tokens: 50_000, thoughts_tokens: 50_000, total_tokens: 200_000 }
    const oneCallCost = estimateCallCostUsd(DEFAULT_VISION_MODEL, expensiveUsage)
    assert.ok(oneCallCost > 0.5)

    const mock = makeMockCli({ findingsByCall: [[], []], usage: expensiveUsage })
    const res = await runVisionPass({
      report,
      outDir: dir,
      cacheDir: join(dir, 'cache'),
      budgetUsd: oneCallCost * 0.9,
      runCli: mock.runCli,
      env: {},
      logger: () => {},
    })
    assert.equal(res.status, 'budget_exceeded')
    assert.equal(res.budgetStopped, true)
    assert.equal(res.calls, 1)
    assert.equal(res.perState['library-homedeck'].status, 'ok')
    assert.equal(res.perState['store-discover'].status, 'budget_exceeded')
  } finally {
    await rm(dir, { recursive: true, force: true })
  }
})

test('checkGeminiAvailability and runVisionPass skip cleanly when CLI is missing, key is unset, or in CI', async () => {
  const missingCli = async () => ({ code: -1, enoent: true, stdout: '', stderr: 'ENOENT' })
  const a1 = await checkGeminiAvailability({ cliPath: 'nonexistent-gemini-api', runCli: missingCli, env: {} })
  assert.equal(a1.available, false)
  assert.equal(a1.reason, 'missing_cli')
  assert.match(a1.message, /not found/i)

  const noKeyCli = async () => ({
    code: 0,
    enoent: false,
    stdout: JSON.stringify({
      credentials: {
        'api-key': { source: 'unset', value: '(not set)' },
        'access-token': { source: 'unset', value: '(not set)' },
      },
    }),
    stderr: '',
  })
  const a2 = await checkGeminiAvailability({ cliPath: 'gemini-api', runCli: noKeyCli, env: {} })
  assert.equal(a2.available, false)
  assert.equal(a2.reason, 'missing_key')
  assert.match(a2.message, /no Gemini API key/i)

  const a3 = await checkGeminiAvailability({ cliPath: 'gemini-api', runCli: missingCli, env: { CI: 'true' } })
  assert.equal(a3.available, false)
  assert.equal(a3.reason, 'ci')
  assert.match(a3.message, /disabled in CI/i)

  const logs = []
  const report = makeSampleReport('a.png', 'b.png')
  const skipped = await runVisionPass({
    report,
    outDir: tmpdir(),
    runCli: missingCli,
    env: {},
    logger: (m) => logs.push(m),
  })
  assert.equal(skipped.status, 'skipped')
  assert.equal(skipped.calls, 0)
  assert.equal(skipped.perState['library-homedeck'].status, 'skipped')
  assert.ok(logs.some((l) => /skipped/i.test(l)))
})
