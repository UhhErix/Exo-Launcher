import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir, writeFile } from 'node:fs/promises'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { ROOMS } from './states.mjs'

const COMPARE_PY = fileURLToPath(new URL('./parity-compare.py', import.meta.url))

function runComparePy(refPath, snapPath, outPath) {
  return new Promise((resolvePromise) => {
    execFile(
      'python',
      ['-I', COMPARE_PY, refPath, snapPath, outPath, '--json', '-'],
      { windowsHide: true, timeout: 30000 },
      (err, stdout, stderr) => {
        if (err) {
          resolvePromise({ error: (stderr || err.message || 'compare failed').trim() })
          return
        }
        try {
          const line = String(stdout || '').trim().split(/\r?\n/).pop()
          resolvePromise(JSON.parse(line))
        } catch {
          resolvePromise({ error: 'invalid JSON from parity-compare.py' })
        }
      },
    )
  })
}

const normLabel = (s) => String(s || '').replace(/\s+/g, ' ').trim().toLowerCase()

export function compareMenus(state, reactRec, nativeRec) {
  const rItems = (reactRec?.menuItems || []).map((s) => String(s).trim()).filter(Boolean)
  const nItems = (nativeRec?.menuItems || []).map((s) => String(s).trim()).filter(Boolean)
  if (!state.expectMenu && rItems.length === 0 && nItems.length === 0) {
    return { status: 'na', equal: true, reactItems: [], nativeItems: [], missing: [], extra: [] }
  }
  const rNorm = new Map(rItems.map((s) => [normLabel(s), s]))
  const nNorm = new Map(nItems.map((s) => [normLabel(s), s]))
  const missing = []
  const extra = []
  for (const [k, v] of rNorm) {
    if (!nNorm.has(k)) missing.push(v)
  }
  for (const [k, v] of nNorm) {
    if (!rNorm.has(k)) extra.push(v)
  }
  const equal = missing.length === 0 && extra.length === 0 && (rItems.length > 0 || !state.expectMenu)
  const status =
    rItems.length === 0 && nItems.length === 0
      ? reactRec?.opened
        ? 'fail'
        : 'na'
      : equal
        ? 'pass'
        : 'fail'
  return { status, equal, reactItems: rItems, nativeItems: nItems, missing, extra }
}

export function compareCursors(reactRec, nativeRec) {
  const rList = reactRec?.cursors || []
  const nList = nativeRec?.cursors || []
  if (rList.length === 0 || nList.length === 0) {
    return { status: 'na', checked: 0, matched: 0, mismatches: [] }
  }
  const nByName = new Map(nList.map((c) => [c.name, c]))
  const mismatches = []
  let checked = 0
  let matched = 0
  for (const r of rList) {
    const n = nByName.get(r.name)
    if (!n) continue
    checked++
    const rc = r.cursor === 'auto' ? 'default' : r.cursor
    const nc = n.cursor === 'auto' ? 'default' : n.cursor
    if (rc === nc) {
      matched++
    } else {
      mismatches.push({ name: r.name, x: r.x, y: r.y, react: rc, native: nc, hitKey: n.hitKey || null })
    }
  }
  const status = checked === 0 ? 'na' : mismatches.length === 0 ? 'pass' : 'fail'
  return { status, checked, matched, mismatches }
}

export function compareGlass(state, reactRec, nativeRec) {
  const rCount = reactRec?.glass?.length || 0
  const nCount = nativeRec?.glass?.length || 0
  const late = Boolean(nativeRec?.glassAppearedLate)
  if (late) {
    return {
      status: 'fail',
      reactCount: rCount,
      nativeCount: nCount,
      appearedLate: true,
      detail: `glass appeared late (frame0=${nativeRec?.glassAtFrame0?.length || 0}, final=${nCount})`,
    }
  }
  if ((state.expectGlass || rCount > 0) && nCount === 0) {
    return {
      status: 'fail',
      reactCount: rCount,
      nativeCount: nCount,
      appearedLate: false,
      detail: `missing glass (react=${rCount}, native=0)`,
    }
  }
  if (rCount === 0 && nCount === 0) {
    return { status: 'pass', reactCount: 0, nativeCount: 0, appearedLate: false, detail: 'none on either' }
  }
  const ratio = Math.abs(rCount - nCount)
  if (ratio >= 4) {
    return {
      status: 'warn',
      reactCount: rCount,
      nativeCount: nCount,
      appearedLate: false,
      detail: `glass count differs (react=${rCount}, native=${nCount})`,
    }
  }
  return {
    status: 'pass',
    reactCount: rCount,
    nativeCount: nCount,
    appearedLate: false,
    detail: `react=${rCount}, native=${nCount}`,
  }
}

