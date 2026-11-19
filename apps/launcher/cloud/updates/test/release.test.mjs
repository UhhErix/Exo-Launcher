// release.ps1's checks and its -WhatIf plan, on throwaway git fixtures. Nothing here builds, uploads or deploys: every run
// passes -WhatIf, except the one that must stop in its preflight before it changes a file.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('../release.ps1', import.meta.url))
const DASH = '—'
const WHATS_NEW = {
  launcher: 'ui/src/lib/whatsNew.json',
  cinema: 'src/ExoCinema/wwwroot/whats-new.json',
}

const git = (cwd, ...args) =>
  spawnSync('git', ['-c', 'user.name=test', '-c', 'user.email=test@example.invalid', ...args], { cwd, encoding: 'utf8' })

// A fixture: <root>/App is a repo on `branch` with VERSION `current`, and <root>/exo-site is a site with a passing test.
function fixture(app, { branch = 'release/9.9.9', current = '9.9.8', changelog = true, notes = true, dirty = false,
  tags = [], site = true } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'exo-release-'))
  const repo = join(root, 'App')
  mkdirSync(repo)
  git(repo, 'init', '-q', '-b', 'main')
  const write = (rel, text) => {
    mkdirSync(dirname(join(repo, rel)), { recursive: true })
    writeFileSync(join(repo, rel), text)
  }
  write('VERSION', `${current}\n`)
  write('CHANGELOG.md', changelog
    ? `# Changelog\n\n## 9.9.9 ${DASH} November 6, 2026\n\n- Entry.\n`
    : `# Changelog\n\n## 9.9.8 ${DASH} November 5, 2026\n\n- Entry.\n`)
  write(WHATS_NEW[app], JSON.stringify([{ version: notes ? '9.9.9' : '9.9.8', items: [] }]))
  write('README.md', 'readme\n')
  git(repo, 'add', '-A')
  git(repo, 'commit', '-q', '-m', 'base')
  if (branch !== 'main') git(repo, 'checkout', '-q', '-b', branch)
  for (const tag of tags) git(repo, 'tag', tag)
  if (dirty) writeFileSync(join(repo, 'README.md'), 'changed\n')
  if (site) {
    const dir = join(root, 'exo-site')
    mkdirSync(dir)
    writeFileSync(join(dir, 'package.json'), JSON.stringify({ private: true, scripts: { test: 'node -e "process.exit(0)"' } }))
  }
  return { root, repo }
}

function run(fx, { app = 'cinema', version = '9.9.9', repo = fx.repo, real = false } = {}) {
  const args = ['-NoProfile', '-File', script, '-App', app, '-Repo', repo, '-Version', version]
  if (!real) args.push('-WhatIf')
  const r = spawnSync('pwsh', args, { encoding: 'utf8' })
  return { code: r.status, out: `${r.stdout}${r.stderr}` }
}

const cleanup = (t, fx) => t.after(() => rmSync(fx.root, { recursive: true, force: true }))

test('-WhatIf prints every step for launcher and changes nothing', (t) => {
  const fx = fixture('launcher')
  cleanup(t, fx)
  const { code, out } = run(fx, { app: 'launcher' })
  assert.equal(code, 0, out)
  for (const step of [
    '[PASS] exo-site tests (npm test) exits 0',
    'WHATIF would write VERSION: 9.9.8 -> 9.9.9',
    'WHATIF would run: pwsh -NoProfile -File tools\\Publish-Docs.ps1',
    'WHATIF would run: pwsh -NoProfile -File tools\\Build-Release.ps1',
    'publish-release.ps1 -App launcher -Tag v9.9.9',
    'WHATIF would run: npm.cmd run build',
    'WHATIF would run: npx.cmd -y wrangler@4.158.0 deploy',
    'WHATIF would check https://updates.getexo.app/v1/launcher/latest names v9.9.9',
    'WHATIF PASS',
  ]) assert.ok(out.includes(step), `missing: ${step}\n${out}`)
  assert.equal(readFileSync(join(fx.repo, 'VERSION'), 'utf8'), '9.9.8\n')
  assert.equal(existsSync(join(fx.repo, 'artifacts')), false)
})

test('an app without a Publish-Docs step (cinema) does not print one', (t) => {
  const fx = fixture('cinema')
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 0, out)
  assert.equal(out.includes('Publish-Docs'), false)
})

test('uncommitted changes to tracked files block, and the plan still prints', (t) => {
  const fx = fixture('cinema', { dirty: true })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes('[FAIL] no uncommitted changes to tracked files (1 listed)'), out)
  assert.ok(out.includes('WHATIF would run: npm.cmd run build'), out)
})

test('the release branch is required', (t) => {
  const fx = fixture('cinema', { branch: 'feature/x' })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes("[FAIL] on release/9.9.9 (now 'feature/x')"), out)
})

test('the new version must be newer than VERSION', (t) => {
  const fx = fixture('cinema')
  cleanup(t, fx)
  const { code, out } = run(fx, { version: '9.9.7' })
  assert.equal(code, 1)
  assert.ok(out.includes("[FAIL] -Version 9.9.7 is newer than VERSION ('9.9.8')"), out)
})

test('an existing tag for the version blocks', (t) => {
  const fx = fixture('cinema', { tags: ['built/v9.9.9'] })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes('[FAIL] no v9.9.9 or built/v9.9.9 tag yet'), out)
})

test('CHANGELOG.md needs the version entry', (t) => {
  const fx = fixture('cinema', { changelog: false })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes("[FAIL] CHANGELOG.md has the '## 9.9.9' entry"), out)
})

test("What's New needs an entry for the version", (t) => {
  const fx = fixture('cinema', { notes: false })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes('has an entry for 9.9.9'), out)
})

test('exo-site beside the app repo is required', (t) => {
  const fx = fixture('cinema', { site: false })
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 1)
  assert.ok(out.includes('[FAIL] exo-site beside the app repo'), out)
})

test('a -Repo that is not a git checkout blocks', (t) => {
  const fx = fixture('cinema')
  cleanup(t, fx)
  const { code, out } = run(fx, { repo: join(fx.root, 'nowhere') })
  assert.equal(code, 1)
  assert.ok(out.includes('[FAIL] -Repo is a git checkout'), out)
})

test('a real run stops in its preflight, before it changes anything', (t) => {
  const fx = fixture('cinema', { dirty: true })
  cleanup(t, fx)
  const { code, out } = run(fx, { real: true })
  assert.equal(code, 1)
  assert.ok(out.includes("STOP at '1. Preflight"), out)
  assert.equal(readFileSync(join(fx.repo, 'VERSION'), 'utf8'), '9.9.8\n')
  assert.equal(existsSync(join(fx.repo, 'artifacts')), false)
})

test('the plan checks the change list at the update address, passes the app folder along, and ends with the live check', (t) => {
  const fx = fixture('cinema')
  cleanup(t, fx)
  const { code, out } = run(fx)
  assert.equal(code, 0, out)
  assert.ok(out.includes('WHATIF would check https://updates.getexo.app/v1/cinema/changelog'), out)
  assert.ok(out.includes('WHATIF would add -Payload'), out)
  assert.ok(out.includes('8. Live check'), out)
  assert.ok(!out.includes('Code backup'), out)
})
