import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import worker from '../src/worker.js'
import { materialize } from '../src/github.js'

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

function fakeMirror() {
  const objects = new Map()
  return {
    objects,
    async get(key) {
      const entry = objects.get(key)
      if (!entry) return null
      return {
        body: new ReadableStream({
          start(c) {
            c.enqueue(entry.bytes)
            c.close()
          },
        }),
        size: entry.bytes.byteLength,
        etag: entry.etag,
        httpEtag: entry.etag,
        httpMetadata: entry.httpMetadata ?? {},
        writeHttpMetadata(headers) {
          if (entry.httpMetadata?.contentType) headers.set('content-type', entry.httpMetadata.contentType)
        },
        async arrayBuffer() {
          return entry.bytes.buffer.slice(entry.bytes.byteOffset, entry.bytes.byteOffset + entry.bytes.byteLength)
        },
        async text() {
          return new TextDecoder().decode(entry.bytes)
        },
        async json() {
          return JSON.parse(new TextDecoder().decode(entry.bytes))
        },
      }
    },
    async head(key) {
      const entry = objects.get(key)
      return entry ? { size: entry.bytes.byteLength, etag: entry.etag } : null
    },
    async put(key, value, opts = {}) {
      if (opts.onlyIf?.etagDoesNotMatch === '*' && objects.has(key)) return null
      const bytes = typeof value === 'string'
        ? new TextEncoder().encode(value)
        : value instanceof Uint8Array
          ? value
          : new Uint8Array(await new Response(value).arrayBuffer())
      const etag = `"${sha256(bytes).slice(0, 16)}"`
      objects.set(key, { bytes, etag, httpMetadata: opts.httpMetadata ?? {} })
      return { etag }
    },
    async delete(key) {
      objects.delete(key)
    },
  }
}

