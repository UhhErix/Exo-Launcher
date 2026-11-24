import fs from 'node:fs'
import path from 'node:path'

export function parseTrace(jsonlText) {
  const entries = []
  if (!jsonlText) return entries
  for (const rawLine of String(jsonlText).split(/\r?\n/)) {
    const line = rawLine.trim()
    if (!line) continue
    try {
      const obj = JSON.parse(line)
      if (obj && typeof obj === 'object' && typeof obj.t === 'number') entries.push(obj)
    } catch {}
  }
  return entries
}

export function sliceTraceByWindow(entries, startMs, endMs, client = null) {
  return entries.filter((entry) => {
    if (client && entry.client !== client) return false
    if (entry.t < startMs || entry.t > endMs) return false
    if (entry.method === 'debug.mark') return false
    return true
  })
}

const ALLOWED_SHAPE_TYPES = new Set(['null', 'boolean', 'number', 'string', 'object', 'array'])

function checkShapeNode(node, pathLabel, findings) {
  if (node === null || node === undefined) return
  if (typeof node === 'string') {
    if (!ALLOWED_SHAPE_TYPES.has(node)) findings.push(`${pathLabel}: non-type string value`)
    return
  }
  if (Array.isArray(node)) {
    for (let i = 0; i < node.length; i++) checkShapeNode(node[i], `${pathLabel}[${i}]`, findings)
    return
  }
  if (typeof node === 'object') {
    for (const [k, v] of Object.entries(node)) {
      if (k !== '$key' && k !== '<key>' && !/^[A-Za-z_][A-Za-z0-9_-]{0,47}$/.test(k)) {
        findings.push(`${pathLabel}.${k}: unsafe key name`)
      }
      checkShapeNode(v, `${pathLabel}.${k}`, findings)
    }
    return
  }
  findings.push(`${pathLabel}: unexpected non-shape literal`)
}

export function isTraceSafe(jsonlText, forbiddenValues = []) {
  const findings = []
  const lines = String(jsonlText ?? '').split(/\r?\n/).filter((l) => l.trim())
  const tokenLike = /(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|[A-Za-z]:(?:\\\\|\\|\/)Users(?:\\\\|\\|\/)|Bearer\s+[A-Za-z0-9._-]{12,}|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})/i
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    for (const bad of forbiddenValues) {
      if (bad && line.includes(bad)) findings.push(`line ${i + 1}: forbidden substring matched`)
    }
    const m = tokenLike.exec(line)
    if (m) findings.push(`line ${i + 1}: value-like pattern matched (${m[0].slice(0, 16)}...)`)
    try {
      const obj = JSON.parse(line)
      if ('params_shape' in obj) checkShapeNode(obj.params_shape, `line ${i + 1}.params_shape`, findings)
      if ('payload_shape' in obj) checkShapeNode(obj.payload_shape, `line ${i + 1}.payload_shape`, findings)
    } catch {}
  }
  return { safe: findings.length === 0, findings }
}

