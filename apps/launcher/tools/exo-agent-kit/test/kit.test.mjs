import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import { verifyGuarantees } from '../scripts/verify-guarantees.mjs'

const KIT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const REPO_ROOT = path.resolve(KIT_ROOT, '..', '..', '..', '..')
const LAUNCHER_WORK_DIR = path.join(REPO_ROOT, 'apps', 'launcher', 'work')
const DIST_MCP = path.join(KIT_ROOT, 'dist', 'mcp-server.mjs')
const DIST_SESSION = path.join(KIT_ROOT, 'dist', 'session-start.mjs')
const DIST_ORCA = path.join(KIT_ROOT, 'dist', 'exo-orca-helper.mjs')

function createTempDir(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix))
}

function runNode(scriptPath, args = [], opts = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [scriptPath, ...args], {
      cwd: opts.cwd ?? REPO_ROOT,
      env: { ...process.env, ...opts.env },
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', (d) => (stdout += d.toString('utf8')))
    child.stderr.on('data', (d) => (stderr += d.toString('utf8')))
    child.on('error', reject)
    child.on('close', (code) => resolve({ code, stdout, stderr }))
  })
}

class McpTestClient {
  constructor(env = {}) {
    this.nextId = 1
    this.pending = new Map()
    this.buf = ''
    this.child = spawn(process.execPath, [DIST_MCP], {
      cwd: REPO_ROOT,
      env: { ...process.env, ...env },
      shell: false,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe'],
    })
    this.child.stdout.on('data', (chunk) => {
      this.buf += chunk.toString('utf8')
      let nl
      while ((nl = this.buf.indexOf('\n')) >= 0) {
        const line = this.buf.slice(0, nl).trim()
        this.buf = this.buf.slice(nl + 1)
        if (!line) continue
        const msg = JSON.parse(line)
        const resolver = this.pending.get(msg.id)
        if (resolver) {
          this.pending.delete(msg.id)
          resolver(msg)
        }
      }
    })
  }

  request(method, params = {}) {
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`Timeout waiting for ${method} (id=${id})`))
      }, 60_000)
      this.pending.set(id, (msg) => {
        clearTimeout(timer)
        resolve(msg)
      })
      this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
    })
  }

  notify(method, params = {}) {
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n')
  }

  async callTool(name, args = {}) {
    const res = await this.request('tools/call', { name, arguments: args })
    assert.equal(res.error, undefined, `Unexpected JSON-RPC error for ${name}: ${JSON.stringify(res.error)}`)
    const text = res.result.content[0].text
    return {
      text,
      isError: res.result.isError === true,
      jsonBytes: Buffer.byteLength(JSON.stringify(text), 'utf8'),
    }
  }

  async close() {
    this.child.stdin.end()
    await new Promise((resolve) => {
      this.child.once('close', resolve)
      setTimeout(() => {
        try {
          this.child.kill()
        } catch {}
        resolve()
      }, 2000)
    })
  }
}

const tempCacheDir = createTempDir('exo-kit-test-cache-')
const client = new McpTestClient({ EXO_KIT_CACHE_DIR: tempCacheDir })

test.before(async () => {
  const init = await client.request('initialize', {
    protocolVersion: '2025-03-26',
    capabilities: {},
    clientInfo: { name: 'exo-kit-test', version: '1.0.0' },
  })
  assert.equal(init.result.serverInfo.name, 'exo-agent-kit')
  client.notify('notifications/initialized', {})
})

test.after(async () => {
  await client.close()
  fs.rmSync(tempCacheDir, { recursive: true, force: true })
})

test('tools/list returns all 8 tools in exact rank order', async () => {
  const list = await client.request('tools/list')
  const names = list.result.tools.map((t) => t.name)
  assert.deepEqual(names, [
    'exo_run_gates',
    'batch_search',
    'exo_contract',
    'exo_rune_api',
    'quick_outline',
    'exo_snapshot',
    'exo_parity_snap',
    'apply_edits',
  ])
})

