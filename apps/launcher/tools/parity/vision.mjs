import { execFile } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { renderHtmlGallery, renderMarkdownReport } from './compare.mjs'

const LAUNCHER_ROOT = fileURLToPath(new URL('../..', import.meta.url))
const CROP_PY = fileURLToPath(new URL('./vision-crop.py', import.meta.url))

export const DEFAULT_VISION_MODEL = 'gemini-4-hydrogen'
export const DEFAULT_BUDGET_USD = 15.0
export const RUBRIC_VERSION = 'v1'
export const VALID_SEVERITIES = new Set(['blocker', 'major', 'minor'])

export const MODEL_PRICING_PER_MILLION = {
  'gemini-4-hydrogen': { input: 1.5, output: 7.5 },
  'gemini-4-neon': { input: 3.0, output: 15.0 },
  'gemini-4-argon': { input: 4.0, output: 20.0 },
  'gemini-4-carbon': { input: 4.0, output: 20.0 },
  'gemini-3.8-flash': { input: 1.5, output: 7.5 },
  'gemini-3.5-flash-lite': { input: 0.3, output: 2.5 },
  'gemini-3.1-pro-preview': { input: 2.0, output: 12.0 },
  'gemini-2.5-flash': { input: 0.3, output: 2.5 },
  'gemini-2.5-pro': { input: 2.5, output: 15.0 },
}

const FALLBACK_PRICING = { input: 3.0, output: 15.0 }

export const RUBRIC_DIMENSIONS = [
  'layout and spacing (margins, paddings, gaps, grid tracks, proportions, clipping)',
  'sizes (container dimensions, card widths/heights, button heights, bar height, border radii)',
  'type (family: Geist, weight, size, tracking / letter-spacing, line-height, sentence case)',
  'colour and glass material (Void #000 page/panels, token grays/accents, Liquid Glass blur/tint/hairline on floating surfaces and controls, text contrast)',
  'icons (Lucide icons at 1.5px stroke, icon size, missing/wrong/distorted glyphs)',
  'missing or extra elements (controls, labels, badges, borders, hairlines, art, chrome)',
  'text and copy (exact wording, casing, truncation, wrapping, formatting, placeholder or hardcoded/fake values)',
  'states (hover, selected/active, disabled, empty, loading, open menus/popovers/dropdowns/modals)',
  'alignment (horizontal/vertical alignment, icon-to-text baselines, shared vertical edges and right-aligned control columns)',
]

