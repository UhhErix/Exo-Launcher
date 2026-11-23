import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export interface WorkspacePaths {
  cwd: string
  repoRoot: string
  launcherRoot: string | null
  isExoLauncher: boolean
}

export type ConfinedPathResult =
  | { ok: true; realPath: string; relPath: string }
  | { ok: false; error: string }

export function toPosixPath(p: string): string {
  return p.replace(/\\/g, '/')
}

export function resolveHomeDir(): string {
  return os.homedir()
}

export function resolveKitHomeDir(): string {
  if (process.env.EXO_KIT_HOME && process.env.EXO_KIT_HOME.trim()) {
    return path.resolve(process.env.EXO_KIT_HOME.trim())
  }
  return path.join(resolveHomeDir(), '.exo-agent-kit')
}

export function resolveCacheDir(): string {
  if (process.env.EXO_KIT_CACHE_DIR && process.env.EXO_KIT_CACHE_DIR.trim()) {
    return path.resolve(process.env.EXO_KIT_CACHE_DIR.trim())
  }
  const kitHome = resolveKitHomeDir()
  if (fs.existsSync(kitHome)) {
    return path.join(kitHome, 'cache')
  }
  return path.join(os.tmpdir(), 'exo-agent-kit-cache')
}

export function resolveCargoHome(): string {
  if (process.env.CARGO_HOME && process.env.CARGO_HOME.trim()) {
    return path.resolve(process.env.CARGO_HOME.trim())
  }
  return path.join(resolveHomeDir(), '.cargo')
}

export function resolveLocalAppData(): string {
  if (process.env.LOCALAPPDATA && process.env.LOCALAPPDATA.trim()) {
    return path.resolve(process.env.LOCALAPPDATA.trim())
  }
  return path.join(resolveHomeDir(), 'AppData', 'Local')
}

export function resolveAppData(): string {
  if (process.env.APPDATA && process.env.APPDATA.trim()) {
    return path.resolve(process.env.APPDATA.trim())
  }
  return path.join(resolveHomeDir(), 'AppData', 'Roaming')
}

function isDisallowedRepoRoot(dirPath: string): boolean {
  const resolved = path.resolve(dirPath)
  if (path.dirname(resolved) === resolved) return true
  const norm = process.platform === 'win32' ? resolved.toLowerCase() : resolved
  const home = path.resolve(resolveHomeDir())
  const homeNorm = process.platform === 'win32' ? home.toLowerCase() : home
  const homeParent = path.dirname(homeNorm)
  return norm === homeNorm || norm === homeParent
}

export function resolveWorkspacePaths(inputCwd?: string): WorkspacePaths {
  const startDir = path.resolve(inputCwd && inputCwd.trim() ? inputCwd : process.cwd())
  let cur = startDir
  let gitRoot: string | null = null
  let launcherRoot: string | null = null

  for (let depth = 0; depth < 16; depth++) {
    if (isDisallowedRepoRoot(cur)) break
    const candidateLauncher = path.join(cur, 'apps', 'launcher')
    if (
      !launcherRoot &&
      fs.existsSync(path.join(candidateLauncher, 'ui', 'contracts', 'host-v1.json'))
    ) {
      launcherRoot = candidateLauncher
    }
    if (
      !launcherRoot &&
      fs.existsSync(path.join(cur, 'ui', 'contracts', 'host-v1.json'))
    ) {
      launcherRoot = cur
    }
    if (!gitRoot && fs.existsSync(path.join(cur, '.git'))) {
      gitRoot = cur
    }
    if (gitRoot && launcherRoot) break
    const parent = path.dirname(cur)
    if (parent === cur) break
    cur = parent
  }

  const repoRoot = gitRoot ?? (launcherRoot ? path.resolve(launcherRoot, '..', '..') : startDir)
  return {
    cwd: startDir,
    repoRoot,
    launcherRoot,
    isExoLauncher: launcherRoot !== null,
  }
}

export function resolveUnconfinedPath(baseDir: string, target: string): string {
  return path.isAbsolute(target) ? path.resolve(target) : path.resolve(baseDir, target)
}