test('1. exo_run_gates maps paths and executes real parity-test gate', async () => {
  const dry = await client.callTool('exo_run_gates', {
    cwd: REPO_ROOT,
    paths: ['apps/launcher/native-ui/crates/exo-ui/src/main.rs', 'apps/launcher/ui/src/lib/host.ts'],
    dryRun: true,
  })
  assert.equal(dry.isError, false)
  assert.match(dry.text, /cargo-fmt/)
  assert.match(dry.text, /cargo-clippy/)
  assert.match(dry.text, /cargo-test/)
  assert.match(dry.text, /ui-check/)
  assert.match(dry.text, /ui-dead/)

  const realGate = await client.callTool('exo_run_gates', {
    cwd: REPO_ROOT,
    gates: ['parity-test'],
  })
  assert.equal(realGate.isError, false, realGate.text)
  assert.match(realGate.text, /1\/1 PASS/)
  assert.ok(realGate.jsonBytes <= 8800)
})

test('2. batch_search runs concurrent queries and enforces read-only git', async () => {
  const res = await client.callTool('batch_search', {
    cwd: REPO_ROOT,
    queries: [
      { id: 'bridge', type: 'text', query: 'object? result = method switch', path: 'apps/launcher/ExoLauncher/Services' },
      { id: 'glob', type: 'glob', query: '*.toml', path: 'apps/launcher/native-ui' },
      { id: 'fuzzy', type: 'fuzzy', query: 'hostcontractlib', path: 'apps/launcher/ui' },
      { id: 'git-ok', type: 'git', query: 'log -n 2 --oneline' },
      { id: 'git-blocked', type: 'git', query: 'status -v' },
    ],
  })
  assert.equal(res.isError, false)
  assert.match(res.text, /WebHostBridge\.cs/)
  assert.match(res.text, /Cargo\.toml/)
  assert.match(res.text, /host-contract-lib\.mjs/)
  assert.match(res.text, /status -v\/--verbose is forbidden/)
  assert.ok(res.jsonBytes <= 8800)
})

test('3. exo_contract resolves game.get across host-v1.json, TS, C#, and Rust', async () => {
  const res = await client.callTool('exo_contract', {
    cwd: REPO_ROOT,
    method: 'game.get',
  })
  assert.equal(res.isError, false, res.text)
  assert.match(res.text, /METHOD: game\.get/)
  assert.match(res.text, /host-v1\.json reply:/)
  assert.match(res.text, /TS host\.ts: host\.getGame/)
  assert.match(res.text, /WebHostBridge\.cs:\d+ => .*GameGetAsync/)
  assert.match(res.text, /C# handler: .*WebHostBridge.*\.cs:\d+/)
  assert.match(res.text, /Rust const: METHOD_GAME_GET/)
  assert.ok(res.jsonBytes <= 8800)
})

test('4. exo_rune_api finds pub const fn tracking in pinned Rune rev', async () => {
  const res = await client.callTool('exo_rune_api', {
    cwd: REPO_ROOT,
    query: 'tracking',
    kind: 'fn',
  })
  assert.equal(res.isError, false, res.text)
  assert.match(res.text, /rev=1735bc2/i)
  assert.match(res.text, /crates\/rune-core\/src\/tok\.rs:108/)
  assert.match(res.text, /pub const fn tracking/)
  assert.ok(res.jsonBytes <= 8800)
})

test('5. quick_outline handles real C#, Rust, TS and deeply indented members', async () => {
  const tmpDir = createTempDir('exo-kit-outline-')
  try {
    const deepCs = path.join(tmpDir, 'DeepSample.cs')
    fs.writeFileSync(
      deepCs,
      [
        'namespace Exo.Sample',
        '{',
        '    public partial class Outer',
        '    {',
        '        public class Inner',
        '        {',
        '            public string Title { get; set; } = "";',
        '            public int Count => 42;',
        '            public async Task<bool> RunAsync(string id)',
        '            {',
        '                return true;',
        '            }',
        '        }',
        '    }',
        '}',
      ].join('\n'),
      'utf8',
    )
    const deepRs = path.join(tmpDir, 'deep.rs')
    fs.writeFileSync(
      deepRs,
      [
        'pub mod outer {',
        '    pub mod inner {',
        '        pub struct Style;',
        '        impl Style {',
        '            pub const fn tracking(self, px: f32) -> Self {',
        '                self',
        '            }',
        '        }',
        '    }',
        '}',
      ].join('\n'),
      'utf8',
    )

    const res = await client.callTool('quick_outline', {
      cwd: REPO_ROOT,
      paths: [
        deepCs,
        deepRs,
        'apps/launcher/native-ui/crates/exo-rpc/src/lib.rs',
        'apps/launcher/ExoLauncher/Services/WebHostBridge.cs',
        'apps/launcher/ui/src/lib/host.ts',
      ],
    })
    assert.equal(res.isError, false, res.text)
    assert.match(res.text, /prop Title/)
    assert.match(res.text, /prop Count/)
    assert.match(res.text, /method RunAsync/)
    assert.match(res.text, /fn tracking/)
    assert.ok(res.jsonBytes <= 8800)
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true })
  }
})

