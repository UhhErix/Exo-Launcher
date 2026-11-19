#!/usr/bin/env node
/**
 * What is breaking, from this PC: the crash groups the reports service counted (D1 `crash_days`) and one sample of each
 * (its report in R2), and how much GitHub has not been told yet (groups and Report a problem messages waiting for the
 * daily job; the issues themselves are in the private UhhErix/Exo-Reports repository). Read only. Uses wrangler's own login; nothing is printed that the reports do not already hold
 * (they are scrubbed on the user's PC and refused by the service if anything personal is left).
 *
 *   node triage.mjs                          the last 2 days, every app, most reports first
 *   node triage.mjs --days 7 --app launcher  a week of Exo Launcher
 *   node triage.mjs --version 3.26.0         one version
 *   node triage.mjs --show 3                 also print the newest sample of the top 3 groups
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const WRANGLER = 'wrangler@4.158.0'
const arg = (name, fallback) => { const at = process.argv.indexOf('--' + name); return at > -1 ? process.argv[at + 1] : fallback }
const days = Math.max(1, Math.min(90, Number(arg('days', 2)) || 2))
const app = arg('app', null)
const version = arg('version', null)
const show = Math.max(0, Math.min(10, Number(arg('show', 0)) || 0))
if (app && !/^(launcher|cinema|browser)$/.test(app)) throw new Error('--app is launcher, cinema or browser')
if (version && !/^\d{1,4}\.\d{1,4}\.\d{1,4}$/.test(version)) throw new Error('--version is like 3.26.0')

const since = new Date(Date.now() - (days - 1) * 86_400_000).toISOString().slice(0, 10)
// The filters are checked above against fixed shapes, so they are safe to put in the statement (wrangler takes no parameters).
const where = [`day >= '${since}'`, app ? `app = '${app}'` : null, version ? `version = '${version}'` : null].filter(Boolean).join(' AND ')
const sql = `SELECT app, version, error_type, SUM(count) AS reports, MIN(day) AS first, MAX(day) AS last, MAX(sample_key) AS sample FROM crash_days WHERE ${where} GROUP BY app, version, error_type ORDER BY reports DESC LIMIT 50`

const run = (args) => execFileSync('npx', ['-y', WRANGLER, ...args], { encoding: 'utf8', shell: process.platform === 'win32', stdio: ['ignore', 'pipe', 'pipe'] })
const out = run(['d1', 'execute', 'exo-telemetry', '--remote', '--json', '--command', JSON.stringify(sql)])
const rows = JSON.parse(out.slice(out.indexOf('[')))[0]?.results ?? []
const waitingSql = 'SELECT (SELECT COUNT(*) FROM crash_groups WHERE pending = 1) AS groups, (SELECT COUNT(*) FROM issue_queue) AS messages'
const waitingOut = run(['d1', 'execute', 'exo-telemetry', '--remote', '--json', '--command', JSON.stringify(waitingSql)])
const waiting = JSON.parse(waitingOut.slice(waitingOut.indexOf('[')))[0]?.results?.[0]
if (waiting) console.log(`Waiting for GitHub: ${waiting.groups} error groups, ${waiting.messages} Report a problem messages.\n`)
if (!rows.length) { console.log(`No crash reports since ${since}.`); process.exit(0) }

console.log(`Crash reports since ${since}${app ? `, ${app}` : ''}${version ? ` ${version}` : ''}:\n`)
for (const row of rows) console.log(`${String(row.reports).padStart(6)}  ${row.app} ${row.version}  ${row.error_type}  (${row.first === row.last ? row.first : `${row.first} to ${row.last}`})`)

if (show) {
  const dir = mkdtempSync(join(tmpdir(), 'exo-triage-'))
  try {
    for (const row of rows.slice(0, show)) {
      const file = join(dir, 'sample.json')
      try {
        run(['r2', 'object', 'get', `exo-crash-reports/${row.sample}`, '--remote', '--file', file])
        const report = JSON.parse(readFileSync(file, 'utf8'))
        console.log(`\n--- ${row.app} ${row.version} ${row.error_type} (Windows ${report.osBuild})\n${report.excerpt}`)
      } catch {
        console.log(`\n--- ${row.app} ${row.version} ${row.error_type}: its sample has expired (reports are kept 30 days)`)
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}
