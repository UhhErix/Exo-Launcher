import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const KIT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const REPO_ROOT = path.resolve(KIT_ROOT, '..', '..', '..', '..')
const DIST_MCP = path.join(KIT_ROOT, 'dist', 'mcp-server.mjs')

async function main() {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'exo-kit-smoke-'))
  const workParityDir = path.join(
    REPO_ROOT,
    'apps',
    'launcher',
    'work',
    `exo-kit-smoke-${crypto.randomBytes(4).toString('hex')}`,
  )
  fs.mkdirSync(workParityDir, { recursive: true })
  const cacheDir = path.join(tempRoot, 'cache')
  const editFile = path.join(tempRoot, 'smoke.txt')
  fs.writeFileSync(editFile, 'hello world\n', 'utf8')

  const refPng = path.join(workParityDir, 'ref.png')
  const snapPng = path.join(workParityDir, 'snap.png')
  const diffPng = path.join(workParityDir, 'diff.png')
  await new Promise((resolve, reject) => {
    const p = spawn(
      'python',
      [
        '-I',
        '-c',
        'import sys; from PIL import Image; Image.new("RGB",(64,48),(0,0,0)).save(sys.argv[1]); Image.new("RGB",(64,48),(12,0,0)).save(sys.argv[2])',
        refPng,
        snapPng,
      ],
      { shell: false, windowsHide: true },
    )
    p.on('error', reject)
    p.on('close', (code) => (code === 0 ? resolve() : reject(new Error(`python exit ${code}`))))
  })

  const child = spawn(process.execPath, [DIST_MCP], {
    cwd: REPO_ROOT,
    env: { ...process.env, EXO_KIT_CACHE_DIR: cacheDir },
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
  })

  let nextId = 1
  const pending = new Map()
  let buf = ''

  child.stdout.on('data', (chunk) => {
    buf += chunk.toString('utf8')
    let nl
    while ((nl = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, nl).trim()
      buf = buf.slice(nl + 1)
      if (!line) continue
      const msg = JSON.parse(line)
      const entry = pending.get(msg.id)
      if (entry) {
        pending.delete(msg.id)
        entry({ msg, rawBytes: Buffer.byteLength(line, 'utf8') })
      }
    }
  })

  const sendReq = (method, params = {}) => {
    const id = nextId++
    const started = performance.now()
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(id)
        reject(new Error(`Timeout on ${method}`))
      }, 60_000)
      pending.set(id, ({ msg, rawBytes }) => {
        clearTimeout(timer)
        resolve({ msg, rawBytes, ms: Number((performance.now() - started).toFixed(1)) })
      })
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
    })
  }

  try {
    const records = []

    const init = await sendReq('initialize', {
      protocolVersion: '2025-03-26',
      capabilities: {},
      clientInfo: { name: 'mcp-smoke', version: '1.0.0' },
    })
    assert.equal(init.msg.result.serverInfo.name, 'exo-agent-kit')
    records.push({ step: 'initialize', ms: init.ms, responseBytes: init.rawBytes, textJsonBytes: null, ok: true })

    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n')

    const list = await sendReq('tools/list')
    assert.equal(list.msg.result.tools.length, 8)
    records.push({ step: 'tools/list', ms: list.ms, responseBytes: list.rawBytes, textJsonBytes: null, ok: true })

    const calls = [
      {
        name: 'exo_run_gates',
        args: { cwd: REPO_ROOT, gates: ['parity-test'] },
      },
      {
        name: 'batch_search',
        args: {
          cwd: REPO_ROOT,
          queries: [
            { id: 'q1', type: 'text', query: 'METHOD_GAME_GET', path: 'apps/launcher/native-ui/crates/exo-rpc/src' },
            { id: 'q2', type: 'git', query: 'log -n 2 --oneline' },
          ],
        },
      },
      {
        name: 'exo_contract',
        args: { cwd: REPO_ROOT, method: 'game.get' },
      },
      {
        name: 'exo_rune_api',
        args: { cwd: REPO_ROOT, query: 'tracking', kind: 'fn' },
      },
      {
        name: 'quick_outline',
        args: {
          cwd: REPO_ROOT,
          paths: ['apps/launcher/native-ui/crates/exo-rpc/src/lib.rs'],
          maxSymbolsPerFile: 25,
        },
      },
      {
        name: 'exo_snapshot',
        args: { cwd: REPO_ROOT },
      },
      {
        name: 'exo_parity_snap',
        args: {
          cwd: REPO_ROOT,
          compare: { refPath: refPng, snapPath: snapPng, outPath: diffPng },
        },
      },
      {
        name: 'apply_edits',
        args: {
          cwd: tempRoot,
          edits: [{ path: 'smoke.txt', oldText: 'hello world', newText: 'hello exo-kit' }],
        },
      },
    ]

    for (const c of calls) {
      const r = await sendReq('tools/call', { name: c.name, arguments: c.args })
      assert.equal(r.msg.error, undefined, `RPC error on ${c.name}: ${JSON.stringify(r.msg.error)}`)
      const text = r.msg.result.content[0].text
      const textJsonBytes = Buffer.byteLength(JSON.stringify(text), 'utf8')
      const isError = r.msg.result.isError === true
      assert.equal(isError, false, `Tool error on ${c.name}: ${text}`)
      assert.ok(textJsonBytes <= 8800, `${c.name} exceeded 8800 bytes: ${textJsonBytes}`)
      records.push({
        step: `tools/call:${c.name}`,
        ms: r.ms,
        responseBytes: r.rawBytes,
        textJsonBytes,
        ok: !isError,
      })
    }

    console.log(JSON.stringify({ ok: true, records }, null, 2))
  } finally {
    child.stdin.end()
    await new Promise((resolve) => {
      child.once('close', resolve)
      setTimeout(() => {
        try {
          child.kill()
        } catch {}
        resolve()
      }, 2000)
    })
    fs.rmSync(tempRoot, { recursive: true, force: true })
    fs.rmSync(workParityDir, { recursive: true, force: true })
  }
}

main().catch((err) => {
  console.error(err)
  process.exit(1)
})
