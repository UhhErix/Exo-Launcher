// Removes CSS rule blocks whose every selector names an `.exo-*` class that no
// TSX/TS/C#/JS source references. Blocks with any live selector are kept whole.
// A class built at run time ("exo-epic-setup-" + name) shows up as a prefix ending in "-": every class under it counts as
// used, so a dynamic class is never pruned.
// Usage: node scripts/prune-dead-css.mjs [--write | --check]
//   --check: writes nothing; exits 1 when --write would remove anything (npm run check runs it).
import fs from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..', '..')
const write = process.argv.includes('--write')
const check = process.argv.includes('--check')
const walk = (dir, exts, skip = /[\\/](obj|bin|node_modules|wwwroot|extensions|artifacts)[\\/]/) => {
  const out = []
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (skip.test(full + path.sep)) continue
    if (entry.isDirectory()) out.push(...walk(full, exts, skip))
    else if (exts.some(ext => entry.name.endsWith(ext))) out.push(full)
  }
  return out
}

const code = [
  ...walk(path.join(root, 'ui', 'src'), ['.tsx', '.ts']),
  ...walk(path.join(root, 'ExoLauncher'), ['.cs', '.js']),
].map(file => fs.readFileSync(file, 'utf8')).join('\n')
const tokens = code.match(/exo-[a-z0-9-]*/g) ?? []
const used = new Set(tokens.filter(token => !token.endsWith('-')))
// Run-time class names are built in the UI's TSX/TS ("exo-btn-" + tone); a file name in C# ("exo-epic-setup-") is not a class.
const uiPrefixes = walk(path.join(root, 'ui', 'src'), ['.tsx', '.ts']).map(file => fs.readFileSync(file, 'utf8')).join('\n')
  .match(/exo-[a-z0-9-]*-(?=[`'"\s$])/g) ?? []
const prefixes = [...new Set(uiPrefixes)]
const isUsed = (name) => used.has(name) || prefixes.some(prefix => name.startsWith(prefix))

const cssFiles = walk(path.join(root, 'ui', 'src'), ['.css'])
let removedBlocks = 0, removedBytes = 0

function prune(text) {
  // Tokenise into top-level blocks; recurse into @media / @container / @supports.
  let out = '', i = 0
  while (i < text.length) {
    const open = text.indexOf('{', i)
    if (open < 0) { out += text.slice(i); break }
    const prelude = text.slice(i, open)
    // Find the matching close brace.
    let depth = 1, j = open + 1
    while (j < text.length && depth) { if (text[j] === '{') depth++; else if (text[j] === '}') depth--; j++ }
    const body = text.slice(open + 1, j - 1)
    const selectorText = prelude.replace(/\/\*[\s\S]*?\*\//g, '').trim()
    if (/^@(media|container|supports|layer)/.test(selectorText)) {
      const inner = prune(body)
      if (inner.trim()) out += prelude + '{' + inner + '}'
      else { removedBlocks++; removedBytes += j - i }
    } else if (selectorText.startsWith('@') || !selectorText) {
      out += text.slice(i, j)
    } else {
      const selectors = selectorText.split(',').map(s => s.trim()).filter(Boolean)
      const dead = selectors.length > 0 && selectors.every(sel => {
        const classes = sel.match(/\.(exo-[a-z0-9-]+)/g)?.map(c => c.slice(1)) ?? []
        return classes.length > 0 && classes.some(c => !isUsed(c))
      })
      if (dead) { removedBlocks++; removedBytes += j - i }
      else out += text.slice(i, j)
    }
    i = j
  }
  return out
}

for (const file of cssFiles) {
  const before = fs.readFileSync(file, 'utf8')
  const after = prune(before).replace(/\n{3,}/g, '\n\n')
  if (after !== before) {
    console.log(`${path.relative(root, file)}: ${before.length} -> ${after.length}`)
    if (write) fs.writeFileSync(file, after)
  }
}
console.log(`${write ? 'Removed' : 'Would remove'} ${removedBlocks} blocks, ${removedBytes} bytes`)
if (check && removedBlocks > 0) {
  console.error('Dead .exo-* CSS is in the styles: run `npm run css:prune` to remove it.')
  process.exit(1)
}