export function isCiEnvironment(env = process.env) {
  const flags = ['CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'BUILDKITE', 'CIRCLECI', 'GITLAB_CI']
  return flags.some((k) => {
    const v = String(env?.[k] || '').trim().toLowerCase()
    return v === '1' || v === 'true' || v === 'yes'
  })
}

export function resolveGeminiCli(override = null, env = process.env) {
  if (override) return resolve(override)
  if (env?.GEMINI_API_CLI) return resolve(env.GEMINI_API_CLI)
  const localApp = env?.LOCALAPPDATA || ''
  if (localApp) {
    const candidate = join(localApp, 'Programs', 'gemini-api', 'gemini-api.exe')
    if (existsSync(candidate)) return candidate
  }
  return 'gemini-api'
}

export function defaultRunCli(cliPath, args, { timeoutMs = 120000 } = {}) {
  return new Promise((resolvePromise) => {
    execFile(
      cliPath,
      args,
      { windowsHide: true, timeout: timeoutMs, maxBuffer: 16 * 1024 * 1024 },
      (err, stdout, stderr) => {
        if (err && err.code === 'ENOENT') {
          resolvePromise({ code: -1, enoent: true, stdout: '', stderr: String(err.message || '') })
          return
        }
        resolvePromise({
          code: err ? (typeof err.code === 'number' ? err.code : 1) : 0,
          enoent: false,
          stdout: String(stdout || ''),
          stderr: String(stderr || ''),
          error: err ? String(err.message || err) : null,
        })
      },
    )
  })
}

export async function checkGeminiAvailability({ cliPath = null, runCli = defaultRunCli, env = process.env } = {}) {
  if (isCiEnvironment(env)) {
    return {
      available: false,
      reason: 'ci',
      cliPath: null,
      authSource: null,
      message: 'Vision step skipped: disabled in CI.',
    }
  }
  const resolved = resolveGeminiCli(cliPath, env)
  const res = await runCli(resolved, ['whoami', '--no-interactive', '-o', 'json'], { timeoutMs: 15000 })
  if (res.enoent) {
    return {
      available: false,
      reason: 'missing_cli',
      cliPath: resolved,
      authSource: null,
      message: `Vision step skipped: gemini-api CLI not found (${resolved}).`,
    }
  }
  if (res.code === 3) {
    return {
      available: false,
      reason: 'missing_key',
      cliPath: resolved,
      authSource: null,
      message: 'Vision step skipped: no Gemini API key configured (run gemini-api configure).',
    }
  }
  if (res.code !== 0) {
    return {
      available: false,
      reason: 'cli_error',
      cliPath: resolved,
      authSource: null,
      message: `Vision step skipped: gemini-api whoami failed (exit ${res.code}).`,
    }
  }
  let parsed = null
  try {
    parsed = JSON.parse(res.stdout)
  } catch {
    return {
      available: false,
      reason: 'cli_error',
      cliPath: resolved,
      authSource: null,
      message: 'Vision step skipped: could not parse gemini-api whoami output.',
    }
  }
  const keySource = parsed?.credentials?.['api-key']?.source || 'unset'
  const tokenSource = parsed?.credentials?.['access-token']?.source || 'unset'
  if (keySource === 'unset' && tokenSource === 'unset') {
    return {
      available: false,
      reason: 'missing_key',
      cliPath: resolved,
      authSource: null,
      message: 'Vision step skipped: no Gemini API key configured (run gemini-api configure).',
    }
  }
  return {
    available: true,
    reason: 'ok',
    cliPath: resolved,
    authSource: keySource !== 'unset' ? keySource : tokenSource,
    message: null,
  }
}

export function buildVisionPrompt(state, pixelMetrics = null, { hasOverlaySheet = false } = {}) {
  const lines = [
    'You are a strict visual QA reviewer comparing two 1920x1080 desktop UI screenshots of Exo Launcher.',
    'Image 1 is the React (WebView2) reference.',
    'Image 2 is the Native (Rune) implementation.',
  ]
  if (hasOverlaySheet) {
    lines.push(
      'Image 3 is a 2x2 comparison sheet (top-left React reference, top-right Native Rune, bottom-left 50% overlay, bottom-right pixel diff heatmap with 8x6 grid scores).',
    )
  }
  lines.push('')
  lines.push(
    `Screen under test: id="${state.id}", name="${state.name || state.id}", room="${state.room || 'unknown'}", kind="${state.kind || 'room'}".`,
  )
  if (pixelMetrics && typeof pixelMetrics.meanAbsDiff === 'number') {
    const worst = (pixelMetrics.worstCells || [])
      .slice(0, 4)
      .map((c) => `[x=${c.x0}-${c.x1}, y=${c.y0}-${c.y1}, score=${c.score}]`)
      .join(', ')
    lines.push(
      `Ground-truth pixel diff: meanAbsDiff=${pixelMetrics.meanAbsDiff}, maxCellDiff=${pixelMetrics.maxCellDiff ?? 'n/a'}${worst ? `, worstCells=${worst}` : ''}.`,
    )
  }
  lines.push('')
  lines.push('Inspect EVERY visual difference across this fixed rubric:')
  RUBRIC_DIMENSIONS.forEach((dim, idx) => {
    lines.push(`${idx + 1}. ${dim}`)
  })
  lines.push('')
  lines.push('Severity definitions:')
  lines.push(
    '- blocker: missing screen/panel/major section, wrong room or state rendered, unusable overlap/clipping, hardcoded/fake data or raw IDs, missing primary control/menu/modal.',
  )
  lines.push(
    '- major: clear mismatch in layout/spacing/size/alignment, missing secondary control/icon/badge, wrong colour or Liquid Glass material, noticeably wrong type weight/size, broken hover/selected state, truncated text.',
  )
  lines.push(
    '- minor: subtle padding/margin/radius/tracking drift, small colour tint difference, minor icon stroke/size difference, tiny copy polish.',
  )
  lines.push('')
  lines.push('Rules:')
  lines.push('- Report only real visible differences between Image 1 (React) and Image 2 (Native). Do not invent issues.')
  lines.push('- Ignore tiny dynamic timestamps or single-pixel font antialiasing noise unless text is visibly wrong.')
  lines.push('- Merge repeated identical row/item issues into one clear finding covering the group bbox.')
  lines.push('- Keep descriptions concrete and concise (exact labels, positions, pixel/colour/weight differences).')
  lines.push('')
  lines.push(
    'Output ONLY a valid JSON array (no markdown fences, no prose before or after). Return [] if visually identical.',
  )
  lines.push('Each item must be an object with exact keys:')
  lines.push(
    '{"area": string, "severity": "blocker" | "major" | "minor", "react": string, "native": string, "fix_hint": string, "bbox": [x, y, width, height]}',
  )
  lines.push('bbox must be 4 integer pixel coordinates [x, y, width, height] inside the 1920x1080 frame.')
  return lines.join('\n')
}

export function buildRetryPrompt(basePrompt, validationError) {
  return [
    basePrompt,
    '',
    `PREVIOUS OUTPUT WAS INVALID: ${validationError}.`,
    'Reply ONLY with a strict JSON array matching [{area, severity, react, native, fix_hint, bbox}]. No markdown fences, no commentary.',
  ].join('\n')
}

function stripJsonFences(text) {
  const trimmed = String(text ?? '').trim()
  const fence = trimmed.match(/^```(?:json)?\s*([\s\S]*?)\s*```$/i)
  return fence ? fence[1].trim() : trimmed
}

function normalizeBbox(bbox) {
  if (bbox === null) return null
  if (!Array.isArray(bbox) || bbox.length !== 4) return undefined
  const nums = bbox.map((v) => Number(v))
  if (nums.some((n) => !Number.isFinite(n))) return undefined
  let [x, y, w, h] = nums.map((n) => Math.round(n))
  if (w < 0 || h < 0) return undefined
  x = Math.max(0, Math.min(1920, x))
  y = Math.max(0, Math.min(1080, y))
  w = Math.max(0, Math.min(1920 - x, w))
  h = Math.max(0, Math.min(1080 - y, h))
  return [x, y, w, h]
}

export function parseAndValidateFindings(rawText) {
  const cleaned = stripJsonFences(rawText)
  if (!cleaned) {
    return { ok: false, error: 'empty response text' }
  }
  let parsed
  try {
    parsed = JSON.parse(cleaned)
  } catch (err) {
    return { ok: false, error: `JSON parse failed: ${err.message}` }
  }
  if (!Array.isArray(parsed)) {
    return { ok: false, error: 'root JSON value must be an array' }
  }
  const findings = []
  for (let i = 0; i < parsed.length; i++) {
    const item = parsed[i]
    if (!item || typeof item !== 'object' || Array.isArray(item)) {
      return { ok: false, error: `item[${i}] must be an object` }
    }
    for (const field of ['area', 'react', 'native', 'fix_hint']) {
      if (typeof item[field] !== 'string' || !item[field].trim()) {
        return { ok: false, error: `item[${i}].${field} must be a non-empty string` }
      }
    }
    const sev = typeof item.severity === 'string' ? item.severity.trim().toLowerCase() : ''
    if (!VALID_SEVERITIES.has(sev)) {
      return { ok: false, error: `item[${i}].severity must be blocker|major|minor` }
    }
    if (!('bbox' in item)) {
      return { ok: false, error: `item[${i}].bbox is required` }
    }
    const bbox = normalizeBbox(item.bbox)
    if (bbox === undefined) {
      return { ok: false, error: `item[${i}].bbox must be [x, y, w, h] of non-negative numbers or null` }
    }
    findings.push({
      area: item.area.trim(),
      severity: sev,
      react: item.react.trim(),
      native: item.native.trim(),
      fix_hint: item.fix_hint.trim(),
      bbox,
    })
  }
  return { ok: true, findings }
}

export function computePairHashFromBuffers(reactBuf, nativeBuf, { model = DEFAULT_VISION_MODEL, rubricVersion = RUBRIC_VERSION } = {}) {
  const h = createHash('sha256')
  h.update(reactBuf)
  h.update('\0')
  h.update(nativeBuf)
  h.update('\0')
  h.update(String(model))
  h.update('\0')
  h.update(String(rubricVersion))
  return h.digest('hex')
}

export async function computePairHash(reactPath, nativePath, opts = {}) {
  const [rBuf, nBuf] = await Promise.all([readFile(reactPath), readFile(nativePath)])
  return computePairHashFromBuffers(rBuf, nBuf, opts)
}

export function normalizeUsage(usage) {
  const prompt = Number(usage?.prompt_tokens || 0)
  const output = Number(usage?.output_tokens || 0)
  const thoughts = Number(usage?.thoughts_tokens || 0)
  const total = Number(usage?.total_tokens || prompt + output + thoughts)
  return {
    prompt_tokens: Number.isFinite(prompt) ? prompt : 0,
    output_tokens: Number.isFinite(output) ? output : 0,
    thoughts_tokens: Number.isFinite(thoughts) ? thoughts : 0,
    total_tokens: Number.isFinite(total) ? total : 0,
  }
}

export function addUsage(a, b) {
  return {
    prompt_tokens: (a?.prompt_tokens || 0) + (b?.prompt_tokens || 0),
    output_tokens: (a?.output_tokens || 0) + (b?.output_tokens || 0),
    thoughts_tokens: (a?.thoughts_tokens || 0) + (b?.thoughts_tokens || 0),
    total_tokens: (a?.total_tokens || 0) + (b?.total_tokens || 0),
  }
}

export function estimateCallCostUsd(model, usage) {
  const rates = MODEL_PRICING_PER_MILLION[model] || FALLBACK_PRICING
  const u = normalizeUsage(usage)
  const billableOutput = u.output_tokens + u.thoughts_tokens
  return (u.prompt_tokens * rates.input + billableOutput * rates.output) / 1_000_000
}

export function countFindingSeverities(findings = []) {
  const counts = { blocker: 0, major: 0, minor: 0 }
  for (const f of findings) {
    if (f?.severity && f.severity in counts) counts[f.severity]++
  }
  return counts
}

async function readCacheEntry(cacheDir, hash) {
  if (!cacheDir || !hash) return null
  const p = join(cacheDir, `${hash}.json`)
  if (!existsSync(p)) return null
  try {
    const data = JSON.parse(await readFile(p, 'utf8'))
    if (Array.isArray(data?.findings)) return data
  } catch {}
  return null
}

async function writeCacheEntry(cacheDir, hash, entry) {
  if (!cacheDir || !hash) return
  await mkdir(cacheDir, { recursive: true })
  await writeFile(join(cacheDir, `${hash}.json`), JSON.stringify(entry, null, 2))
}

function resolveShotPath(outDir, shotPath) {
  if (!shotPath) return null
  if (existsSync(shotPath)) return shotPath
  const joined = join(outDir, shotPath)
  if (existsSync(joined)) return joined
  return null
}

async function invokeAnalyze({ cliPath, model, images, prompt, runCli }) {
  const args = ['analyze', '--no-interactive', '-o', 'json', '-m', model]
  for (const img of images) {
    args.push('-i', img)
  }
  args.push(prompt)
  const res = await runCli(cliPath, args, { timeoutMs: 180000 })
  if (res.enoent) {
    return { ok: false, error: 'gemini-api CLI not found', usage: normalizeUsage(null) }
  }
  if (res.code !== 0) {
    return {
      ok: false,
      error: (res.stderr || res.error || `exit ${res.code}`).trim().slice(0, 300),
      usage: normalizeUsage(null),
    }
  }
  try {
    const parsed = JSON.parse(res.stdout)
    return {
      ok: true,
      text: String(parsed?.text ?? ''),
      model: String(parsed?.model || model),
      usage: normalizeUsage(parsed?.usage),
    }
  } catch (err) {
    return { ok: false, error: `invalid CLI JSON envelope: ${err.message}`, usage: normalizeUsage(null) }
  }
}

export async function generateFindingCrops({ items, outDir, cropsDir }) {
  const manifest = []
  for (const item of items) {
    const findings = item.vision?.findings
    if (!Array.isArray(findings) || findings.length === 0) continue
    const reactAbs = resolveShotPath(outDir, item.reactShot)
    const nativeAbs = resolveShotPath(outDir, item.nativeShot)
    if (!reactAbs || !nativeAbs) continue
    for (let i = 0; i < findings.length; i++) {
      const f = findings[i]
      if (!Array.isArray(f.bbox) || f.bbox[2] <= 0 || f.bbox[3] <= 0) continue
      const outPath = join(cropsDir, `${item.id}-${String(i + 1).padStart(2, '0')}.png`)
      manifest.push({
        stateId: item.id,
        index: i,
        reactPath: reactAbs,
        nativePath: nativeAbs,
        bbox: f.bbox,
        outPath,
      })
    }
  }
  if (manifest.length === 0) return 0
  await mkdir(cropsDir, { recursive: true })
  const manifestPath = join(cropsDir, 'manifest.json')
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2))
  const runRes = await new Promise((resolvePromise) => {
    execFile('python', ['-I', CROP_PY, manifestPath], { windowsHide: true, timeout: 120000 }, (err, stdout, stderr) => {
      resolvePromise({ code: err ? 1 : 0, stdout: String(stdout || ''), stderr: String(stderr || '') })
    })
  })
  if (runRes.code !== 0) return 0
  let created = 0
  for (const entry of manifest) {
    if (existsSync(entry.outPath)) {
      const item = items.find((it) => it.id === entry.stateId)
      if (item?.vision?.findings?.[entry.index]) {
        item.vision.findings[entry.index].crop = relative(outDir, entry.outPath).replaceAll('\\', '/')
        created++
      }
    }
  }
  return created
}

