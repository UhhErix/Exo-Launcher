// SPDX-License-Identifier: MIT

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const launcherRoot = path.resolve(__dirname, '..', '..', '..')

function walk(dir, exts, out = []) {
  if (!fs.existsSync(dir)) return out
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name === 'dist') continue
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) walk(full, exts, out)
    else if (exts.some(e => entry.name.endsWith(e))) out.push(full)
  }
  return out
}

const CSS_CHECKS = [
  ['::before / ::after pseudo-elements', /::?(before|after)\b/g],
  ['@keyframes animations', /@keyframes\b/g],
  ['CSS transition', /\btransition(-property|-duration)?\s*:/g],
  ['CSS transform (translate/scale/rotate)', /\btransform\s*:/g],
  ['backdrop-filter', /\bbackdrop-filter\s*:/g],
  ['mask-image / -webkit-mask-image', /\b(-webkit-)?mask(-image)?\s*:/g],
  ['linear-gradient / radial-gradient', /\b(linear|radial|conic)-gradient\(/g],
  ['color-mix()', /\bcolor-mix\(/g],
  [':has() relational pseudo-class', /:has\(/g],
  [':nth-child / :nth-of-type', /:nth-(child|of-type)\(/g],
  ['position: sticky', /position\s*:\s*sticky/g],
  ['position: fixed', /position\s*:\s*fixed/g],
  ['CSS Grid (display: grid / grid-template)', /\bgrid-template-(columns|rows|areas)\s*:/g],
  ['@container queries', /@container\b/g],
  ['CSS filter (blur/drop-shadow/brightness)', /(?<!backdrop-)\bfilter\s*:/g],
  ['clip-path', /\bclip-path\s*:/g],
  ['linear() spring easing', /\blinear\([0-9.,%\s]+\)/g],
  ['calc() with mixed % and px', /calc\([^)]*%[^)]*px|calc\([^)]*px[^)]*%/g],
]

const DOM_CHECKS = [
  ['document.* direct access', /\bdocument\.(querySelector|querySelectorAll|getElementById|createElement|documentElement|body|addEventListener|hidden|activeElement|styleSheets)/g],
  ['window.* listeners & globals', /\bwindow\.(addEventListener|matchMedia|getComputedStyle|parent|open|location|innerWidth|innerHeight|devicePixelRatio)/g],
  ['getBoundingClientRect / offset* / client*', /\.(getBoundingClientRect|offsetWidth|offsetHeight|offsetLeft|offsetTop|clientWidth|clientHeight|scrollHeight)\b/g],
  ['ResizeObserver', /\bResizeObserver\b/g],
  ['MutationObserver', /\bMutationObserver\b/g],
  ['IntersectionObserver', /\bIntersectionObserver\b/g],
  ['requestAnimationFrame', /\brequestAnimationFrame\b/g],
  ['createPortal (react-dom)', /\bcreatePortal\b/g],
  ['Canvas 2D / WebGL (getContext)', /\.getContext\(/g],
  ['<video> / <audio> / MediaSource / HTMLMediaElement', /<(video|audio)\b|\bHTMLVideoElement\b|\bHTMLAudioElement\b|\bAudioContext\b/g],
  ['<iframe> embedding', /<iframe\b|\bHTMLIFrameElement\b/g],
  ['Inline <svg> elements', /<svg\b/g],
  ['dangerouslySetInnerHTML', /\bdangerouslySetInnerHTML\b/g],
  ['navigator.clipboard / navigator.hid', /\bnavigator\.(clipboard|hid)\b/g],
  ['WebSocket / fetch / EventSource', /\b(new WebSocket|fetch\(|new EventSource)\b/g],
  ['localStorage / sessionStorage', /\b(localStorage|sessionStorage)\b/g],
  ['Web Animations API (.animate())', /\.animate\s*\(/g],
  ['Pointer capture (setPointerCapture)', /\bsetPointerCapture\b/g],
  ['File / Blob / URL.createObjectURL', /\b(URL\.createObjectURL|new Blob|new FileReader)\b/g],
]

function scan(files, checks) {
  const results = []
  for (const [label, rx] of checks) {
    let count = 0
    let fileCount = 0
    const samples = []
    for (const f of files) {
      const text = fs.readFileSync(f, 'utf8')
      const matches = text.match(rx)
      if (matches && matches.length > 0) {
        count += matches.length
        fileCount++
        if (samples.length < 3) samples.push(path.relative(launcherRoot, f).replace(/\\/g, '/'))
      }
    }
    results.push({ label, count, fileCount, samples })
  }
  return results
}

const uiCss = walk(path.join(launcherRoot, 'ui', 'src'), ['.css'])
const mouseCss = walk(path.join(launcherRoot, 'mouse', 'src'), ['.css'])
const uiTs = walk(path.join(launcherRoot, 'ui', 'src'), ['.ts', '.tsx'])
const mouseTs = walk(path.join(launcherRoot, 'mouse', 'src'), ['.ts', '.tsx'])

const report = {
  counts: {
    uiCssFiles: uiCss.length,
    mouseCssFiles: mouseCss.length,
    uiTsFiles: uiTs.length,
    mouseTsFiles: mouseTs.length,
  },
  cssUi: scan(uiCss, CSS_CHECKS),
  cssMouse: scan(mouseCss, CSS_CHECKS),
  domUi: scan(uiTs, DOM_CHECKS),
  domMouse: scan(mouseTs, DOM_CHECKS),
}

fs.writeFileSync(path.join(__dirname, 'dist', 'breakage-audit.json'), JSON.stringify(report, null, 2))
console.log('=== CSS BREAKAGE AUDIT (ui/ + mouse/) ===')
for (let i = 0; i < CSS_CHECKS.length; i++) {
  const u = report.cssUi[i]
  const m = report.cssMouse[i]
  console.log(`${u.label.padEnd(42)} | ui: ${String(u.count).padStart(4)} (${u.fileCount}f) | mouse: ${String(m.count).padStart(4)} (${m.fileCount}f)`)
}
console.log('\n=== DOM / BROWSER API AUDIT (ui/ + mouse/) ===')
for (let i = 0; i < DOM_CHECKS.length; i++) {
  const u = report.domUi[i]
  const m = report.domMouse[i]
  console.log(`${u.label.padEnd(46)} | ui: ${String(u.count).padStart(4)} (${u.fileCount}f) | mouse: ${String(m.count).padStart(4)} (${m.fileCount}f)`)
}
