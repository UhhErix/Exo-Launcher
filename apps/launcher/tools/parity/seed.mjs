// Copies an owner's data folder into a test data dir, so the parity run starts signed in.
// The source is only read. The app folder and its update staging folders are skipped:
// the test instance must never run from or touch the installed app.
import { cp, stat } from 'node:fs/promises'
import { basename, resolve } from 'node:path'

const SKIPPED = /^app(\.incoming-dev-.*|\.previous-dev-.*)?$/i

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
  return true
}
