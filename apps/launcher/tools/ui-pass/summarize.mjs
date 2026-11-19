import { readdirSync, readFileSync } from 'node:fs'
const dir = new URL('./out/', import.meta.url)
const all = {}
for (const f of readdirSync(dir).filter((x) => /^results.*\.json$/.test(x))) Object.assign(all, JSON.parse(readFileSync(new URL(f, dir))))
const only = process.argv[2] ?? ''
const recs = Object.values(all).filter((r) => r.layout && r.file.includes(only))
const agg = (key, get) => { const m = new Map(); for (const r of recs) for (const x of get(r)) { const k = key(x); const o = m.get(k) ?? { n: 0, files: new Set(), x }; o.n++; o.files.add(r.file.replace(/-void-glass.*|\.png/, '')); m.set(k, o) } return [...m.values()].sort((a, b) => b.files.size - a.files.size) }
console.log('records with audit:', recs.length)
console.log('\n== contrast (p5 below need), worst per selector+text')
for (const o of agg((x) => x.sel + '|' + x.text, (r) => r.contrast.failing).slice(0, +(process.argv[3] ?? 40))) console.log(`${o.x.p5}/${o.x.need} ${o.x.size}px "${o.x.text}" ${o.x.sel} [${o.files.size} shots: ${[...o.files].slice(0, 3).join(', ')}] ${o.x.color}`)
console.log('\n== small targets (<28px)')
for (const o of agg((x) => x.sel + x.w + 'x' + x.h, (r) => r.layout.small).slice(0, 40)) console.log(`${o.x.w}x${o.x.h} ${o.x.sel} "${o.x.label}" [${o.files.size}]`)
console.log('\n== clipped w/o ellipsis / past window')
for (const o of agg((x) => x.sel + x.ellipsis, (r) => r.layout.clipped.filter((c) => !c.ellipsis)).slice(0, 40)) console.log(JSON.stringify(o.x), [...o.files].slice(0, 3).join(','))
console.log('\n== off-scale font sizes')
const fs = new Map(); for (const r of recs) for (const [k, v] of Object.entries(r.layout.offscale)) { const o = fs.get(k) ?? { n: 0, ex: new Set() }; o.n++; v.forEach((s) => o.ex.size < 3 && o.ex.add(s)); fs.set(k, o) }
for (const [k, o] of [...fs].sort((a, b) => b[1].n - a[1].n)) console.log(k, o.n, [...o.ex].join(' || '))
console.log('\n== radii (all shots)')
const rd = new Map(); for (const r of recs) for (const [k, v] of Object.entries(r.layout.radii)) { const o = rd.get(k) ?? { n: 0, ex: v.ex[0] }; o.n += v.n; rd.set(k, o) }
for (const [k, o] of [...rd].sort((a, b) => b[1].n - a[1].n)) console.log(k, o.n, o.ex)
console.log('\n== overlaps')
for (const o of agg((x) => x.a + x.b, (r) => r.layout.overlaps).slice(0, 25)) console.log(o.x.w + 'x' + o.x.h, o.x.a, '<>', o.x.b, [...o.files].slice(0, 2).join(','))
console.log('\n== unnamed controls'); for (const o of agg((x) => x.sel, (r) => r.layout.unnamed).slice(0, 15)) console.log(o.x.sel, [...o.files].slice(0, 2).join(','))
console.log('\n== overflowX>0'); for (const r of recs) if (r.layout.overflowX > 0) console.log(r.file, r.layout.overflowX)
console.log('\n== console/errors'); const ce = new Map(); for (const r of Object.values(all)) for (const c of [...(r.console ?? []), ...(r.errors ?? []).map((t) => ({ type: 'exception', text: t }))]) { const k = c.type + ' ' + c.text.slice(0, 160); const o = ce.get(k) ?? new Set(); o.add(r.file?.split('-')[0]); ce.set(k, o) }
for (const [k, v] of ce) console.log(k, '|', [...v].join(','))
console.log('\n== CLS'); for (const r of Object.values(all)) if (r.cls > 0.02) console.log(r.file, r.cls, JSON.stringify(r.shifts ?? []).slice(0, 200))