export function scoreStateResult(state, checks) {
  const issues = []
  let score = 0
  const prioBoost = state.priority === 1 ? 30 : state.priority === 2 ? 15 : 0

  if (checks.opened.status === 'fail') {
    score += 100
    issues.push(checks.opened.detail)
  }
  if (checks.menus.status === 'fail') {
    score += 50
    issues.push(
      `menu mismatch (react=${checks.menus.reactItems.length}, native=${checks.menus.nativeItems.length}, missing=${checks.menus.missing.length})`,
    )
  }
  if (checks.glass.status === 'fail') {
    score += 35
    issues.push(checks.glass.detail)
  } else if (checks.glass.status === 'warn') {
    score += 10
    issues.push(checks.glass.detail)
  }
  if (checks.cursors.status === 'fail') {
    const ratio = checks.cursors.checked > 0 ? checks.cursors.mismatches.length / checks.cursors.checked : 0
    score += Math.round(25 + ratio * 25)
    issues.push(`cursor mismatches (${checks.cursors.mismatches.length}/${checks.cursors.checked})`)
  }
  if (checks.pixels.status === 'fail') {
    score += Math.min(60, Math.round((checks.pixels.meanAbsDiff || 0) * 1.5))
    issues.push(`pixel diff ${checks.pixels.meanAbsDiff} (max cell ${checks.pixels.maxCellDiff})`)
  } else if (checks.pixels.status === 'warn') {
    score += Math.round((checks.pixels.meanAbsDiff || 0) * 0.8)
    issues.push(`pixel diff ${checks.pixels.meanAbsDiff}`)
  }

  if (score > 0) score += prioBoost

  let severity = 'PASS'
  if (checks.opened.status === 'skip' && score === 0) severity = 'SKIP'
  else if (checks.opened.status === 'fail' || score >= 110) severity = 'CRITICAL'
  else if (score >= 65) severity = 'HIGH'
  else if (score >= 30) severity = 'MEDIUM'
  else if (score > 0) severity = 'LOW'

  return { score, severity, issues }
}