test('6. exo_snapshot and dist/session-start.mjs emit branch guard within 1500 bytes', async () => {
  const snap = await client.callTool('exo_snapshot', { cwd: REPO_ROOT })
  assert.equal(snap.isError, false)
  assert.match(snap.text, /\[exo-snapshot\]/)
  assert.match(snap.text, /Exo Launcher/)
  assert.match(snap.text, /--base-branch/)
  assert.ok(snap.jsonBytes <= 1500)

  const claudeHook = await runNode(DIST_SESSION, ['--agent', 'claude', '--cwd', REPO_ROOT])
  assert.equal(claudeHook.code, 0)
  assert.match(claudeHook.stdout, /\[exo-snapshot\]/)
  assert.ok(Buffer.byteLength(JSON.stringify(claudeHook.stdout.trim()), 'utf8') <= 1500)

  const agyHook = await runNode(DIST_SESSION, ['--agent', 'antigravity', '--cwd', REPO_ROOT])
  assert.equal(agyHook.code, 0)
  const parsed = JSON.parse(agyHook.stdout)
  assert.ok(Array.isArray(parsed.injectSteps))
  assert.match(parsed.injectSteps[0].systemMessage, /\[exo-snapshot\]/)
})

test('7. exo_parity_snap enforces write confinement, refuses installed exe, and wraps parity-compare.py', async () => {
  const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local')
  const refusedExe = await client.callTool('exo_parity_snap', {
    cwd: REPO_ROOT,
    scenes: ['library'],
    hostExe: path.join(localAppData, 'ExoLauncher', 'app', 'ExoLauncher.exe'),
  })
  assert.equal(refusedExe.isError, true)
  assert.match(refusedExe.text, /Refused/)

  const refusedOutDir = await client.callTool('exo_parity_snap', {
    cwd: REPO_ROOT,
    scenes: ['library'],
    outDir: path.join(os.tmpdir(), 'exo-parity-escape'),
    dryRun: true,
  })
  assert.equal(refusedOutDir.isError, true)
  assert.match(refusedOutDir.text, /Refused outDir/)

  const refusedGitOutDir = await client.callTool('exo_parity_snap', {
    cwd: REPO_ROOT,
    scenes: ['library'],
    outDir: '.git/parity-out',
    dryRun: true,
  })
  assert.equal(refusedGitOutDir.isError, true)
  assert.match(refusedGitOutDir.text, /Refused outDir/)

  const dry = await client.callTool('exo_parity_snap', {
    cwd: REPO_ROOT,
    scenes: ['library', 'settings'],
    dryRun: true,
  })
  assert.equal(dry.isError, false)
  assert.match(dry.text, /parity-snap\.ps1/)

  const workRunDir = path.join(LAUNCHER_WORK_DIR, `exo-kit-parity-${crypto.randomBytes(4).toString('hex')}`)
  fs.mkdirSync(workRunDir, { recursive: true })
  try {
    const refPng = path.join(workRunDir, 'ref.png')
    const snapPng = path.join(workRunDir, 'snap.png')
    const outPng = path.join(workRunDir, 'diff.png')
    const gen = await new Promise((resolve, reject) => {
      const p = spawn(
        'python',
        [
          '-I',
          '-c',
          'import sys; from PIL import Image; Image.new("RGB",(64,48),(10,20,30)).save(sys.argv[1]); Image.new("RGB",(64,48),(20,20,30)).save(sys.argv[2])',
          refPng,
          snapPng,
        ],
        { shell: false, windowsHide: true },
      )
      p.on('error', reject)
      p.on('close', resolve)
    })
    assert.equal(gen, 0)

    const refusedCompareOut = await client.callTool('exo_parity_snap', {
      cwd: REPO_ROOT,
      compare: {
        refPath: refPng,
        snapPath: snapPng,
        outPath: path.join(os.tmpdir(), 'escape-diff.png'),
      },
    })
    assert.equal(refusedCompareOut.isError, true)
    assert.match(refusedCompareOut.text, /Refused compare\.outPath/)

    const cmp = await client.callTool('exo_parity_snap', {
      cwd: REPO_ROOT,
      compare: { refPath: refPng, snapPath: snapPng, outPath: outPng },
    })
    assert.equal(cmp.isError, false, cmp.text)
    assert.match(cmp.text, /meanAbsDiff=/)
    assert.match(cmp.text, /worst 8x6 grid cells/)
    assert.ok(fs.existsSync(outPng))
    assert.ok(cmp.jsonBytes <= 8800)
  } finally {
    fs.rmSync(workRunDir, { recursive: true, force: true })
  }
})

