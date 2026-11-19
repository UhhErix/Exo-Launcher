// Exo's icons are Lucide (lucide.dev, ISC, through @iconify-json/lucide), one family everywhere (owner, 2026-11-19). scripts/lucide-map.mjs
// names the Lucide icon for each of Exo's glyph names; this writes them into brand/glyphs.ts, with the check that draws itself in and
// the glyphs CSS draws (styles/glyphs.css), and stops on a name icons.tsx uses with no icon, an icon nothing names, a Lucide name
// that doesn't exist, or two Exo names sharing one icon.
// Run it after changing the map: npm run icons (check-only with --check).
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { LUCIDE } from './lucide-map.mjs'

const require = createRequire(import.meta.url)
const lucide = require('@iconify-json/lucide/icons.json').icons
const brand = path.resolve(import.meta.dirname, '../src/brand')
const source = fs.readFileSync(path.join(brand, 'icons.tsx'), 'utf8')
const out = path.join(brand, 'glyphs.ts')
const cssOut = path.resolve(brand, '../styles/glyphs.css')

// icon('library') and marked('hash-lock', 'hash', 'lock-on'): the glyph, or a glyph and its badge.
const named = new Set()
for (const [, exo, glyph, badge] of source.matchAll(/\b(?:icon\('([a-z0-9-]+)'\)|marked\('[a-z0-9-]+', '([a-z0-9-]+)', '([a-z0-9-]+)'\))/g)) {
  for (const name of [exo, glyph, badge]) if (name) named.add(name)
}
const NAMES = Object.keys(LUCIDE).sort()
const base = name => (name.endsWith('-on') && NAMES.includes(name.slice(0, -3)) ? name.slice(0, -3) : name)
for (const name of named) if (!NAMES.includes(base(name))) throw new Error(`icons.tsx names "${name}", which scripts/lucide-map.mjs has no icon for`)
for (const name of NAMES) if (!named.has(name) && !named.has(name + '-on')) throw new Error(`scripts/lucide-map.mjs maps "${name}", which icons.tsx doesn't name`)
for (const name of Object.keys(LUCIDE)) if (!lucide[LUCIDE[name]]) throw new Error(`"${name}" maps to "${LUCIDE[name]}", which Lucide doesn't have`)
const used = new Map()
for (const [name, icon] of Object.entries(LUCIDE)) {
  if (used.has(icon)) throw new Error(`${name} and ${used.get(icon)} share the Lucide icon ${icon}`)
  used.set(icon, name)
}

// A Lucide icon is its strokes on a 24 grid: its body is the markup inside the <svg>. Lucide draws outlines only, so a solid
// form is the outline filled as well as stroked (2): a toggle that is on (liked, starred, pinned) and the transport, as every
// player draws it. The rooms keep their outline when current: the white pill says where you are.
const SOLID = new Set(['play', 'pause', 'stop', 'next', 'previous', 'record'])
const ON = new Set(['bookmark', 'heart', 'lock', 'pin', 'star', 'thumbs-down'])
// Lucide writes fill, stroke, its 2px width and the round ends on every element; they would beat the <svg>'s own (icons.tsx),
// so the line would never follow its size (lineAt) and a solid form would never fill. The <svg> sets them once instead.
const bare = markup => markup.replace(/ (?:fill="none"|(?:stroke|stroke-width|stroke-linecap|stroke-linejoin)="[^"]*")/g, '')
// The parts of a solid form that stay a line: the lock's shackle (filled, it would close into a dome).
const OPEN = { lock: 'M7 11V7' }
const body = name => bare(lucide[LUCIDE[name]].body)
const solidBody = name => {
  const markup = body(name)
  return OPEN[name] ? markup.replace(`<path d="${OPEN[name]}`, `<path fill="none" d="${OPEN[name]}`) : markup
}
const entries = {}
for (const name of NAMES) {
  entries[name] = [SOLID.has(name) ? solidBody(name) : body(name), SOLID.has(name) ? 2 : 0]
  if (ON.has(name)) entries[name + '-on'] = [solidBody(name), 2]
}
// The check that draws itself in is Lucide's check run from its short arm, so the line goes on as a hand writes it.
const points = d => {
  const out = []
  let at = [0, 0]
  for (const [, cmd, args] of d.matchAll(/([MLml])([^MLml]*)/g)) {
    const n = (args.match(/-?(?:\d+\.?\d*|\.\d+)/g) ?? []).map(Number)
    for (let i = 0; i < n.length; i += 2) {
      at = cmd === cmd.toLowerCase() ? [at[0] + n[i], at[1] + n[i + 1]] : [n[i], n[i + 1]]
      out.push(at)
    }
  }
  return out
}
const checkD = /^<path d="([^"]+)"\/>$/.exec(body('check'))?.[1]
if (!checkD || /[^MLml\d\s.,-]/.test(checkD)) throw new Error(`Lucide's check is no longer one line (${body('check')}): redraw CHECK_PATH`)
const CHECK_PATH = 'M' + points(checkD).reverse().map(p => p.join(' ')).join('L')

// The glyphs CSS draws as a mask or an image (a menu's check, Discord's chevrons, the Library's not-installed mark), all at
// 12 or 14px: white at the small line, so a mask (its alpha) and an image both work.
const CSS_GLYPHS = ['check', 'chevron-left', 'chevron-right', 'download']
const svgText = name => `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'>${body(name).replace(/"/g, "'")}</svg>`
const asCss = name => `url("data:image/svg+xml,${svgText(name).replace(/[<>#%]/g, c => encodeURIComponent(c))}")`
const css = `/* Generated by scripts/icons.mjs from scripts/lucide-map.mjs: the glyphs CSS draws, as glyphs.ts has them. Don't edit; run
   npm run icons. Each is white at the small line (1.5 on the 24 grid): a mask (mask: var(--exo-glyph-check) center / contain
   no-repeat) or an image. */
:root {
${CSS_GLYPHS.map(name => `  --exo-glyph-${name}: ${asCss(name)};`).join('\n')}
}
`

const text = `// Generated by scripts/icons.mjs from scripts/lucide-map.mjs: Lucide's icons (lucide.dev, ISC), each [markup, solid]: its
// strokes on a 24 grid (0), or the same strokes filled too (2). Don't edit; run npm run icons.
/** The line on the 24 grid at a size in px: 1.5 up to 24, 1.75 from there, as light as Apple's symbols. */
export const lineAt = (size: number) => (size >= 24 ? 1.75 : 1.5)
/** Lucide's check as one line from its short arm, for a check that draws itself in (tokens.css › .exo-motion-check). */
export const CHECK_PATH = '${CHECK_PATH}'
export const GLYPHS = {
${Object.entries(entries).map(([name, [markup, solid]]) => `  '${name}': [${JSON.stringify(markup)}, ${solid}],`).join('\n')}
} as const
`
const read = file => (fs.existsSync(file) ? fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n') : '')
if (process.argv.includes('--check')) {
  if (read(out) !== text) { console.error('brand/glyphs.ts is out of date: run npm run icons'); process.exit(1) }
  if (read(cssOut) !== css) { console.error('styles/glyphs.css is out of date: run npm run icons'); process.exit(1) }
} else {
  fs.writeFileSync(out, text)
  fs.writeFileSync(cssOut, css)
  console.log(`Wrote ${Object.keys(entries).length} icons (${used.size} Lucide) to src/brand/glyphs.ts, and ${CSS_GLYPHS.length} to src/styles/glyphs.css`)
}