test('exo-delta packs and Ed25519 signatures materialize and serve from /latest and /download', async () => {
  const mirror = fakeMirror()
  const setupBytes = new TextEncoder().encode('fake-setup-exe')
  const setupSha = sha256(setupBytes)
  const shaFileBytes = new TextEncoder().encode(`${setupSha}  ExoLauncher-Setup.exe`)
  const notesBytes = new TextEncoder().encode(JSON.stringify({ version: '4.0.0', items: [{ icon: 'Zap', title: 'Delta', detail: 'Fast' }] }))
  const deltaPackBytes = new TextEncoder().encode('EXD1-fake-delta-pack-payload')
  const deltaSigBytes = new TextEncoder().encode('a'.repeat(128))
  const fullPackBytes = new TextEncoder().encode('EXD1-fake-full-pack-payload')
  const fullSigBytes = new Uint8Array(64).fill(0x5b)

  const urls = new Map([
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/ExoLauncher-Setup.exe.sha256', shaFileBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/whats-new.json', notesBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/delta.exopack', deltaPackBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/delta.exopack.sig', deltaSigBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/full.exopack', fullPackBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/full.exopack.sig', fullSigBytes],
    ['https://raw.githubusercontent.com/UhhErix/Exo-Launcher/v4.0.0/apps/launcher/VERSION', new TextEncoder().encode('4.0.0\n')],
  ])

  const prevFetch = globalThis.fetch
  globalThis.fetch = async (url) => {
    const key = typeof url === 'string' ? url : url.url
    const body = urls.get(key)
    if (!body) return new Response('not found', { status: 404 })
    return new Response(body, { status: 200, headers: { 'content-length': String(body.byteLength) } })
  }

  try {
    const release = {
      tag: 'v4.0.0',
      assets: {
        'ExoLauncher-Setup.exe': { size: setupBytes.byteLength, sha256: setupSha, url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/ExoLauncher-Setup.exe' },
        'ExoLauncher-Setup.exe.sha256': { size: shaFileBytes.byteLength, sha256: sha256(shaFileBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/ExoLauncher-Setup.exe.sha256' },
        'whats-new.json': { size: notesBytes.byteLength, sha256: sha256(notesBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/whats-new.json' },
        'delta.exopack': { size: deltaPackBytes.byteLength, sha256: sha256(deltaPackBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/delta.exopack' },
        'delta.exopack.sig': { size: deltaSigBytes.byteLength, sha256: sha256(deltaSigBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/delta.exopack.sig' },
        'full.exopack': { size: fullPackBytes.byteLength, sha256: sha256(fullPackBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/full.exopack' },
        'full.exopack.sig': { size: fullSigBytes.byteLength, sha256: sha256(fullSigBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.0/full.exopack.sig' },
      },
    }

    const { manifest, error } = await materialize(release, { MIRROR: mirror })
    assert.equal(error, undefined)
    assert.equal(manifest.files.deltaPack.name, 'delta.exopack')
    assert.equal(manifest.files.deltaSig.name, 'delta.exopack.sig')
    assert.equal(manifest.files.fullPack.name, 'full.exopack')
    assert.equal(manifest.files.fullSig.name, 'full.exopack.sig')

    await mirror.put('launcher/latest.json', JSON.stringify(manifest))
    await mirror.put('launcher/v4.0.0/delta.exopack', deltaPackBytes)

    const env = { MIRROR: mirror }
    const ctx = { waitUntil() {} }
    const latestRes = await worker.fetch(new Request('https://updates.getexo.app/v1/launcher/latest'), env, ctx)
    assert.equal(latestRes.status, 200)
    const latestJson = await latestRes.json()
    assert.equal(latestJson.files.deltaPack, 'https://updates.getexo.app/v1/launcher/download/v4.0.0/delta.exopack')
    assert.equal(latestJson.files.deltaSig, 'https://updates.getexo.app/v1/launcher/download/v4.0.0/delta.exopack.sig')
    assert.equal(latestJson.files.fullPack, 'https://updates.getexo.app/v1/launcher/download/v4.0.0/full.exopack')
    assert.equal(latestJson.files.fullSig, 'https://updates.getexo.app/v1/launcher/download/v4.0.0/full.exopack.sig')

    const dlPack = await worker.fetch(new Request('https://updates.getexo.app/v1/launcher/download/v4.0.0/delta.exopack'), env, ctx)
    assert.equal(dlPack.status, 200)
    assert.equal(await dlPack.text(), 'EXD1-fake-delta-pack-payload')

    const dlSig = await worker.fetch(new Request('https://updates.getexo.app/v1/launcher/download/v4.0.0/delta.exopack.sig'), env, ctx)
    assert.equal(dlSig.status, 200)
    assert.equal(await dlSig.text(), 'a'.repeat(128))
  } finally {
    globalThis.fetch = prevFetch
  }
})

test('rejects malformed Ed25519 signature for delta.exopack', async () => {
  const mirror = fakeMirror()
  const setupBytes = new TextEncoder().encode('fake-setup-exe')
  const setupSha = sha256(setupBytes)
  const shaFileBytes = new TextEncoder().encode(`${setupSha}  ExoLauncher-Setup.exe`)
  const notesBytes = new TextEncoder().encode(JSON.stringify({ version: '4.0.1', items: [{ icon: 'Zap', title: 'Bad', detail: 'Sig' }] }))
  const deltaPackBytes = new TextEncoder().encode('EXD1-fake')
  const badSigBytes = new TextEncoder().encode('not-a-valid-ed25519-sig')

  const urls = new Map([
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/ExoLauncher-Setup.exe.sha256', shaFileBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/whats-new.json', notesBytes],
    ['https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/delta.exopack.sig', badSigBytes],
    ['https://raw.githubusercontent.com/UhhErix/Exo-Launcher/v4.0.1/apps/launcher/VERSION', new TextEncoder().encode('4.0.1\n')],
  ])

  const prevFetch = globalThis.fetch
  globalThis.fetch = async (url) => {
    const key = typeof url === 'string' ? url : url.url
    const body = urls.get(key)
    if (!body) return new Response('not found', { status: 404 })
    return new Response(body, { status: 200, headers: { 'content-length': String(body.byteLength) } })
  }

  try {
    const release = {
      tag: 'v4.0.1',
      assets: {
        'ExoLauncher-Setup.exe': { size: setupBytes.byteLength, sha256: setupSha, url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/ExoLauncher-Setup.exe' },
        'ExoLauncher-Setup.exe.sha256': { size: shaFileBytes.byteLength, sha256: sha256(shaFileBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/ExoLauncher-Setup.exe.sha256' },
        'whats-new.json': { size: notesBytes.byteLength, sha256: sha256(notesBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/whats-new.json' },
        'delta.exopack': { size: deltaPackBytes.byteLength, sha256: sha256(deltaPackBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/delta.exopack' },
        'delta.exopack.sig': { size: badSigBytes.byteLength, sha256: sha256(badSigBytes), url: 'https://github.com/UhhErix/Exo-Launcher/releases/download/v4.0.1/delta.exopack.sig' },
      },
    }
    const result = await materialize(release, { MIRROR: mirror })
    assert.match(result.error ?? '', /not a valid Ed25519 signature/)
  } finally {
    globalThis.fetch = prevFetch
  }
})