test('8. apply_edits handles atomic rollback, CRLF preservation, JSON-string array, and allowlisted verify', async () => {
  const tmpDir = createTempDir('exo-kit-edits-')
  try {
    const f1 = path.join(tmpDir, 'a.txt')
    const f2 = path.join(tmpDir, 'b.txt')
    const verifyScript = path.join(tmpDir, 'check.mjs')
    const testScript = path.join(tmpDir, 'sample.test.mjs')
    fs.writeFileSync(f1, 'alpha\r\nbeta\r\ngamma\r\n', 'utf8')
    fs.writeFileSync(f2, 'one\ntwo\nthree\n', 'utf8')
    fs.writeFileSync(verifyScript, 'process.stdout.write("verify-ok")\n', 'utf8')
    fs.writeFileSync(
      testScript,
      'import test from "node:test"\nimport assert from "node:assert/strict"\ntest("ok", () => assert.equal(1, 1))\n',
      'utf8',
    )

    const abort = await client.callTool('apply_edits', {
      cwd: tmpDir,
      edits: [
        { path: 'a.txt', oldText: 'beta', newText: 'BETA' },
        { path: 'b.txt', oldText: 'two_missing', newText: 'TWO' },
      ],
    })
    assert.equal(abort.isError, true)
    assert.match(abort.text, /ABORTED/)
    assert.match(abort.text, /closest L2/)
    assert.equal(fs.readFileSync(f1, 'utf8'), 'alpha\r\nbeta\r\ngamma\r\n')

    const applied = await client.callTool('apply_edits', {
      cwd: tmpDir,
      edits: JSON.stringify([
        { path: 'a.txt', oldText: 'beta', newText: 'BETA' },
        { path: 'b.txt', oldText: 'two', newText: 'TWO' },
      ]),
      verify: {
        command: 'node',
        args: ['check.mjs'],
      },
    })
    assert.equal(applied.isError, false, applied.text)
    assert.match(applied.text, /Verify \[node check\.mjs\] PASS/)
    assert.equal(fs.readFileSync(f1, 'utf8'), 'alpha\r\nBETA\r\ngamma\r\n')
    assert.equal(fs.readFileSync(f2, 'utf8'), 'one\nTWO\nthree\n')

    const testVerify = await client.callTool('apply_edits', {
      cwd: tmpDir,
      edits: [{ path: 'b.txt', oldText: 'TWO', newText: 'TWO_2' }],
      verify: {
        command: 'node',
        args: ['--test', 'sample.test.mjs'],
      },
    })
    assert.equal(testVerify.isError, false, testVerify.text)
    assert.match(testVerify.text, /Verify \[node --test sample\.test\.mjs\] PASS/)
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true })
  }
})

