import { cp, mkdir, stat } from 'node:fs/promises'
import { basename, join, resolve } from 'node:path'

const SKIPPED = /^app(\.incoming-dev-.*|\.previous-dev-.*)?$/i
const WEBVIEW_CACHE_SKIP = /(^|[\\/])(Cache|Code Cache|GPUCache|GrShaderCache|ShaderCache|DawnGraphiteCache|DawnWebGPUCache|CacheStorage)([\\/]|$)/i

export async function seedEmbeddedWebViewProfile(dataDir) {
  const localApp = process.env.LOCALAPPDATA || ''
  if (!localApp) return false
  const sharedEb = join(localApp, 'Exo', 'WebView2', 'EBWebView')
  const embeddedSrc = join(sharedEb, 'WV2Profile_embedded')
  const info = await stat(embeddedSrc).catch(() => null)
  if (!info?.isDirectory()) return false

  const localStateSrc = join(sharedEb, 'Local State')
  const localStateInfo = await stat(localStateSrc).catch(() => null)

  for (const profileFolder of ['webview-debug', 'webview']) {
    const destEb = join(dataDir, profileFolder, 'EBWebView')
    const destEmbedded = join(destEb, 'WV2Profile_embedded')
    const existing = await stat(destEmbedded).catch(() => null)
    if (existing?.isDirectory()) continue
    await mkdir(destEb, { recursive: true })
    if (localStateInfo?.isFile()) {
      await cp(localStateSrc, join(destEb, 'Local State'), { force: true }).catch(() => {})
    }
    await cp(embeddedSrc, destEmbedded, {
      recursive: true,
      force: true,
      filter: (p) => !WEBVIEW_CACHE_SKIP.test(p),
    }).catch(() => {})
  }
  return true
}

export async function seedDataFrom(seedFrom, dataDir) {
  if (!seedFrom) return false
  const src = resolve(seedFrom)
  const info = await stat(src).catch(() => null)
  if (!info?.isDirectory()) throw new Error(`--seed-from is not a folder: ${src}`)
  await cp(src, dataDir, {
    recursive: true,
    force: true,
    filter: (path) => {
      const rel = resolve(path).slice(src.length)
      const top = rel.split(/[\\/]/).filter(Boolean)[0]
      return !(top && SKIPPED.test(top) && basename(path) === top)
    },
  })
  await seedEmbeddedWebViewProfile(dataDir)
  return true
}