export async function runVisionPass({
  report,
  outDir,
  model = DEFAULT_VISION_MODEL,
  budgetUsd = DEFAULT_BUDGET_USD,
  cacheDir = join(LAUNCHER_ROOT, 'parity-report', '.vision-cache'),
  cliPath = null,
  runCli = defaultRunCli,
  env = process.env,
  logger = (line) => console.log(line),
  includeOverlay = true,
  makeCrops = false,
} = {}) {
  const avail = await checkGeminiAvailability({ cliPath, runCli, env })
  const zeroUsage = normalizeUsage(null)
  if (!avail.available) {
    logger(avail.message)
    const perState = {}
    for (const item of report.items || []) {
      perState[item.id] = {
        status: 'skipped',
        reason: avail.reason,
        model,
        imageHash: null,
        findings: [],
        counts: { blocker: 0, major: 0, minor: 0 },
        usage: zeroUsage,
        estimatedCostUsd: 0,
      }
    }
    return {
      status: 'skipped',
      reason: avail.reason,
      message: avail.message,
      model,
      budgetUsd,
      totalCostUsd: 0,
      calls: 0,
      cacheHits: 0,
      retries: 0,
      budgetStopped: false,
      totalUsage: zeroUsage,
      counts: { blocker: 0, major: 0, minor: 0 },
      perState,
    }
  }

  let totalCostUsd = 0
  let calls = 0
  let cacheHits = 0
  let retries = 0
  let budgetStopped = false
  let totalUsage = normalizeUsage(null)
  const totalCounts = { blocker: 0, major: 0, minor: 0 }
  const perState = {}

  for (const item of report.items || []) {
    if (item.checks?.opened && item.checks.opened.react === false) {
      perState[item.id] = {
        status: 'skipped',
        reason: 'react_precondition_unmet',
        model,
        imageHash: null,
        findings: [],
        counts: { blocker: 0, major: 0, minor: 0 },
        usage: zeroUsage,
        estimatedCostUsd: 0,
      }
      logger(`[vision] ${item.id} (skipped: react precondition unmet)`)
      continue
    }
    const reactAbs = resolveShotPath(outDir, item.reactShot)
    const nativeAbs = resolveShotPath(outDir, item.nativeShot)
    if (!reactAbs || !nativeAbs) {
      perState[item.id] = {
        status: 'skipped',
        reason: 'missing_screenshot',
        model,
        imageHash: null,
        findings: [],
        counts: { blocker: 0, major: 0, minor: 0 },
        usage: zeroUsage,
        estimatedCostUsd: 0,
      }
      continue
    }

    const imageHash = await computePairHash(reactAbs, nativeAbs, { model, rubricVersion: RUBRIC_VERSION })
    const cached = await readCacheEntry(cacheDir, imageHash)
    if (cached) {
      cacheHits++
      const counts = countFindingSeverities(cached.findings)
      totalCounts.blocker += counts.blocker
      totalCounts.major += counts.major
      totalCounts.minor += counts.minor
      perState[item.id] = {
        status: 'cached',
        model,
        imageHash,
        findings: cached.findings,
        counts,
        usage: zeroUsage,
        estimatedCostUsd: 0,
      }
      logger(
        `[vision] ${item.id} (cached) findings=${counts.blocker}B/${counts.major}M/${counts.minor}m cost=$0.0000 total=$${totalCostUsd.toFixed(4)}/$${budgetUsd.toFixed(2)}`,
      )
      continue
    }

    if (totalCostUsd >= budgetUsd) {
      budgetStopped = true
      perState[item.id] = {
        status: 'budget_exceeded',
        model,
        imageHash,
        findings: [],
        counts: { blocker: 0, major: 0, minor: 0 },
        usage: zeroUsage,
        estimatedCostUsd: 0,
      }
      continue
    }

    const sheetAbs = includeOverlay ? resolveShotPath(outDir, item.checks?.pixels?.sheet) : null
    const images = sheetAbs ? [reactAbs, nativeAbs, sheetAbs] : [reactAbs, nativeAbs]
    const basePrompt = buildVisionPrompt(item, item.checks?.pixels || null, { hasOverlaySheet: Boolean(sheetAbs) })

    let stateUsage = normalizeUsage(null)
    let stateCost = 0
    let validated = null
    let lastError = null

    for (let attempt = 0; attempt < 2; attempt++) {
      if (attempt > 0) {
        if (totalCostUsd >= budgetUsd) {
          budgetStopped = true
          lastError = `budget limit $${budgetUsd.toFixed(2)} reached before retry`
          break
        }
        retries++
      }
      const prompt = attempt === 0 ? basePrompt : buildRetryPrompt(basePrompt, lastError)
      const callRes = await invokeAnalyze({
        cliPath: avail.cliPath,
        model,
        images,
        prompt,
        runCli,
      })
      calls++
      const callCost = estimateCallCostUsd(callRes.model || model, callRes.usage)
      stateUsage = addUsage(stateUsage, callRes.usage)
      totalUsage = addUsage(totalUsage, callRes.usage)
      stateCost += callCost
      totalCostUsd += callCost

      if (!callRes.ok) {
        lastError = callRes.error
        break
      }
      const parsed = parseAndValidateFindings(callRes.text)
      if (parsed.ok) {
        validated = parsed.findings
        break
      }
      lastError = parsed.error
    }

    if (validated !== null) {
      const counts = countFindingSeverities(validated)
      totalCounts.blocker += counts.blocker
      totalCounts.major += counts.major
      totalCounts.minor += counts.minor
      await writeCacheEntry(cacheDir, imageHash, {
        stateId: item.id,
        model,
        rubricVersion: RUBRIC_VERSION,
        imageHash,
        cachedAt: new Date().toISOString(),
        findings: validated,
      })
      perState[item.id] = {
        status: 'ok',
        model,
        imageHash,
        findings: validated,
        counts,
        usage: stateUsage,
        estimatedCostUsd: Number(stateCost.toFixed(6)),
      }
      logger(
        `[vision] ${item.id} (ok) findings=${counts.blocker}B/${counts.major}M/${counts.minor}m tokens=${stateUsage.prompt_tokens}/${stateUsage.output_tokens}/${stateUsage.thoughts_tokens} cost=$${stateCost.toFixed(4)} total=$${totalCostUsd.toFixed(4)}/$${budgetUsd.toFixed(2)}`,
      )
    } else {
      const status = budgetStopped ? 'budget_exceeded' : 'invalid'
      perState[item.id] = {
        status,
        error: lastError,
        model,
        imageHash,
        findings: [],
        counts: { blocker: 0, major: 0, minor: 0 },
        usage: stateUsage,
        estimatedCostUsd: Number(stateCost.toFixed(6)),
      }
      logger(
        `[vision] ${item.id} (${status}) error=${lastError} tokens=${stateUsage.prompt_tokens}/${stateUsage.output_tokens}/${stateUsage.thoughts_tokens} cost=$${stateCost.toFixed(4)} total=$${totalCostUsd.toFixed(4)}/$${budgetUsd.toFixed(2)}`,
      )
    }

    if (totalCostUsd >= budgetUsd) {
      budgetStopped = true
      logger(`[vision] Budget hard stop reached: $${totalCostUsd.toFixed(4)} >= $${budgetUsd.toFixed(2)}`)
    }
  }

  const visionRun = {
    status: budgetStopped ? 'budget_exceeded' : 'ok',
    reason: budgetStopped ? 'budget_exceeded' : 'ok',
    message: budgetStopped ? `Stopped at budget limit $${budgetUsd.toFixed(2)}` : null,
    model,
    budgetUsd,
    totalCostUsd: Number(totalCostUsd.toFixed(6)),
    calls,
    cacheHits,
    retries,
    budgetStopped,
    totalUsage,
    counts: totalCounts,
    perState,
  }

  const merged = mergeVisionIntoReport(report, visionRun)
  if (makeCrops) {
    await generateFindingCrops({ items: merged.items, outDir, cropsDir: join(outDir, 'crops') })
  }
  return visionRun
}

