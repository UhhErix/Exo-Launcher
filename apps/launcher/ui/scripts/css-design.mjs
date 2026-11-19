// DESIGN.md's hover and type rules for every CSS file, run by `npm run check`.
//   exo/hover-fill      a :hover rule never paints a see-through white background; use var(--hover-fill).
//   exo/no-hover-fade   a :hover rule never lowers opacity (opacity: 1 to reveal a control is fine).
//   exo/type-scale      font sizes come from the scale; anything else is a named exception in css-design.config.mjs.
//   exo/one-slider      a range's track and knob (::-webkit-slider-*, ::-moz-range-*) are drawn only in config.sliderFiles.
//   exo/control-fill    a control (a selector ending in a btn, button, chip, pill, tab, cta, toggle,
//                       play, primary, trigger or go class) paints its background with a token (var(--...)),
//                       transparent, none or #000, never a literal gray.
//   exo/art-edge        art (a selector ending in a cover, poster, -art, artwork, thumb or avatar class) has no ring
//                       on its edge: no inset 0 0 0 shadow and no visible border. A ring is an outline outside it.
//                       backdrop-filter is Liquid Glass (owner, 2026-11-14): allowed, and not checked here.
//   exo/token-colour    a colour is a token (var(--...)) outside config.tokenFiles: no hex, rgb(), hsl(), oklch() or
//                       named colour in a declaration. transparent, currentcolor, inherit, #000 (the page's black),
//                       the forced-colors system colours and pictures (url()) are not literals. The literals that were
//                       there when the rule landed are listed in config.colourBaseline; a new one fails, and so does a
//                       baseline entry that no longer matches (the list only shrinks).
// A genuine exception at one declaration: `/* design-disable-next-line <rule> -- why */`. The why must be a real reason
// (at least three words) and every disable in use is listed in the gate's output, so none hides.
// Property names are matched case-insensitively. Checked too: <style> blocks and style="" attributes of config.htmlFiles.
// Plain PostCSS (already in the toolchain through Vite) rather than Stylelint, whose glob dependency
// carries a high-severity advisory with no fixed release.
//   node scripts/css-design.mjs [config] [dir]
import fs from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import postcss from 'postcss'

const [configPath = 'css-design.config.mjs', dir = 'src'] = process.argv.slice(2)
const config = (await import(pathToFileURL(path.resolve(configPath)).href)).default
const sizes = new Set(config.sizes ?? [])
const exceptions = config.exceptions ?? []
const sliderFiles = new Set((config.sliderFiles ?? []).map((f) => path.normalize(f)))
const motionExempt = new Set((config.motionExempt ?? []).map((f) => path.normalize(f)))
// A missing tokenFiles must fail: a config that forgets it would otherwise switch the colour rule off.
if (!Array.isArray(config.tokenFiles) || !config.tokenFiles.length) {
  console.error(`${configPath}  tokenFiles is missing: name the file(s) colours are defined in (exo/token-colour cannot switch itself off)`)
  process.exit(1)
}
const tokenFiles = new Set(config.tokenFiles.map((f) => path.normalize(f)))
const colourKey = (file, selector, prop, value) => `${path.normalize(file).replaceAll('\\', '/')} | ${norm(selector)} | ${prop}: ${norm(value)}`
const baseline = new Map()
for (const key of (config.colourBaseline ?? [])) baseline.set(key, (baseline.get(key) ?? 0) + 1)

const isHover = (rule) => rule?.type === 'rule' && /:hover\b/.test(rule.selector)
const num = (s) => (s.endsWith('%') ? parseFloat(s) / 100 : parseFloat(s))
const norm = (s) => s.replace(/\s+/g, ' ').trim()