export async function compareAll({ states, reactResults, nativeResults, outDir }) {
  const sheetsDir = join(outDir, 'sheets')
  await mkdir(sheetsDir, { recursive: true })
  const items = []

  for (const state of states) {
    const r = reactResults[state.id] || null
    const n = nativeResults[state.id] || null

    const reactOpened = Boolean(r?.opened)
    const nativeOpened = Boolean(n?.opened)
    let opened
    if (reactOpened && nativeOpened) {
      opened = { status: 'pass', react: true, native: true, detail: 'opened in both' }
    } else if (reactOpened && !nativeOpened) {
      opened = {
        status: 'fail',
        react: true,
        native: false,
        detail: `failed to open in native (scene=${state.native?.scene || 'library'}${n?.error ? `: ${n.error}` : ''})`,
      }
    } else if (!reactOpened && nativeOpened) {
      opened = {
        status: 'skip',
        react: false,
        native: true,
        detail: `react precondition unmet (${r?.stepError || 'not visible'}), native opened`,
      }
    } else {
      opened = {
        status: 'skip',
        react: false,
        native: false,
        detail: `not reached on either (${r?.stepError || 'unavailable in fresh test instance'})`,
      }
    }

    const isSkipped = opened.status === 'skip'
    const menus = isSkipped
      ? { status: 'na', equal: true, reactItems: [], nativeItems: [], missing: [], extra: [] }
      : compareMenus(state, r, n)
    const cursors = isSkipped
      ? { status: 'na', checked: 0, matched: 0, mismatches: [] }
      : compareCursors(r, n)
    const glass = isSkipped
      ? { status: 'pass', reactCount: 0, nativeCount: 0, appearedLate: false, detail: 'skipped' }
      : compareGlass(state, r, n)

    let pixels = { status: 'na', meanAbsDiff: null, maxCellDiff: null, worstCells: [], sheet: null }
    if (!isSkipped && r?.screenshot && n?.screenshot && existsSync(r.screenshot) && existsSync(n.screenshot)) {
      const sheetPath = join(sheetsDir, `${state.id}-compare.png`)
      const cmp = await runComparePy(r.screenshot, n.screenshot, sheetPath)
      if (!cmp.error) {
        const diff = cmp.meanAbsDiff
        const pStatus = diff <= 6 ? 'pass' : diff <= 15 ? 'warn' : 'fail'
        pixels = {
          status: pStatus,
          meanAbsDiff: cmp.meanAbsDiff,
          maxCellDiff: cmp.maxCellDiff,
          worstCells: cmp.worstCells || [],
          sheet: relative(outDir, sheetPath).replaceAll('\\', '/'),
        }
      } else {
        pixels = { status: 'fail', meanAbsDiff: null, maxCellDiff: null, worstCells: [], sheet: null, error: cmp.error }
      }
    }

    const checks = { opened, menus, cursors, glass, pixels }
    const ranking = scoreStateResult(state, checks)
    items.push({
      id: state.id,
      room: state.room,
      name: state.name,
      kind: state.kind,
      priority: state.priority,
      scene: state.native?.scene || 'library',
      reactShot: r?.screenshot ? relative(outDir, r.screenshot).replaceAll('\\', '/') : null,
      nativeShot: n?.screenshot ? relative(outDir, n.screenshot).replaceAll('\\', '/') : null,
      checks,
      ...ranking,
    })
  }

  const summary = buildSummary(items)
  const payload = { generatedAt: new Date().toISOString(), summary, items }
  await writeFile(join(outDir, 'results.json'), JSON.stringify(payload, null, 2))
  const md = renderMarkdownReport(payload)
  await writeFile(join(outDir, 'report.md'), md)
  const html = renderHtmlGallery(payload)
  await writeFile(join(outDir, 'index.html'), html)
  return payload
}

export function buildSummary(items) {
  const counts = { total: items.length, CRITICAL: 0, HIGH: 0, MEDIUM: 0, LOW: 0, PASS: 0, SKIP: 0 }
  const checks = {
    openFail: 0,
    menuFail: 0,
    cursorFail: 0,
    glassFail: 0,
    glassLate: 0,
    pixelFail: 0,
  }
  let pixelSum = 0
  let pixelCount = 0
  for (const item of items) {
    counts[item.severity] = (counts[item.severity] || 0) + 1
    if (item.checks.opened.status === 'fail') checks.openFail++
    if (item.checks.menus.status === 'fail') checks.menuFail++
    if (item.checks.cursors.status === 'fail') checks.cursorFail++
    if (item.checks.glass.status === 'fail') checks.glassFail++
    if (item.checks.glass.appearedLate) checks.glassLate++
    if (item.checks.pixels.status === 'fail') checks.pixelFail++
    if (typeof item.checks.pixels.meanAbsDiff === 'number') {
      pixelSum += item.checks.pixels.meanAbsDiff
      pixelCount++
    }
  }
  return {
    counts,
    checks,
    avgMeanAbsDiff: pixelCount ? Number((pixelSum / pixelCount).toFixed(2)) : null,
  }
}