export function mergeVisionIntoReport(report, visionRun) {
  const perState = visionRun?.perState || {}
  const items = (report.items || []).map((item) => {
    const v = perState[item.id] || null
    return {
      ...item,
      vision: v,
    }
  })
  const visionSummary = {
    status: visionRun?.status || 'skipped',
    reason: visionRun?.reason || null,
    message: visionRun?.message || null,
    model: visionRun?.model || DEFAULT_VISION_MODEL,
    budgetUsd: visionRun?.budgetUsd ?? DEFAULT_BUDGET_USD,
    totalCostUsd: visionRun?.totalCostUsd ?? 0,
    calls: visionRun?.calls ?? 0,
    cacheHits: visionRun?.cacheHits ?? 0,
    retries: visionRun?.retries ?? 0,
    budgetStopped: Boolean(visionRun?.budgetStopped),
    totalUsage: visionRun?.totalUsage || normalizeUsage(null),
    counts: visionRun?.counts || { blocker: 0, major: 0, minor: 0 },
  }
  return {
    ...report,
    visionSummary,
    items,
  }
}

export async function applyVisionToReportDir({
  reportDir,
  model = DEFAULT_VISION_MODEL,
  budgetUsd = DEFAULT_BUDGET_USD,
  cacheDir = join(LAUNCHER_ROOT, 'parity-report', '.vision-cache'),
  cliPath = null,
  includeOverlay = true,
  makeCrops = true,
  runCli = defaultRunCli,
  env = process.env,
  logger = (line) => console.log(line),
} = {}) {
  const outDir = resolve(reportDir)
  const resultsPath = join(outDir, 'results.json')
  if (!existsSync(resultsPath)) {
    throw new Error(`No results.json found at ${resultsPath}`)
  }
  const report = JSON.parse(await readFile(resultsPath, 'utf8'))
  const visionRun = await runVisionPass({
    report,
    outDir,
    model,
    budgetUsd,
    cacheDir,
    cliPath,
    runCli,
    env,
    logger,
    includeOverlay,
    makeCrops: false,
  })
  const merged = mergeVisionIntoReport(report, visionRun)
  if (makeCrops && visionRun.status !== 'skipped') {
    await generateFindingCrops({ items: merged.items, outDir, cropsDir: join(outDir, 'crops') })
  }
  await writeFile(resultsPath, JSON.stringify(merged, null, 2))
  await writeFile(join(outDir, 'report.md'), renderMarkdownReport(merged))
  await writeFile(join(outDir, 'index.html'), renderHtmlGallery(merged))
  return merged
}