/** True for a white (or near-white) colour with alpha below 1, in any notation. */
export function translucentWhite(value) {
  const v = value.toLowerCase().replace(/\s+/g, ' ')
  if (/#f{3}[0-9a-e]\b|#f{6}[0-9a-f][0-9a-e]\b/.test(v)) return true
  for (const m of v.matchAll(/rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)\s*[,/]\s*([\d.]+%?)\s*\)/g)) {
    if (+m[1] >= 240 && +m[2] >= 240 && +m[3] >= 240 && num(m[4]) < 1) return true
  }
  for (const m of v.matchAll(/hsla?\(\s*[\d.]+(?:deg)?[ ,]+[\d.]+%[ ,]+([\d.]+)%\s*[,/]\s*([\d.]+%?)\s*\)/g)) {
    if (+m[1] >= 94 && num(m[2]) < 1) return true
  }
  for (const m of v.matchAll(/color-mix\(([^()]*(?:\([^()]*\)[^()]*)*)\)/g)) {
    if (/\b(white|#fff(fff)?)\b/.test(m[1]) && /\btransparent\b/.test(m[1])) return true
  }
  return false
}

const NAMED = 'aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan darkgoldenrod darkgray darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid darkred darksalmon darkseagreen darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold goldenrod gray green greenyellow grey honeydew hotpink indianred indigo ivory khaki lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan lightgoldenrodyellow lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen lightskyblue lightslategray lightslategrey lightsteelblue lightyellow lime limegreen linen magenta maroon mediumaquamarine mediumblue mediumorchid mediumpurple mediumseagreen mediumslateblue mediumspringgreen mediumturquoise mediumvioletred midnightblue mintcream mistyrose moccasin navajowhite navy oldlace olive olivedrab orange orangered orchid palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru pink plum powderblue purple rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown seagreen seashell sienna silver skyblue slateblue slategray slategrey snow springgreen steelblue tan teal thistle tomato turquoise violet wheat white whitesmoke yellow yellowgreen'
const NAMED_RE = new RegExp(`(^|[\\s,(])(${NAMED.split(' ').join('|')})(?=$|[\\s,)])`, 'i')

/** True when a declaration's value paints a literal colour rather than a token: a hex (but #000, the page's black),
 *  an rgb()/hsl()/oklch()/lab()/hwb() with numbers, or a named colour. A var() is a token, but its fallback is checked; a url() is skipped. */
export function literalColour(value) {
  let v = value
  // url(...) is a picture: drop it. var(--x, fallback) is a token with its fallback: keep the fallback, since a
  // literal colour hidden in one is still a literal colour.
  for (let i = v.toLowerCase().indexOf('url('); i >= 0; i = v.toLowerCase().indexOf('url(')) {
    let depth = 0, j = i + 3
    for (; j < v.length; j++) { if (v[j] === '(') depth++; else if (v[j] === ')' && --depth === 0) break }
    v = v.slice(0, i) + 'TOKEN' + v.slice(j + 1)
  }
  for (let i = v.toLowerCase().indexOf('var('); i >= 0; i = v.toLowerCase().indexOf('var(')) {
    let depth = 0, j = i + 3, comma = -1
    for (; j < v.length; j++) {
      if (v[j] === '(') depth++
      else if (v[j] === ')' && --depth === 0) break
      else if (v[j] === ',' && depth === 1 && comma < 0) comma = j
    }
    v = v.slice(0, i) + ' TOKEN ' + (comma < 0 ? '' : v.slice(comma + 1, j)) + ' ' + v.slice(j + 1)
  }
  const hex = [...v.matchAll(/#([0-9a-f]{3,8})\b/gi)].map((m) => m[1].toLowerCase())
  if (hex.some((h) => h !== '000' && h !== '000000')) return true
  if (/\b(rgba?|hsla?|oklch|oklab|lab|lch|hwb)\(\s*(from\s+#|[\d.])/i.test(v)) return true
  return NAMED_RE.test(v)
}

/** A control's own selector: its last compound names a button-like class (not a part of one, like -label or -icon). */
const isControl = (selector) => selector.split(',').some((s) => {
  const last = s.trim().split(/\s+|>|\+|~/).filter(Boolean).pop() ?? ''
  if (/::(before|after)/.test(last)) return false
  return /\.(?!is-|has-)[a-z0-9-]*(btn|button|chip|pill|tab|cta|toggle|play|primary|trigger|go)(?![a-z0-9_-])/.test(last.replace(/::?[a-z-]+(\([^)]*\))?/g, ''))
})
/** Art's own selector: its last compound names a picture class (not a state, a pseudo-element or hover/focus). */
const isArt = (selector) => selector.split(',').some((s) => {
  const last = s.trim().split(/\s+|>|\+|~/).filter(Boolean).pop() ?? ''
  return !/::(before|after)|:hover|:focus/.test(last) && /\.(?!is-)[a-z0-9-]*(cover|poster|-art|artwork|thumb|avatar)(?![a-z0-9_-])/.test(last)
})
const ringOnEdge = (decl) => (decl.prop === 'box-shadow' && /inset 0 0 0/.test(decl.value)) ||
  (decl.prop === 'border' && !/^(0|none)\b/.test(decl.value.trim()) && !/transparent/.test(decl.value))

/** A fill from the palette: a token, nothing, or the page's black. Gradients and images are art, not fills. */
const tokenFill = (value) => /^(var\(--[a-z0-9-]+\)|transparent|none|inherit|currentcolor|#000|#000000)$/i.test(norm(value)) || /gradient|url\(/.test(value)

/** On scale: calc(Npx * var(--exo-scale, 1)) with N in sizes, a type token, inherit, or 0 (text hidden). */
function onScale(value) {
  const v = norm(value)
  if (/^(inherit|0|var\(--(exo-)?type-[a-z-]+\))$/.test(v)) return true
  const m = /^calc\(([\d.]+)px \* var\(--exo-scale, 1\)\)$/.exec(v)
  return !!m && sizes.has(+m[1])
}

const DISABLE = /^design-disable-next-line\s+(exo\/[a-z-]+)\s+--\s+(\S+(?:\s+\S+){2,})$/
const disables = []
/** The rule a `design-disable-next-line` comment right before this declaration turns off, if any. */
function disabledRule(decl) {
  const prev = decl.prev()
  if (prev?.type !== 'comment') return null
  const m = DISABLE.exec(prev.text.trim())
  return m ? m[1] : null
}

const COLOUR_PROPS = /^(--.+|color|background(-color|-image)?|border(-(top|right|bottom|left|block|inline)(-start|-end)?)?(-color)?|border-image(-source)?|outline(-color)?|box-shadow|text-shadow|fill|stroke|caret-color|accent-color|text-decoration(-color)?|column-rule(-color)?|filter|-webkit-text-stroke(-color)?|-webkit-text-fill-color|-webkit-tap-highlight-color|text-emphasis(-color)?|scrollbar-color|stop-color|flood-color|lighting-color|(-webkit-)?mask(-image|-border-source)?|initial-value)$/

const problems = []
const used = new Set()
function report(file, decl, rule, message) {
  if (disabledRule(decl) === rule) {
    used.add(decl.prev())
    disables.push(`${file}:${decl.prev().source?.start?.line ?? 0}  ${rule} -- ${decl.prev().text.trim().replace(/^.*?--\s+/, '')}`)
    return
  }
  problems.push(`${file}:${decl.source?.start?.line ?? 0}  ${message} (${rule})`)
}

function check(file, root) {
  // Property names are case-insensitive in CSS (Backdrop-Filter, COLOR): compare them lower-cased. Custom properties keep their case.
  root.walkDecls((decl) => { if (!decl.prop.startsWith('--')) decl.prop = decl.prop.toLowerCase() })
  root.walkComments((c) => {
    if (/^design-disable/.test(c.text.trim()) && !DISABLE.test(c.text.trim()))
      problems.push(`${file}:${c.source?.start?.line ?? 0}  a design-disable comment names one rule and gives a real reason after "--" (at least three words)`)
  })
  root.walkDecls(/^background(-color|-image)?$/, (decl) => {
    if (isHover(decl.parent) && translucentWhite(decl.value))
      report(file, decl, 'exo/hover-fill', `A hover background is never see-through white ("${decl.value}"): use var(--hover-fill)`)
    else if (decl.prop !== 'background-image' && decl.parent?.type === 'rule' && isControl(decl.parent.selector) && !tokenFill(decl.value))
      report(file, decl, 'exo/control-fill', `A control's fill is a token ("${decl.value}"): var(--exo-fill), var(--hover-fill), var(--exo-selected), var(--color-fg) or transparent`)
  })
  root.walkDecls(/^(box-shadow|border)$/, (decl) => {
    if (decl.parent?.type === 'rule' && isArt(decl.parent.selector) && ringOnEdge(decl))
      report(file, decl, 'exo/art-edge', `Art has no ring on its edge ("${decl.prop}: ${decl.value}"): it shows only in the antialiased corner; draw an outline outside the art`)
  })
  if (!sliderFiles.has(path.normalize(file))) root.walkRules(/::-(webkit-slider|moz-range)-/, (rule) => {
    problems.push(`${file}:${rule.source?.start?.line ?? 0}  a range's track and knob are drawn once, in ${[...sliderFiles].join(' or ') || 'the slider file'} (exo/one-slider)`)
  })
  root.walkDecls('opacity', (decl) => {
    const v = decl.value.trim()
    if (isHover(decl.parent) && /^[\d.]+%?$/.test(v) && num(v) < 1)
      report(file, decl, 'exo/no-hover-fade', `Hover never fades ("opacity: ${v}"): change colour, or reveal with opacity: 1`)
  })
  const scale = (decl, value) => {
    if (onScale(value)) return
    const selector = norm(decl.parent?.selector ?? '')
    if (exceptions.some((e) => norm(e.selector) === selector && norm(e.value) === norm(value))) return
    report(file, decl, 'exo/type-scale', `"${value}" is off the type scale (${[...sizes].join('/')}px as calc(Npx * var(--exo-scale, 1))): use a scale size or name an exception in the config`)
  }
  root.walkDecls((decl) => {
    if (decl.prop === 'font-size' || /^--(exo-)?type-/.test(decl.prop)) scale(decl, decl.value)
    else if (decl.prop === 'font' && !/^(inherit|var\()/.test(decl.value.trim())) {
      const size = /(calc\([^)]*\)\)|[\d.]+(px|rem|em))/.exec(decl.value)
      if (size) scale(decl, size[1])
    }
  })
  // DESIGN.md › Motion: five times only for what moves once. A duration is a --dur-* token (or built from one with
  // calc); a literal time only as a delay, a loop's period (infinite: the spin, shine, sheen), the reduce-motion
  // override's 0.01ms, 0, or with a design-disable comment saying why (a hold, a live readout).
  const literalTime = (t) => /^[\d.]+m?s$/.test(t) && !/^0(\.0+)?m?s$/.test(t) && t !== '0.01ms'
  if (!motionExempt.has(path.normalize(file))) root.walkDecls(/^(transition|animation)(-duration)?$/, (decl) => {
    for (const item of decl.value.split(/,(?![^(]*\))/)) {
      if (/\binfinite\b/.test(item)) continue
      const duration = item.trim().split(/\s+(?![^(]*\))/).find((part) => /^[\d.]+m?s$/.test(part) || /^(var|calc)\(/.test(part))
      if (duration && literalTime(duration))
        report(file, decl, 'exo/motion-token', `"${duration}" is a literal duration: use one of DESIGN.md's five times (var(--dur-press|quick|base|move|flight))`)
    }
  })
  if (tokenFiles && !tokenFiles.has(path.normalize(file))) root.walkDecls(COLOUR_PROPS, (decl) => {
    // A mask paints nothing: only its alpha counts, so black stops (and a dark rgb with alpha) are not colours.
    const value = /^(-webkit-)?mask(-image|-border-source)?$/.test(decl.prop) ? decl.value.replace(/\bblack\b|rgba?\(\s*0[ ,]+0[ ,]+0\s*[,/][^)]*\)/gi, 'transparent') : decl.value
    if (!literalColour(value)) return
    const key = colourKey(file, decl.parent?.selector ?? decl.parent?.params ?? '', decl.prop, decl.value)
    if (baseline.get(key) > 0) { baseline.set(key, baseline.get(key) - 1); return }
    report(file, decl, 'exo/token-colour', `A colour is a token ("${decl.prop}: ${decl.value}"): use a var(--...) from ${[...tokenFiles].join(' or ')}`)
  })
  // A disable comment that turns nothing off is stale.
  root.walkComments((c) => {
    if (/^design-disable-next-line/.test(c.text.trim()) && !used.has(c))
      problems.push(`${file}:${c.source?.start?.line ?? 0}  this design-disable comment turns nothing off; remove it`)
  })
}

const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)])
let files = 0
for (const file of walk(dir).filter((f) => f.endsWith('.css'))) {
  files++
  check(path.relative(process.cwd(), file), postcss.parse(fs.readFileSync(file, 'utf8'), { from: file }))
}
// <style> blocks and style="" attributes in the pages' HTML (index.html, setup.html) are CSS too.
for (const htmlFile of config.htmlFiles ?? []) {
  const html = fs.readFileSync(htmlFile, 'utf8')
  const parts = [...html.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)].map((m) => m[1])
  for (const m of html.matchAll(/\sstyle\s*=\s*(?:"([^"]*)"|'([^']*)')/gi)) parts.push(`.inline { ${m[1] ?? m[2]} }`)
  files++
  check(path.relative(process.cwd(), htmlFile), postcss.parse(parts.join('\n'), { from: htmlFile }))
}
for (const [key, left] of baseline) for (let i = 0; i < left; i++)
  problems.push(`${configPath}  colourBaseline entry no longer matches a declaration; remove it: ${key} (exo/token-colour)`)
if (disables.length) console.log(`${disables.length} design-disable comment(s) in use:\n${disables.join('\n')}`)
if (problems.length) {
  console.error(problems.join('\n'))
  console.error(`\n${problems.length} design problem(s) in ${files} CSS file(s).`)
  process.exit(1)
}