function canonicalizeWithMissingTail(absPath: string, depth = 0): string {
  if (depth > 16) return path.resolve(absPath)
  try {
    return fs.realpathSync(absPath)
  } catch {}

  try {
    const lst = fs.lstatSync(absPath)
    if (lst.isSymbolicLink()) {
      const linkTarget = fs.readlinkSync(absPath)
      const resolvedLink = path.isAbsolute(linkTarget)
        ? path.resolve(linkTarget)
        : path.resolve(path.dirname(absPath), linkTarget)
      return canonicalizeWithMissingTail(resolvedLink, depth + 1)
    }
  } catch {}

  const tail: string[] = []
  let cur = path.resolve(absPath)
  while (true) {
    const parent = path.dirname(cur)
    if (parent === cur) return path.resolve(absPath)
    tail.unshift(path.basename(cur))
    cur = parent
    try {
      const realParent = fs.realpathSync(cur)
      return path.join(realParent, ...tail)
    } catch {}
  }
}

function isPathInsideRoot(realRoot: string, realCandidate: string): boolean {
  const normRoot =
    process.platform === 'win32'
      ? realRoot.toLowerCase().replace(/[\\\/]+$/, '')
      : realRoot.replace(/\/+$/, '')
  const normCand =
    process.platform === 'win32'
      ? realCandidate.toLowerCase().replace(/[\\\/]+$/, '')
      : realCandidate.replace(/\/+$/, '')

  if (normCand === normRoot) return true
  if (!normCand.startsWith(normRoot + path.sep)) return false

  const rel = path.relative(realRoot, realCandidate)
  if (!rel) return true
  if (rel === '..' || rel.startsWith(`..${path.sep}`) || path.isAbsolute(rel)) return false
  return true
}

function containsGitSegment(relPath: string): boolean {
  const segments = relPath.split(/[\\\/]/).filter(Boolean)
  return segments.some((seg) => seg.toLowerCase() === '.git')
}

export function resolveConfinedPath(
  rootDir: string,
  baseDir: string,
  target: string,
  options: { allowRoot?: boolean } = {},
): ConfinedPathResult {
  if (!target || !target.trim()) {
    return { ok: false, error: 'Refused: empty target path.' }
  }

  let realRoot: string
  try {
    realRoot = fs.realpathSync(path.resolve(rootDir))
  } catch {
    realRoot = path.resolve(rootDir)
  }

  const candidate = path.isAbsolute(target) ? path.resolve(target) : path.resolve(baseDir, target)
  const realPath = canonicalizeWithMissingTail(candidate)

  if (!isPathInsideRoot(realRoot, realPath)) {
    return {
      ok: false,
      error: `Refused: path "${target}" resolves outside workspace root (${toPosixPath(realRoot)}).`,
    }
  }

  const relPath = toPosixPath(path.relative(realRoot, realPath))
  if (!options.allowRoot && relPath === '') {
    return {
      ok: false,
      error: `Refused: cannot write directly to workspace root (${toPosixPath(realRoot)}).`,
    }
  }

  if (containsGitSegment(relPath)) {
    return {
      ok: false,
      error: `Refused: path "${target}" is inside .git/ (${relPath}).`,
    }
  }

  return { ok: true, realPath, relPath }
}

export function resolveConfinedWritePath(
  rootDir: string,
  baseDir: string,
  target: string,
): ConfinedPathResult {
  return resolveConfinedPath(rootDir, baseDir, target, { allowRoot: false })
}

export function resolveConfinedDir(
  rootDir: string,
  baseDir: string,
  target: string,
): ConfinedPathResult {
  return resolveConfinedPath(rootDir, baseDir, target, { allowRoot: true })
}

export function parseMaybeJsonArray<T>(value: unknown): T[] | null {
  if (Array.isArray(value)) return value as T[]
  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed.startsWith('[')) return null
    try {
      const parsed: unknown = JSON.parse(trimmed)
      return Array.isArray(parsed) ? (parsed as T[]) : null
    } catch {
      return null
    }
  }
  return null
}
