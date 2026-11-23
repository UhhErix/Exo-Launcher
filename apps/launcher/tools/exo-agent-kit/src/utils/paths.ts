import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export interface WorkspacePaths {
  cwd: string
  repoRoot: string
  launcherRoot: string | null
  isExoLauncher: boolean
}

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

export function resolveWorkspacePaths(inputCwd?: string): WorkspacePaths {
  const startDir = path.resolve(inputCwd && inputCwd.trim() ? inputCwd : process.cwd())
  let cur = startDir
  let gitRoot: string | null = null
  let launcherRoot: string | null = null

  for (let depth = 0; depth < 16; depth++) {
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

export function resolveSafePath(baseDir: string, target: string): string {
  return path.isAbsolute(target) ? path.resolve(target) : path.resolve(baseDir, target)
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