test('8b. apply_edits refuses absolute outside path, .. escape, and .git/ writes', async () => {
  const parentDir = createTempDir('exo-kit-boundary-')
  try {
    const wsDir = path.join(parentDir, 'workspace')
    const outsideDir = path.join(parentDir, 'outside')
    fs.mkdirSync(path.join(wsDir, '.git'), { recursive: true })
    fs.mkdirSync(outsideDir, { recursive: true })
    const outsideFile = path.join(outsideDir, 'secret.txt')
    const gitConfig = path.join(wsDir, '.git', 'config')
    fs.writeFileSync(outsideFile, 'do-not-touch\n', 'utf8')
    fs.writeFileSync(gitConfig, '[core]\n', 'utf8')

    const absRefused = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: outsideFile, oldText: 'do-not-touch', newText: 'pwned' }],
    })
    assert.equal(absRefused.isError, true)
    assert.match(absRefused.text, /Refused:.*outside workspace root/)
    assert.equal(fs.readFileSync(outsideFile, 'utf8'), 'do-not-touch\n')

    const dotDotExisting = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: '../outside/secret.txt', oldText: 'do-not-touch', newText: 'pwned' }],
    })
    assert.equal(dotDotExisting.isError, true)
    assert.match(dotDotExisting.text, /Refused:.*outside workspace root/)
    assert.equal(fs.readFileSync(outsideFile, 'utf8'), 'do-not-touch\n')

    const dotDotNew = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: '../outside/new-file.txt', createIfMissing: true, content: 'nope' }],
    })
    assert.equal(dotDotNew.isError, true)
    assert.match(dotDotNew.text, /Refused:.*outside workspace root/)
    assert.equal(fs.existsSync(path.join(outsideDir, 'new-file.txt')), false)

    const gitWrite = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: '.git/config', oldText: '[core]', newText: '[evil]' }],
    })
    assert.equal(gitWrite.isError, true)
    assert.match(gitWrite.text, /Refused:.*inside \.git\//)
    assert.equal(fs.readFileSync(gitConfig, 'utf8'), '[core]\n')

    const gitNewHook = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: '.git/hooks/pre-commit', createIfMissing: true, content: 'evil' }],
    })
    assert.equal(gitNewHook.isError, true)
    assert.match(gitNewHook.text, /Refused:.*inside \.git\//)
    assert.equal(fs.existsSync(path.join(wsDir, '.git', 'hooks', 'pre-commit')), false)
  } finally {
    fs.rmSync(parentDir, { recursive: true, force: true })
  }
})

test('8c. apply_edits refuses junction/symlink escape outside workspace root', async (t) => {
  const parentDir = createTempDir('exo-kit-link-escape-')
  try {
    const wsDir = path.join(parentDir, 'workspace')
    const outsideDir = path.join(parentDir, 'outside')
    fs.mkdirSync(wsDir, { recursive: true })
    fs.mkdirSync(outsideDir, { recursive: true })
    const outsideFile = path.join(outsideDir, 'target.txt')
    fs.writeFileSync(outsideFile, 'original\n', 'utf8')

    const linkPath = path.join(wsDir, 'escape-link')
    try {
      fs.symlinkSync(outsideDir, linkPath, process.platform === 'win32' ? 'junction' : 'dir')
    } catch (err) {
      t.skip(`OS refused symlink/junction creation without elevation: ${err.code ?? err.message}`)
      return
    }

    const existingViaLink = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: 'escape-link/target.txt', oldText: 'original', newText: 'escaped' }],
    })
    assert.equal(existingViaLink.isError, true)
    assert.match(existingViaLink.text, /Refused:.*outside workspace root/)
    assert.equal(fs.readFileSync(outsideFile, 'utf8'), 'original\n')

    const newViaLink = await client.callTool('apply_edits', {
      cwd: wsDir,
      edits: [{ path: 'escape-link/nested/created.txt', createIfMissing: true, content: 'escaped' }],
    })
    assert.equal(newViaLink.isError, true)
    assert.match(newViaLink.text, /Refused:.*outside workspace root/)
    assert.equal(fs.existsSync(path.join(outsideDir, 'nested', 'created.txt')), false)
  } finally {
    fs.rmSync(parentDir, { recursive: true, force: true })
  }
})