export function renderMarkdownReport(payload) {
  const { generatedAt, summary, items } = payload
  const lines = []
  lines.push('# Exo Launcher Interactive Parity Report')
  lines.push('')
  lines.push(`Generated: ${generatedAt}`)
  lines.push('')
  lines.push('## Summary')
  lines.push('')
  lines.push(
    `- Total states checked: **${summary.counts.total}** | CRITICAL: **${summary.counts.CRITICAL}** | HIGH: **${summary.counts.HIGH}** | MEDIUM: **${summary.counts.MEDIUM}** | LOW: **${summary.counts.LOW}** | PASS: **${summary.counts.PASS}** | SKIP: **${summary.counts.SKIP}**`,
  )
  lines.push(
    `- Failures by dimension: Open **${summary.checks.openFail}** | Menus **${summary.checks.menuFail}** | Cursors **${summary.checks.cursorFail}** | Glass **${summary.checks.glassFail}** (late: **${summary.checks.glassLate}**) | Pixels (>15 diff) **${summary.checks.pixelFail}**`,
  )
  if (summary.avgMeanAbsDiff !== null) {
    lines.push(`- Mean absolute pixel diff across compared states: **${summary.avgMeanAbsDiff}** (0 = identical)`)
  }
  if (payload.visionSummary) {
    const vs = payload.visionSummary
    lines.push(
      `- Vision QA (${vs.model}, status=${vs.status}): **${vs.counts.blocker}** blockers, **${vs.counts.major}** major, **${vs.counts.minor}** minor | calls=${vs.calls}, cacheHits=${vs.cacheHits}, spend=$${Number(vs.totalCostUsd || 0).toFixed(4)}`,
    )
  }
  lines.push('')
  lines.push('## Ranked Gap List (Grouped by Room)')
  lines.push('')

  const byRoom = new Map()
  for (const room of ROOMS) byRoom.set(room, [])
  for (const item of items) {
    if (!byRoom.has(item.room)) byRoom.set(item.room, [])
    byRoom.get(item.room).push(item)
  }

  const roomRanks = []
  for (const [room, roomItems] of byRoom) {
    const gaps = roomItems.filter((i) => i.severity !== 'PASS' && i.severity !== 'SKIP').sort((a, b) => b.score - a.score)
    const totalScore = gaps.reduce((acc, g) => acc + g.score, 0)
    roomRanks.push({ room, gaps, roomItems, totalScore })
  }
  roomRanks.sort((a, b) => b.totalScore - a.totalScore)

  for (const { room, gaps, roomItems, totalScore } of roomRanks) {
    lines.push(`### ${room} (${gaps.length}/${roomItems.length} gaps, severity sum ${totalScore})`)
    lines.push('')
    if (gaps.length === 0) {
      lines.push('No gaps detected in reachable states.')
      lines.push('')
      continue
    }
    lines.push('| Rank | Severity | State | Kind | Open | Menus | Cursors | Glass | Pixel Diff | Gaps |')
    lines.push('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
    gaps.forEach((g, idx) => {
      const c = g.checks
      const curStr = c.cursors.checked > 0 ? `${c.cursors.matched}/${c.cursors.checked}` : 'n/a'
      const menuStr = c.menus.status === 'na' ? 'n/a' : `${c.menus.nativeItems.length}/${c.menus.reactItems.length}`
      const pxStr = c.pixels.meanAbsDiff !== null ? `${c.pixels.meanAbsDiff}` : 'n/a'
      const detail = g.issues.join('; ').replaceAll('|', '\\|')
      lines.push(
        `| ${idx + 1} | **${g.severity}** | \`${g.id}\` (${g.name}) | ${g.kind} | ${c.opened.status} | ${menuStr} | ${curStr} | ${c.glass.status} | ${pxStr} | ${detail} |`,
      )
    })
    lines.push('')
    for (const g of gaps.slice(0, 6)) {
      const details = []
      if (g.checks.menus.status === 'fail') {
        details.push(
          `  - Menus: missing [${g.checks.menus.missing.slice(0, 8).join(', ')}]` +
            (g.checks.menus.extra.length ? `; extra [${g.checks.menus.extra.slice(0, 8).join(', ')}]` : ''),
        )
      }
      if (g.checks.cursors.mismatches.length > 0) {
        const sample = g.checks.cursors.mismatches
          .slice(0, 6)
          .map((m) => `${m.name}@(${m.x},${m.y}) react=${m.react} native=${m.native}`)
          .join('; ')
        details.push(`  - Cursors: ${sample}`)
      }
      if (details.length > 0) {
        lines.push(`- **${g.id}**:`)
        lines.push(...details)
      }
    }
    lines.push('')
  }

  lines.push('## Full Per-State Checklist')
  lines.push('')
  lines.push('| State | Room | Kind | Severity | Score | Open (R/N) | Menus | Cursors | Glass | Pixels | Sheet |')
  lines.push('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
  for (const item of items) {
    const c = item.checks
    const openStr = `${c.opened.react ? 'Y' : 'N'}/${c.opened.native ? 'Y' : 'N'} (${c.opened.status})`
    const menuStr = c.menus.status === 'na' ? 'n/a' : `${c.menus.status} (${c.menus.nativeItems.length}/${c.menus.reactItems.length})`
    const curStr = c.cursors.checked > 0 ? `${c.cursors.status} (${c.cursors.matched}/${c.cursors.checked})` : 'n/a'
    const glassStr = `${c.glass.status} (${c.glass.reactCount}/${c.glass.nativeCount}${c.glass.appearedLate ? ' LATE' : ''})`
    const pxStr = c.pixels.meanAbsDiff !== null ? `${c.pixels.meanAbsDiff}` : 'n/a'
    const sheetLink = c.pixels.sheet ? `[compare](${c.pixels.sheet})` : '—'
    lines.push(
      `| \`${item.id}\` | ${item.room} | ${item.kind} | ${item.severity} | ${item.score} | ${openStr} | ${menuStr} | ${curStr} | ${glassStr} | ${pxStr} | ${sheetLink} |`,
    )
  }
  lines.push('')
  return lines.join('\n')
}

const esc = (s) =>
  String(s ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')

export function renderHtmlGallery(payload) {
  const { generatedAt, summary, items } = payload
  const rooms = Array.from(new Set(items.map((i) => i.room)))
  const cards = items
    .map((item) => {
      const c = item.checks
      const badge = (label, status) => `<span class="badge is-${esc(status)}">${esc(label)}: ${esc(status)}</span>`
      const cursorHtml =
        c.cursors.mismatches.length > 0
          ? `<div class="detail"><strong>Cursor mismatches (${c.cursors.mismatches.length}/${c.cursors.checked}):</strong><ul>${c.cursors.mismatches
              .slice(0, 8)
              .map((m) => `<li><code>${esc(m.name)}</code> (${m.x},${m.y}): react <b>${esc(m.react)}</b> vs native <b>${esc(m.native)}</b></li>`)
              .join('')}</ul></div>`
          : ''
      const menuHtml =
        c.menus.status === 'fail'
          ? `<div class="detail"><strong>Menu diff:</strong> react=[${esc(c.menus.reactItems.join(', '))}] | native=[${esc(c.menus.nativeItems.join(', '))}]</div>`
          : ''
      const visionFindings = item.vision?.findings || []
      const visionHtml =
        visionFindings.length > 0
          ? `<div class="detail"><strong>Vision QA (${visionFindings.length}):</strong><ul>${visionFindings
              .slice(0, 6)
              .map(
                (f) =>
                  `<li><b>[${esc(f.severity)}] ${esc(f.area)}:</b> react=${esc(f.react)} | native=${esc(f.native)}</li>`,
              )
              .join('')}</ul></div>`
          : ''
      const img = c.pixels.sheet || item.nativeShot || item.reactShot
      return `<article class="card" data-room="${esc(item.room)}" data-sev="${esc(item.severity)}" data-search="${esc(`${item.id} ${item.name} ${item.room} ${item.kind}`.toLowerCase())}">
  <header class="card-head">
    <div><span class="sev sev-${esc(item.severity)}">${esc(item.severity)}</span> <strong>${esc(item.id)}</strong> <span class="muted">${esc(item.name)}</span></div>
    <div class="muted">${esc(item.room)} / ${esc(item.kind)} / score ${item.score}</div>
  </header>
  <div class="badges">
    ${badge('Open', c.opened.status)}
    ${badge('Menus', c.menus.status)}
    ${badge(`Cursors ${c.cursors.matched}/${c.cursors.checked}`, c.cursors.status)}
    ${badge(`Glass ${c.glass.reactCount}/${c.glass.nativeCount}${c.glass.appearedLate ? ' LATE' : ''}`, c.glass.status)}
    ${badge(`Pixels ${c.pixels.meanAbsDiff ?? 'n/a'}`, c.pixels.status)}
  </div>
  ${item.issues.length ? `<p class="issues">${esc(item.issues.join(' • '))}</p>` : ''}
  ${menuHtml}
  ${cursorHtml}
  ${visionHtml}
  ${img ? `<a href="${esc(img)}" target="_blank"><img loading="lazy" src="${esc(img)}" alt="${esc(item.id)}"></a>` : '<div class="muted">No screenshot available</div>'}
</article>`
    })
    .join('\n')

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Exo Interactive Parity Gallery</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; font-family: system-ui, -apple-system, sans-serif; background: #000; color: #f5f5f5; }
  header.top { position: sticky; top: 0; z-index: 10; background: rgba(12,12,12,.92); backdrop-filter: blur(12px); border-bottom: 1px solid #222; padding: 14px 20px; display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; }
  .stats { display: flex; gap: 10px; flex-wrap: wrap; font-size: 13px; }
  .filters { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  select, input { background: #141414; color: #fff; border: 1px solid #2c2c2c; border-radius: 8px; padding: 6px 10px; font-size: 13px; }
  main { display: grid; grid-template-columns: repeat(auto-fill, minmax(560px, 1fr)); gap: 16px; padding: 18px 20px; }
  .card { background: #0d0d0d; border: 1px solid #222; border-radius: 12px; padding: 12px; display: flex; flex-direction: column; gap: 8px; }
  .card-head { display: flex; justify-content: space-between; gap: 8px; align-items: baseline; font-size: 13px; }
  .muted { color: #8c8c8c; font-size: 12px; }
  .badges { display: flex; flex-wrap: wrap; gap: 6px; }
  .badge, .sev { font-size: 11px; padding: 2px 7px; border-radius: 999px; border: 1px solid #2a2a2a; background: #161616; }
  .is-pass, .sev-PASS { border-color: #1f5133; color: #6ee7a8; }
  .is-warn, .sev-MEDIUM, .sev-LOW { border-color: #665018; color: #fcd34d; }
  .is-fail, .sev-CRITICAL, .sev-HIGH { border-color: #6b1d24; color: #fca5a5; }
  .is-na, .is-skip, .sev-SKIP { color: #9ca3af; }
  .issues { margin: 0; font-size: 12px; color: #fca5a5; }
  .detail { font-size: 12px; color: #d4d4d4; background: #141414; padding: 8px; border-radius: 8px; }
  .detail ul { margin: 4px 0 0 18px; padding: 0; }
  img { width: 100%; height: auto; border-radius: 8px; border: 1px solid #222; display: block; }
  .hidden { display: none !important; }
</style>
</head>
<body>
<header class="top">
  <div>
    <strong>Exo Interactive Parity Gallery</strong>
    <span class="muted">${esc(generatedAt)}</span>
    <div class="stats">
      <span>Total: <b>${summary.counts.total}</b></span>
      <span>CRITICAL: <b>${summary.counts.CRITICAL}</b></span>
      <span>HIGH: <b>${summary.counts.HIGH}</b></span>
      <span>MEDIUM: <b>${summary.counts.MEDIUM}</b></span>
      <span>PASS: <b>${summary.counts.PASS}</b></span>
      <span>SKIP: <b>${summary.counts.SKIP}</b></span>
      <span>Avg Diff: <b>${summary.avgMeanAbsDiff ?? 'n/a'}</b></span>
    </div>
  </div>
  <div class="filters">
    <select id="roomFilter"><option value="">All rooms</option>${rooms.map((r) => `<option value="${esc(r)}">${esc(r)}</option>`).join('')}</select>
    <select id="sevFilter"><option value="">All severities</option><option>CRITICAL</option><option>HIGH</option><option>MEDIUM</option><option>LOW</option><option>PASS</option><option>SKIP</option></select>
    <input id="searchBox" type="search" placeholder="Filter states...">
  </div>
</header>
<main id="grid">
${cards}
</main>
<script>
  const room = document.getElementById('roomFilter');
  const sev = document.getElementById('sevFilter');
  const search = document.getElementById('searchBox');
  const cards = Array.from(document.querySelectorAll('.card'));
  function apply() {
    const r = room.value, s = sev.value, q = search.value.trim().toLowerCase();
    for (const c of cards) {
      const ok = (!r || c.dataset.room === r) && (!s || c.dataset.sev === s) && (!q || c.dataset.search.includes(q));
      c.classList.toggle('hidden', !ok);
    }
  }
  room.addEventListener('change', apply);
  sev.addEventListener('change', apply);
  search.addEventListener('input', apply);
</script>
</body>
</html>`
}