export function summarizeEntries(entries) {
  const methods = new Map()
  const events = new Map()
  const repliesById = new Map()
  const requestMethodById = new Map()

  for (const entry of entries) {
    if (typeof entry.method === 'string') {
      if (entry.method === 'debug.mark') continue
      requestMethodById.set(entry.id, entry.method)
      const cur = methods.get(entry.method) ?? { count: 0, ok: 0, failed: 0, ms: [], shapes: new Set() }
      cur.count += 1
      if (entry.params_shape !== undefined) cur.shapes.add(JSON.stringify(entry.params_shape))
      methods.set(entry.method, cur)
    } else if (typeof entry.ok === 'boolean' && typeof entry.id === 'string') {
      repliesById.set(entry.id, entry)
    } else if (typeof entry.event === 'string') {
      const cur = events.get(entry.event) ?? { count: 0, shapes: new Set() }
      cur.count += 1
      if (entry.payload_shape !== undefined) cur.shapes.add(JSON.stringify(entry.payload_shape))
      events.set(entry.event, cur)
    }
  }

  for (const [id, reply] of repliesById) {
    const method = requestMethodById.get(id)
    if (!method) continue
    const cur = methods.get(method)
    if (!cur) continue
    if (reply.ok) cur.ok += 1
    else cur.failed += 1
    if (typeof reply.ms === 'number') cur.ms.push(reply.ms)
  }

  const methodObj = {}
  for (const [name, info] of [...methods.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const sortedMs = [...info.ms].sort((a, b) => a - b)
    const medianMs = sortedMs.length ? sortedMs[Math.floor(sortedMs.length / 2)] : null
    methodObj[name] = {
      count: info.count,
      ok: info.ok,
      failed: info.failed,
      medianMs,
      shapes: [...info.shapes].slice(0, 3),
    }
  }

  const eventObj = {}
  for (const [name, info] of [...events.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    eventObj[name] = {
      count: info.count,
      shapes: [...info.shapes].slice(0, 3),
    }
  }

  return {
    totalRequests: [...methods.values()].reduce((s, v) => s + v.count, 0),
    totalEvents: [...events.values()].reduce((s, v) => s + v.count, 0),
    methods: methodObj,
    events: eventObj,
  }
}

function walkFiles(dir, exts) {
  const out = []
  if (!fs.existsSync(dir)) return out
  const stack = [dir]
  while (stack.length) {
    const cur = stack.pop()
    for (const entry of fs.readdirSync(cur, { withFileTypes: true })) {
      const full = path.join(cur, entry.name)
      if (entry.isDirectory()) {
        if (entry.name === 'node_modules' || entry.name === 'dist' || entry.name === 'target') continue
        stack.push(full)
      } else if (exts.some((ext) => entry.name.endsWith(ext))) {
        out.push(full)
      }
    }
  }
  return out.sort()
}

export function indexReactCallers(uiSrcDir) {
  const hostPath = path.join(uiSrcDir, 'lib', 'host.ts')
  const methodToWrappers = new Map()
  const methodToHostLines = new Map()
  const eventSubscribers = new Map()

  if (fs.existsSync(hostPath)) {
    const lines = fs.readFileSync(hostPath, 'utf8').split(/\r?\n/)
    let inHostObj = false
    let currentProp = null
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]
      if (!inHostObj) {
        if (/^export\s+const\s+host\s*=\s*\{/.test(line)) inHostObj = true
        continue
      }
      if (/^\}\s*$/.test(line)) {
        inHostObj = false
        currentProp = null
        continue
      }
      const propMatch = /^  ([A-Za-z_][A-Za-z0-9_]*)\s*:/.exec(line)
      if (propMatch) currentProp = propMatch[1]
      if (currentProp) {
        const stringMatches = [...line.matchAll(/['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)['"]/g)]
        for (const m of stringMatches) {
          const method = m[1]
          if (!methodToWrappers.has(method)) methodToWrappers.set(method, new Set())
          methodToWrappers.get(method).add(currentProp)
          if (!methodToHostLines.has(method)) methodToHostLines.set(method, [])
          methodToHostLines.get(method).push(`ui/src/lib/host.ts:${i + 1}`)
        }
      }
    }
  }

  const wrapperToMethods = new Map()
  for (const [method, wrappers] of methodToWrappers) {
    for (const w of wrappers) {
      if (!wrapperToMethods.has(w)) wrapperToMethods.set(w, new Set())
      wrapperToMethods.get(w).add(method)
    }
  }

  const callers = new Map()
  const addCaller = (method, ref) => {
    if (!callers.has(method)) callers.set(method, new Set())
    callers.get(method).add(ref)
  }

  const files = walkFiles(uiSrcDir, ['.ts', '.tsx'])
  for (const file of files) {
    const rel = 'ui/src/' + path.relative(uiSrcDir, file).replaceAll('\\', '/')
    if (/\.test\.tsx?$/.test(rel) || /Mock\.ts$/.test(rel)) continue
    const text = fs.readFileSync(file, 'utf8')
    const lines = text.split(/\r?\n/)
    const isHostFile = rel === 'ui/src/lib/host.ts'
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]
      for (const m of line.matchAll(/\bonHostEvent\(\s*['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)['"]/g)) {
        const ev = m[1]
        if (!eventSubscribers.has(ev)) eventSubscribers.set(ev, new Set())
        eventSubscribers.get(ev).add(`${rel}:${i + 1}`)
      }
      if (isHostFile) continue
      for (const m of line.matchAll(/\bhost\.([A-Za-z_][A-Za-z0-9_]*)\b/g)) {
        const methods = wrapperToMethods.get(m[1])
        if (!methods) continue
        for (const method of methods) addCaller(method, `${rel}:${i + 1}`)
      }
      for (const m of line.matchAll(/['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)['"]/g)) {
        if (methodToWrappers.has(m[1])) addCaller(m[1], `${rel}:${i + 1}`)
      }
    }
  }

  const callerMap = {}
  const allMethods = new Set([...methodToWrappers.keys(), ...callers.keys()])
  for (const method of [...allMethods].sort()) {
    const direct = [...(callers.get(method) ?? [])]
    const fallback = methodToHostLines.get(method) ?? []
    callerMap[method] = direct.length ? direct.slice(0, 6) : fallback.slice(0, 3)
  }

  const subscriberMap = {}
  for (const [ev, refs] of [...eventSubscribers.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    subscriberMap[ev] = [...refs].slice(0, 6)
  }

  return { callers: callerMap, eventSubscribers: subscriberMap }
}

export function indexNativeHandlers(nativeUiDir) {
  const handledEvents = new Set()
  const calledMethods = new Set()
  const rpcLib = path.join(nativeUiDir, 'crates', 'exo-rpc', 'src', 'lib.rs')
  if (fs.existsSync(rpcLib)) {
    const text = fs.readFileSync(rpcLib, 'utf8')
    const parseIdx = text.indexOf('fn parse_event(')
    const slice = parseIdx >= 0 ? text.slice(parseIdx) : text
    for (const m of slice.matchAll(/"([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)"\s*=>/g)) {
      handledEvents.add(m[1])
    }
  }
  const rsFiles = walkFiles(path.join(nativeUiDir, 'crates'), ['.rs'])
  for (const file of rsFiles) {
    const text = fs.readFileSync(file, 'utf8')
    for (const m of text.matchAll(/"([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)"/g)) {
      calledMethods.add(m[1])
    }
  }
  return { handledEvents: [...handledEvents].sort(), calledMethods: [...calledMethods].sort() }
}

export function diffParity({
  actions,
  webTraces,
  nativeTraces,
  webStatus = {},
  nativeStatus = {},
  reactCallers = {},
  reactEventSubscribers = {},
  nativeHandledEvents = [],
}) {
  const nativeHandledSet = new Set(nativeHandledEvents)
  const perAction = []
  const globalReactOnly = new Map()
  const globalNativeOnly = new Map()
  const globalChattiness = new Map()
  const globalEventGaps = new Map()
  const notDrivable = []

  const allWebMethods = new Map()
  const allNativeMethods = new Map()
  const allWebEvents = new Map()
  const allNativeEvents = new Map()

  for (const action of actions) {
    const wStatus = webStatus[action.id] ?? { status: 'ok' }
    const nStatus = nativeStatus[action.id] ?? { status: 'ok' }
    const wSum = summarizeEntries(webTraces[action.id] ?? [])
    const nSum = summarizeEntries(nativeTraces[action.id] ?? [])

    if (wStatus.status !== 'ok') {
      notDrivable.push({ actionId: action.id, label: action.label, client: 'web', reason: wStatus.reason || 'not drivable' })
    }
    if (nStatus.status !== 'ok') {
      notDrivable.push({ actionId: action.id, label: action.label, client: 'native', reason: nStatus.reason || 'not drivable' })
    }

    for (const [m, info] of Object.entries(wSum.methods)) {
      allWebMethods.set(m, (allWebMethods.get(m) ?? 0) + info.count)
    }
    for (const [m, info] of Object.entries(nSum.methods)) {
      allNativeMethods.set(m, (allNativeMethods.get(m) ?? 0) + info.count)
    }
    for (const [e, info] of Object.entries(wSum.events)) {
      allWebEvents.set(e, (allWebEvents.get(e) ?? 0) + info.count)
    }
    for (const [e, info] of Object.entries(nSum.events)) {
      allNativeEvents.set(e, (allNativeEvents.get(e) ?? 0) + info.count)
    }

    const reactOnlyMethods = []
    const nativeOnlyMethods = []
    const chattiness = []
    const pushEventGaps = []

    const methodNames = new Set([...Object.keys(wSum.methods), ...Object.keys(nSum.methods)])
    for (const method of [...methodNames].sort()) {
      const wc = wSum.methods[method]?.count ?? 0
      const nc = nSum.methods[method]?.count ?? 0
      const callers = reactCallers[method] ?? []
      if (wc > 0 && nc === 0) {
        reactOnlyMethods.push({
          method,
          webCount: wc,
          nativeCount: 0,
          webMedianMs: wSum.methods[method]?.medianMs ?? null,
          reactCallers: callers,
        })
        const agg = globalReactOnly.get(method) ?? { method, totalWebCalls: 0, actions: [], reactCallers: callers }
        agg.totalWebCalls += wc
        agg.actions.push(action.id)
        globalReactOnly.set(method, agg)
      } else if (nc > 0 && wc === 0) {
        nativeOnlyMethods.push({
          method,
          webCount: 0,
          nativeCount: nc,
          nativeMedianMs: nSum.methods[method]?.medianMs ?? null,
          reactCallers: callers,
        })
        const agg = globalNativeOnly.get(method) ?? { method, totalNativeCalls: 0, actions: [], reactCallers: callers }
        agg.totalNativeCalls += nc
        agg.actions.push(action.id)
        globalNativeOnly.set(method, agg)
      } else if (wc > 0 && nc > 0) {
        const ratio = Math.max(wc, nc) / Math.min(wc, nc)
        if (ratio > 2) {
          const chattier = wc > nc ? 'web' : 'native'
          chattiness.push({
            method,
            webCount: wc,
            nativeCount: nc,
            ratio: Number(ratio.toFixed(2)),
            chattier,
            reactCallers: callers,
          })
          const prev = globalChattiness.get(method)
          if (!prev || ratio > prev.ratio) {
            globalChattiness.set(method, {
              method,
              webCount: wc,
              nativeCount: nc,
              ratio: Number(ratio.toFixed(2)),
              chattier,
              actionId: action.id,
              reactCallers: callers,
            })
          }
        }
      }
    }

    const eventNames = new Set([...Object.keys(wSum.events), ...Object.keys(nSum.events)])
    for (const ev of [...eventNames].sort()) {
      const wc = wSum.events[ev]?.count ?? 0
      const nc = nSum.events[ev]?.count ?? 0
      const subscribers = reactEventSubscribers[ev] ?? []
      const nativeHandles = nativeHandledSet.has(ev)
      if ((wc > 0 && nc === 0) || (wc > 0 && !nativeHandles)) {
        const why = nc === 0 ? (nativeHandles ? 'not emitted on native' : 'not emitted and unhandled in native') : 'emitted but unhandled in native'
        pushEventGaps.push({
          event: ev,
          webCount: wc,
          nativeCount: nc,
          nativeHandles,
          reason: why,
          reactSubscribers: subscribers,
        })
        const agg = globalEventGaps.get(ev) ?? {
          event: ev,
          totalWebEvents: 0,
          totalNativeEvents: 0,
          nativeHandles,
          actions: [],
          reactSubscribers: subscribers,
        }
        agg.totalWebEvents += wc
        agg.totalNativeEvents += nc
        agg.actions.push(action.id)
        globalEventGaps.set(ev, agg)
      }
    }

    reactOnlyMethods.sort((a, b) => b.webCount - a.webCount || a.method.localeCompare(b.method))
    nativeOnlyMethods.sort((a, b) => b.nativeCount - a.nativeCount || a.method.localeCompare(b.method))
    chattiness.sort((a, b) => b.ratio - a.ratio || a.method.localeCompare(b.method))

    const gapScore = reactOnlyMethods.length * 10
      + pushEventGaps.length * 6
      + chattiness.length * 4
      + nativeOnlyMethods.length * 2
      + (nStatus.status !== 'ok' ? 15 : 0)

    perAction.push({
      id: action.id,
      label: action.label,
      group: action.group,
      webStatus: wStatus,
      nativeStatus: nStatus,
      webSummary: wSum,
      nativeSummary: nSum,
      gapScore,
      reactOnlyMethods,
      nativeOnlyMethods,
      pushEventGaps,
      chattiness,
    })
  }

  const staticUnhandledEvents = Object.entries(reactEventSubscribers)
    .filter(([ev]) => !nativeHandledSet.has(ev))
    .map(([event, reactSubscribers]) => ({
      event,
      observedWebCount: allWebEvents.get(event) ?? 0,
      observedNativeCount: allNativeEvents.get(event) ?? 0,
      reactSubscribers,
    }))
    .sort((a, b) => b.observedWebCount - a.observedWebCount || b.reactSubscribers.length - a.reactSubscribers.length || a.event.localeCompare(b.event))

  const globalSessionReactOnly = [...allWebMethods.entries()]
    .filter(([m]) => !allNativeMethods.has(m))
    .map(([method, webCount]) => ({
      method,
      webCount,
      nativeCount: 0,
      actions: globalReactOnly.get(method)?.actions ?? [],
      reactCallers: reactCallers[method] ?? [],
    }))
    .sort((a, b) => b.actions.length - a.actions.length || b.webCount - a.webCount || a.method.localeCompare(b.method))

  const globalSessionNativeOnly = [...allNativeMethods.entries()]
    .filter(([m]) => !allWebMethods.has(m))
    .map(([method, nativeCount]) => ({
      method,
      webCount: 0,
      nativeCount,
      actions: globalNativeOnly.get(method)?.actions ?? [],
      reactCallers: reactCallers[method] ?? [],
    }))
    .sort((a, b) => b.nativeCount - a.nativeCount || a.method.localeCompare(b.method))

  const globalSessionChattiness = []
  for (const [method, wc] of allWebMethods) {
    const nc = allNativeMethods.get(method) ?? 0
    if (wc > 0 && nc > 0) {
      const ratio = Math.max(wc, nc) / Math.min(wc, nc)
      if (ratio > 2) {
        globalSessionChattiness.push({
          method,
          webCount: wc,
          nativeCount: nc,
          ratio: Number(ratio.toFixed(2)),
          chattier: wc > nc ? 'web' : 'native',
          reactCallers: reactCallers[method] ?? [],
        })
      }
    }
  }
  globalSessionChattiness.sort((a, b) => b.ratio - a.ratio || Math.max(b.webCount, b.nativeCount) - Math.max(a.webCount, a.nativeCount))

  perAction.sort((a, b) => b.gapScore - a.gapScore || a.id.localeCompare(b.id))

  return {
    generatedAt: new Date().toISOString(),
    totals: {
      actions: actions.length,
      webDrivable: actions.length - notDrivable.filter((x) => x.client === 'web').length,
      nativeDrivable: actions.length - notDrivable.filter((x) => x.client === 'native').length,
      uniqueWebMethods: allWebMethods.size,
      uniqueNativeMethods: allNativeMethods.size,
      sessionReactOnlyMethods: globalSessionReactOnly.length,
      sessionNativeOnlyMethods: globalSessionNativeOnly.length,
      sessionChattyMethods: globalSessionChattiness.length,
      observedWebEvents: allWebEvents.size,
      observedNativeEvents: allNativeEvents.size,
      staticUnhandledReactEvents: staticUnhandledEvents.length,
    },
    notDrivable,
    sessionReactOnly: globalSessionReactOnly,
    sessionNativeOnly: globalSessionNativeOnly,
    sessionChattiness: globalSessionChattiness,
    observedEventGaps: [...globalEventGaps.values()].sort((a, b) => b.totalWebEvents - a.totalWebEvents || a.event.localeCompare(b.event)),
    staticUnhandledEvents,
    perAction,
  }
}

function fmtRefs(refs) {
  if (!refs || !refs.length) return '—'
  return refs.slice(0, 3).map((r) => `\`${r}\``).join(', ')
}

export function formatMarkdownReport(diff, meta = {}) {
  const lines = []
  lines.push('# host-v1 Record & Replay RPC Parity Report')
  lines.push('')
  lines.push(`Generated: ${diff.generatedAt}`)
  if (meta.captureDir) lines.push(`Capture: \`${meta.captureDir}\``)
  lines.push('')
  lines.push('## Summary')
  lines.push('')
  lines.push('| Metric | Value |')
  lines.push('| --- | ---: |')
  lines.push(`| Actions recorded | ${diff.totals.actions} |`)
  lines.push(`| Web drivable actions | ${diff.totals.webDrivable} / ${diff.totals.actions} |`)
  lines.push(`| Native drivable actions | ${diff.totals.nativeDrivable} / ${diff.totals.actions} |`)
  lines.push(`| Unique RPC methods called by React (web) | ${diff.totals.uniqueWebMethods} |`)
  lines.push(`| Unique RPC methods called by Native (Rune) | ${diff.totals.uniqueNativeMethods} |`)
  lines.push(`| Methods React called that Native never called | ${diff.totals.sessionReactOnlyMethods} |`)
  lines.push(`| Methods Native called that React never called | ${diff.totals.sessionNativeOnlyMethods} |`)
  lines.push(`| Chatty methods (>2x call ratio) | ${diff.totals.sessionChattyMethods} |`)
  lines.push(`| Observed push events (web / native) | ${diff.totals.observedWebEvents} / ${diff.totals.observedNativeEvents} |`)
  lines.push(`| React-subscribed push events unhandled by Native (static) | ${diff.totals.staticUnhandledReactEvents} |`)
  lines.push('')

  if (diff.notDrivable.length) {
    lines.push('## Not drivable')
    lines.push('')
    lines.push('| Action | Client | Reason |')
    lines.push('| --- | --- | --- |')
    for (const item of diff.notDrivable) {
      lines.push(`| \`${item.actionId}\` (${item.label}) | ${item.client} | ${item.reason} |`)
    }
    lines.push('')
  }

  lines.push('## Top Missing Methods (React called, Native never called)')
  lines.push('')
  if (diff.sessionReactOnly.length) {
    lines.push('| Method | Web calls | Actions | React caller(s) |')
    lines.push('| --- | ---: | --- | --- |')
    for (const row of diff.sessionReactOnly) {
      lines.push(`| \`${row.method}\` | ${row.webCount} | ${row.actions.map((a) => `\`${a}\``).join(', ')} | ${fmtRefs(row.reactCallers)} |`)
    }
  } else {
    lines.push('None.')
  }
  lines.push('')

  lines.push('## Native-only Methods (Native called, React did not)')
  lines.push('')
  if (diff.sessionNativeOnly.length) {
    lines.push('| Method | Native calls | Actions | React caller(s) |')
    lines.push('| --- | ---: | --- | --- |')
    for (const row of diff.sessionNativeOnly) {
      lines.push(`| \`${row.method}\` | ${row.nativeCount} | ${row.actions.map((a) => `\`${a}\``).join(', ')} | ${fmtRefs(row.reactCallers)} |`)
    }
  } else {
    lines.push('None.')
  }
  lines.push('')

  lines.push('## Chattiness (>2x call count difference across session)')
  lines.push('')
  if (diff.sessionChattiness.length) {
    lines.push('| Method | Web calls | Native calls | Ratio | Chattier | React caller(s) |')
    lines.push('| --- | ---: | ---: | ---: | --- | --- |')
    for (const row of diff.sessionChattiness) {
      lines.push(`| \`${row.method}\` | ${row.webCount} | ${row.nativeCount} | ${row.ratio}x | ${row.chattier} | ${fmtRefs(row.reactCallers)} |`)
    }
  } else {
    lines.push('None.')
  }
  lines.push('')

  lines.push('## Push Event Gaps')
  lines.push('')
  lines.push('### Observed during recorded actions')
  lines.push('')
  if (diff.observedEventGaps.length) {
    lines.push('| Event | Web count | Native count | Native handles? | Actions | React subscriber(s) |')
    lines.push('| --- | ---: | ---: | --- | --- | --- |')
    for (const row of diff.observedEventGaps) {
      lines.push(`| \`${row.event}\` | ${row.totalWebEvents} | ${row.totalNativeEvents} | ${row.nativeHandles ? 'yes' : 'no'} | ${row.actions.map((a) => `\`${a}\``).join(', ')} | ${fmtRefs(row.reactSubscribers)} |`)
    }
  } else {
    lines.push('None observed during this run.')
  }
  lines.push('')

  lines.push('### Static React subscribers missing from native `exo-rpc`')
  lines.push('')
  if (diff.staticUnhandledEvents.length) {
    lines.push('| Event | Observed web | Observed native | React subscriber(s) |')
    lines.push('| --- | ---: | ---: | --- |')
    for (const row of diff.staticUnhandledEvents) {
      lines.push(`| \`${row.event}\` | ${row.observedWebCount} | ${row.observedNativeCount} | ${fmtRefs(row.reactSubscribers)} |`)
    }
  } else {
    lines.push('None.')
  }
  lines.push('')

  lines.push('## Per-Action Breakdown (sorted by biggest gaps first)')
  lines.push('')
  for (const act of diff.perAction) {
    const wBadge = act.webStatus.status === 'ok' ? 'ok' : `not drivable (${act.webStatus.reason})`
    const nBadge = act.nativeStatus.status === 'ok' ? 'ok' : `not drivable (${act.nativeStatus.reason})`
    lines.push(`### \`${act.id}\` — ${act.label}`)
    lines.push('')
    lines.push(`- **Gap score:** ${act.gapScore} | **Web:** ${wBadge} (${act.webSummary.totalRequests} req, ${act.webSummary.totalEvents} ev) | **Native:** ${nBadge} (${act.nativeSummary.totalRequests} req, ${act.nativeSummary.totalEvents} ev)`)
    lines.push('')
    if (act.reactOnlyMethods.length) {
      lines.push('**React called, Native did not:**')
      lines.push('')
      lines.push('| Method | Web calls | Median ms | React caller(s) |')
      lines.push('| --- | ---: | ---: | --- |')
      for (const row of act.reactOnlyMethods) {
        lines.push(`| \`${row.method}\` | ${row.webCount} | ${row.webMedianMs ?? '—'} | ${fmtRefs(row.reactCallers)} |`)
      }
      lines.push('')
    }
    if (act.nativeOnlyMethods.length) {
      lines.push('**Native called, React did not:**')
      lines.push('')
      lines.push('| Method | Native calls | Median ms | React caller(s) |')
      lines.push('| --- | ---: | ---: | --- |')
      for (const row of act.nativeOnlyMethods) {
        lines.push(`| \`${row.method}\` | ${row.nativeCount} | ${row.nativeMedianMs ?? '—'} | ${fmtRefs(row.reactCallers)} |`)
      }
      lines.push('')
    }
    if (act.chattiness.length) {
      lines.push('**Chattiness (>2x):**')
      lines.push('')
      lines.push('| Method | Web | Native | Ratio | Chattier | React caller(s) |')
      lines.push('| --- | ---: | ---: | ---: | --- | --- |')
      for (const row of act.chattiness) {
        lines.push(`| \`${row.method}\` | ${row.webCount} | ${row.nativeCount} | ${row.ratio}x | ${row.chattier} | ${fmtRefs(row.reactCallers)} |`)
      }
      lines.push('')
    }
    if (act.pushEventGaps.length) {
      lines.push('**Push event gaps:**')
      lines.push('')
      lines.push('| Event | Web | Native | Reason | React subscriber(s) |')
      lines.push('| --- | ---: | ---: | --- | --- |')
      for (const row of act.pushEventGaps) {
        lines.push(`| \`${row.event}\` | ${row.webCount} | ${row.nativeCount} | ${row.reason} | ${fmtRefs(row.reactSubscribers)} |`)
      }
      lines.push('')
    }
    if (!act.reactOnlyMethods.length && !act.nativeOnlyMethods.length && !act.chattiness.length && !act.pushEventGaps.length) {
      lines.push('No RPC or push-event gaps observed for this action.')
      lines.push('')
    }
  }

  return lines.join('\n')
}