test('8d. apply_edits verify refuses node -e, python -c, rundll32, and verify cwd outside repo', async () => {
  const parentDir = createTempDir('exo-kit-verify-guard-')
  try {
    const wsDir = path.join(parentDir, 'workspace')
    const outsideDir = path.join(parentDir, 'outside')
    fs.mkdirSync(wsDir, { recursive: true })
    fs.mkdirSync(outsideDir, { recursive: true })
    const filePath = path.join(wsDir, 'file.txt')
    fs.writeFileSync(filePath, 'hello\n', 'utf8')

    const cases = [
      {
        label: 'node -e',
        verify: { command: 'node', args: ['-e', 'console.log(1)'] },
        expect: /Refused node verify flag "-e"/,
      },
      {
        label: 'node --eval',
        verify: { command: 'node', args: ['--eval=console.log(1)'] },
        expect: /Refused node verify flag "--eval"/,
      },
      {
        label: 'node -p',
        verify: { command: 'node', args: ['-p', '1+1'] },
        expect: /Refused node verify flag "-p"/,
      },
      {
        label: 'node --import',
        verify: { command: 'node', args: ['--import', './x.mjs', 'file.txt'] },
        expect: /Refused node verify flag "--import"/,
      },
      {
        label: 'node -r',
        verify: { command: 'node', args: ['-r', './x.cjs', 'file.txt'] },
        expect: /Refused node verify flag "-r"/,
      },
      {
        label: 'python -c',
        verify: { command: 'python', args: ['-c', 'print(1)'] },
        expect: /Refused python verify/,
      },
      {
        label: 'python -I -c',
        verify: { command: 'python', args: ['-I', '-c', 'print(1)'] },
        expect: /Refused python verify/,
      },
      {
        label: 'rundll32',
        verify: { command: 'rundll32', args: ['url.dll,FileProtocolHandler', 'x'] },
        expect: /Refused verify command "rundll32"/,
      },
      {
        label: 'mshta',
        verify: { command: 'mshta', args: ['about:blank'] },
        expect: /Refused verify command "mshta"/,
      },
      {
        label: 'verify cwd .. escape',
        verify: { command: 'rg', args: ['hello'], cwd: '..' },
        expect: /Refused verify cwd:.*outside workspace root/,
      },
      {
        label: 'verify cwd absolute outside',
        verify: { command: 'rg', args: ['hello'], cwd: outsideDir },
        expect: /Refused verify cwd:.*outside workspace root/,
      },
    ]

    for (const c of cases) {
      const res = await client.callTool('apply_edits', {
        cwd: wsDir,
        edits: [{ path: 'file.txt', oldText: 'hello', newText: 'changed' }],
        verify: c.verify,
      })
      assert.equal(res.isError, true, `Expected refusal for ${c.label}, got: ${res.text}`)
      assert.match(res.text, c.expect, `Unexpected refusal text for ${c.label}: ${res.text}`)
      assert.equal(fs.readFileSync(filePath, 'utf8'), 'hello\n', `File should not be written when ${c.label} is refused`)
    }
  } finally {
    fs.rmSync(parentDir, { recursive: true, force: true })
  }
})

