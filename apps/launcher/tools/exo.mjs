#!/usr/bin/env node
// exo: compact JSON status, gates, board and ship for agents across Exo's repos, so they read one line instead of
// whole logs. Each command prints one JSON line; a gate's output is cut to its error lines.
//   node tools/exo.mjs status [repo...]       branch, head, ahead/behind, dirty per repo
//   node tools/exo.mjs gates [repo] [--only a,b]  runs the repo's exo.json gates in order, stops at the first failure
//   node tools/exo.mjs board [--owner x] [--status todo] [--tier 1]
//   node tools/exo.mjs ship [repo]            last tag, commits since it, head
// Repos are found beside this one (EXO_HOME overrides). A repo adopts gates with an exo.json at its root:
//   { "gates": [ { "name": "ui", "cwd": "ui", "cmd": "npm run -s check" } ] }
import { execSync, spawnSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const HOME = process.env.EXO_HOME ?? dirname(HERE)
const REPOS = ['ExoLauncher', 'ExoSite']
const BOARD = process.env.EXO_BOARD ?? join(process.env.USERPROFILE ?? HOME, '.exo-team', 'ideas.md')

export function parseBoard(md) {
  const rows = []
  let tier = null
  for (const line of md.split(/\r?\n/)) {
    const t = /^##\s+Tier\s+(\d+)/i.exec(line)
    if (t) { tier = Number(t[1]); continue }
    if (!line.startsWith('|')) continue
    const cells = line.split('|').slice(1, -1).map(c => c.trim())
    if (cells.length < 4 || cells[0] === 'ID' || /^-+$/.test(cells[0])) continue
    rows.push({ id: cells[0], idea: cells[1], owner: cells[2], status: cells[3], tier })
  }
  return rows
}

export function errorLines(output, max = 8) {
  return output.split(/\r?\n/).map(l => l.trim())
    .filter(l => l && !/\bwarning\b/i.test(l) && /\berror\b|\bfailed\b|✖|\bFAIL\b/i.test(l) && !/\bfailed:\s*0\b|\b0 error/i.test(l))
    .slice(0, max)
}

export function parseGitStatus(text) {
  const out = { branch: null, head: null, ahead: 0, behind: 0, dirty: 0 }
  for (const line of text.split(/\r?\n/)) {
    if (line.startsWith('# branch.oid ')) out.head = line.slice(13, 21)
    else if (line.startsWith('# branch.head ')) out.branch = line.slice(14)
    else if (line.startsWith('# branch.ab ')) { const [a, b] = line.slice(12).split(' '); out.ahead = Number(a); out.behind = -Number(b) }
    else if (line && !line.startsWith('#')) out.dirty++
  }
  return out
}

const git = (repo, args) => execSync(`git -C "${repo}" ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
// A bare name is a repo beside this one (a repo also holds a project folder of the same name); a path is used as is.
const repoPath = name => (name && /[\\/]/.test(name) ? resolve(name) : join(HOME, name ?? 'ExoLauncher'))

function status(names) {
  return (names.length ? names : REPOS).map(name => {
    const repo = repoPath(name)
    if (!existsSync(join(repo, '.git'))) return { repo: name, missing: true }
    try { return { repo: name, ...parseGitStatus(git(repo, 'status --porcelain=v2 --branch')) } } catch (e) { return { repo: name, error: String(e.message).split('\n')[0] } }
  })
}

function gates(name, only) {
  const repo = repoPath(name)
  const file = join(repo, 'exo.json')
  if (!existsSync(file)) return { repo: name ?? 'ExoLauncher', error: 'no exo.json' }
  const all = JSON.parse(readFileSync(file, 'utf8')).gates ?? []
  const steps = []
  for (const g of all.filter(g => !only || only.includes(g.name))) {
    const t = Date.now()
    const r = spawnSync(g.cmd, { cwd: join(repo, g.cwd ?? '.'), shell: true, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 })
    const ok = r.status === 0
    steps.push({ name: g.name, ok, ms: Date.now() - t, ...(ok ? {} : { errors: errorLines(`${r.stdout}\n${r.stderr}`) }) })
    if (!ok) break
  }
  return { repo: name ?? 'ExoLauncher', ok: steps.length > 0 && steps.every(s => s.ok), steps }
}

function ship(name) {
  const repo = repoPath(name)
  let tag = null
  try { tag = git(repo, 'describe --tags --abbrev=0').trim() } catch { /* no tags yet */ }
  return { repo: name ?? 'ExoLauncher', tag, since: Number(git(repo, `rev-list --count ${tag ? tag + '..' : ''}HEAD`).trim()), head: git(repo, 'rev-parse --short=8 HEAD').trim() }
}

function flag(args, name) {
  const i = args.indexOf(`--${name}`)
  return i < 0 ? null : args.splice(i, 2)[1]
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [cmd, ...args] = process.argv.slice(2)
  let out
  if (cmd === 'status') out = status(args)
  else if (cmd === 'gates') { const only = flag(args, 'only')?.split(','); out = gates(args[0], only) }
  else if (cmd === 'board') {
    const owner = flag(args, 'owner'), st = flag(args, 'status'), tier = flag(args, 'tier')
    out = parseBoard(readFileSync(BOARD, 'utf8')).filter(r => (!owner || r.owner === owner) && (!st || r.status.startsWith(st)) && (!tier || r.tier === Number(tier)))
  } else if (cmd === 'ship') out = ship(args[0])
  else { out = { error: 'usage: exo status|gates|board|ship' }; process.exitCode = 2 }
  if (out?.ok === false) process.exitCode = 1
  process.stdout.write(JSON.stringify(out) + '\n')
}