function parseCliArgs(argv) {
  const opts = {
    reportDir: null,
    model: DEFAULT_VISION_MODEL,
    budgetUsd: DEFAULT_BUDGET_USD,
    cacheDir: join(LAUNCHER_ROOT, 'parity-report', '.vision-cache'),
    cliPath: null,
    noOverlay: false,
    noCrops: false,
  }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (a === '--report-dir') opts.reportDir = argv[++i] || null
    else if (a === '--model') opts.model = argv[++i] || DEFAULT_VISION_MODEL
    else if (a === '--budget') opts.budgetUsd = Number(argv[++i]) || DEFAULT_BUDGET_USD
    else if (a === '--cache-dir') opts.cacheDir = argv[++i] || opts.cacheDir
    else if (a === '--cli') opts.cliPath = argv[++i] || null
    else if (a === '--no-overlay') opts.noOverlay = true
    else if (a === '--no-crops') opts.noCrops = true
  }
  return opts
}

const isMain = process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url
if (isMain) {
  const opts = parseCliArgs(process.argv.slice(2))
  if (!opts.reportDir) {
    console.error('Usage: node tools/parity/vision.mjs --report-dir <dir> [--model <id>] [--budget <usd>]')
    process.exit(2)
  }
  applyVisionToReportDir({
    reportDir: opts.reportDir,
    model: opts.model,
    budgetUsd: opts.budgetUsd,
    cacheDir: opts.cacheDir,
    cliPath: opts.cliPath,
    includeOverlay: !opts.noOverlay,
    makeCrops: !opts.noCrops,
  })
    .then((merged) => {
      const vs = merged.visionSummary
      console.log(
        `Vision pass ${vs.status}: calls=${vs.calls}, cacheHits=${vs.cacheHits}, findings=${vs.counts.blocker}B/${vs.counts.major}M/${vs.counts.minor}m, spend=$${vs.totalCostUsd.toFixed(4)}`,
      )
    })
    .catch((err) => {
      console.error(err?.stack || String(err))
      process.exit(1)
    })
}