test('9. exo-orca-helper CLI auto-injects --base-branch in wave and builds 3-step recover', async () => {
  const wave = await runNode(DIST_ORCA, [
    'wave',
    '--run',
    'run_test',
    '--specs',
    'a.md,b.md',
    '--agent',
    'antigravity',
    '--cwd',
    REPO_ROOT,
    '--dry-run',
  ])
  assert.equal(wave.code, 0, wave.stderr)
  const waveJson = JSON.parse(wave.stdout)
  assert.equal(waveJson.commands.length, 2)
  assert.ok(waveJson.commands[0].args.includes('--base-branch'))
  assert.ok(waveJson.detectedBranch && waveJson.detectedBranch !== 'main')

  const rec = await runNode(DIST_ORCA, [
    'recover',
    '--dispatch',
    'ctx_123',
    '--task',
    'task_456',
    '--run',
    'run_789',
    '--worktree-path',
    REPO_ROOT,
    '--agent',
    'antigravity',
    '--dry-run',
  ])
  assert.equal(rec.code, 0, rec.stderr)
  const recJson = JSON.parse(rec.stdout)
  assert.deepEqual(
    recJson.steps.map((s) => s.step),
    ['worker-abandon', 'task-reopen', 'worker-restart'],
  )
  assert.ok(!JSON.stringify(recJson).includes('--retry-of'))
  assert.ok(!JSON.stringify(recJson).includes('--spec'))
})

test('10. 8.8 KB hard inline byte cap holds under oversized search output', async () => {
  const res = await client.callTool('batch_search', {
    cwd: REPO_ROOT,
    queries: [
      { id: 'big1', type: 'text', query: 'return', path: 'apps/launcher/ExoLauncher/Services', maxResults: 200 },
      { id: 'big2', type: 'text', query: 'export', path: 'apps/launcher/ui/src', maxResults: 200 },
      { id: 'big3', type: 'text', query: 'pub ', path: 'apps/launcher/native-ui/crates', maxResults: 200 },
    ],
  })
  assert.equal(res.isError, false)
  assert.ok(res.jsonBytes <= 8800, `Expected <= 8800 JSON bytes, got ${res.jsonBytes}`)
  assert.ok(res.jsonBytes > 2000, `Expected substantial budgeted content, got ${res.jsonBytes}`)
})

test('11. guarantee check fails on planted shell: true and unrestricted write tool', async () => {
  const tmpDir = createTempDir('exo-kit-guarantee-')
  try {
    const fakeSrc = path.join(tmpDir, 'src')
    const fakeDist = path.join(tmpDir, 'dist')
    fs.mkdirSync(path.join(fakeSrc, 'utils'), { recursive: true })
    fs.mkdirSync(path.join(fakeSrc, 'protocol'), { recursive: true })
    fs.mkdirSync(path.join(fakeSrc, 'tools'), { recursive: true })
    fs.mkdirSync(fakeDist, { recursive: true })

    fs.copyFileSync(path.join(KIT_ROOT, 'src', 'utils', 'budget.ts'), path.join(fakeSrc, 'utils', 'budget.ts'))
    fs.copyFileSync(path.join(KIT_ROOT, 'src', 'utils', 'git.ts'), path.join(fakeSrc, 'utils', 'git.ts'))
    fs.copyFileSync(path.join(KIT_ROOT, 'src', 'utils', 'paths.ts'), path.join(fakeSrc, 'utils', 'paths.ts'))
    fs.copyFileSync(path.join(KIT_ROOT, 'src', 'protocol', 'mcp.ts'), path.join(fakeSrc, 'protocol', 'mcp.ts'))

    fs.writeFileSync(
      path.join(fakeSrc, 'planted.ts'),
      'import { spawn } from "node:child_process"\nspawn("cmd", [], { shell: true })\n',
      'utf8',
    )
    fs.writeFileSync(
      path.join(fakeSrc, 'tools', 'planted-writer.ts'),
      'import fs from "node:fs"\nimport path from "node:path"\nexport function writeAny(p: string) { fs.writeFileSync(path.resolve(p), "x") }\n',
      'utf8',
    )

    const result = verifyGuarantees({ srcDir: fakeSrc, distDir: fakeDist })
    assert.equal(result.ok, false)
    assert.ok(result.violations.some((v) => v.rule === 'no-shell-true'))
    assert.ok(result.violations.some((v) => v.rule === 'confined-write-boundary'))
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true })
  }
})
