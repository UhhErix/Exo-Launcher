import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  MAX_SNAPSHOT_BYTES,
  truncateToJsonBytes,
} from '../utils/budget.js'
import { runReadOnlyGit } from '../utils/git.js'
import { resolveWorkspacePaths, toPosixPath } from '../utils/paths.js'

const SNAPSHOT_GIT_TIMEOUT_MS = 1200

const AREA_RULES: { area: string; match: (rel: string) => boolean; skills: string[] }[] = [
  {
    area: 'native-ui',
    match: (r) => r.includes('native-ui/'),
    skills: ['native-ui'],
  },
  {
    area: 'ui',
    match: (r) => r.includes('apps/launcher/ui/') || r.startsWith('ui/'),
    skills: ['exo-design', 'frontend-design', 'impeccable'],
  },
  {
    area: 'mouse',
    match: (r) => r.includes('apps/launcher/mouse/') || r.startsWith('mouse/'),
    skills: ['exo-design'],
  },
  {
    area: 'ExoLauncher',
    match: (r) => r.includes('ExoLauncher/'),
    skills: ['apps/launcher/AGENTS.md'],
  },
  {
    area: 'shaders',
    match: (r) => r.includes('shaders/'),
    skills: ['apps/launcher/AGENTS.md'],
  },
  {
    area: 'tools',
    match: (r) => r.includes('apps/launcher/tools/') || r.startsWith('tools/'),
    skills: ['delete-comment'],
  },
]

export async function buildExoSnapshot(
  inputCwd?: string,
  maxBytes = MAX_SNAPSHOT_BYTES,
  gitTimeoutMs = SNAPSHOT_GIT_TIMEOUT_MS,
): Promise<string> {
  const budget = Math.max(256, Math.floor(maxBytes))
  const ws = resolveWorkspacePaths(inputCwd)

  const [branchRes, statusRes, logRes] = await Promise.all([
    runReadOnlyGit(ws.repoRoot, 'rev-parse', ['--abbrev-ref', 'HEAD'], gitTimeoutMs),
    runReadOnlyGit(ws.repoRoot, 'status', ['--porcelain=v1', '-uall'], gitTimeoutMs),
    runReadOnlyGit(ws.repoRoot, 'log', ['-n', '3', '--oneline'], gitTimeoutMs),
  ])

  const branch = branchRes.exitCode === 0 ? branchRes.stdout.trim() : '(detached/unknown)'
  const dirtyLines =
    statusRes.exitCode === 0
      ? statusRes.stdout
          .split(/\r?\n/)
          .map((l) => l.trimEnd())
          .filter(Boolean)
      : []

  const dirtyPaths = dirtyLines.map((line) => {
    let entry = line.slice(3).trim()
    const arrow = entry.indexOf(' -> ')
    if (arrow >= 0) entry = entry.slice(arrow + 4).trim()
    return entry.replace(/^"|"$/g, '')
  })

  const activeAreas = new Set<string>()
  const skills = new Set<string>(['apps/launcher/AGENTS.md'])
  for (const p of dirtyPaths) {
    for (const rule of AREA_RULES) {
      if (rule.match(p)) {
        activeAreas.add(rule.area)
        for (const s of rule.skills) skills.add(s)
      }
    }
  }

  if (ws.isExoLauncher && ws.launcherRoot) {
    const presentAreas = ['native-ui', 'ui', 'ExoLauncher', 'mouse', 'shaders'].filter((a) =>
      fs.existsSync(path.join(ws.launcherRoot!, a)),
    )
    if (activeAreas.size === 0) {
      for (const a of presentAreas) activeAreas.add(a)
      skills.add('native-ui')
      skills.add('exo-design')
      skills.add('delete-comment')
      skills.add('orchestration')
    } else {
      skills.add('delete-comment')
      skills.add('orchestration')
    }
  }

  const lines: string[] = []
  lines.push(
    `[exo-snapshot] repo=${toPosixPath(ws.repoRoot)} branch=${branch}${ws.isExoLauncher ? ' (Exo Launcher)' : ''}`,
  )

  if (branch && branch !== 'main' && branch !== '(detached/unknown)') {
    lines.push(`Orca guard: NOT on main; pass --base-branch ${branch} when creating child worktrees.`)
  }

  if (dirtyLines.length === 0) {
    lines.push('Dirty: clean')
  } else {
    const shown = dirtyLines.slice(0, 8).join('; ')
    const extra = dirtyLines.length > 8 ? ` (+${dirtyLines.length - 8} more)` : ''
    lines.push(`Dirty (${dirtyLines.length}): ${shown}${extra}`)
  }

  if (logRes.exitCode === 0 && logRes.stdout.trim()) {
    const commits = logRes.stdout
      .trim()
      .split(/\r?\n/)
      .slice(0, 3)
      .join(' | ')
    lines.push(`Commits: ${commits}`)
  }

  if (activeAreas.size > 0) {
    lines.push(`Areas: ${Array.from(activeAreas).join(', ')}`)
  }
  if (ws.isExoLauncher) {
    lines.push(`Rules/Skills: ${Array.from(skills).join(', ')}`)
    lines.push(
      'Kit tools: exo_contract (RPC), exo_rune_api (Rune), exo_run_gates (verify), batch_search, quick_outline, exo_parity_snap, apply_edits.',
    )
  }

  return truncateToJsonBytes(lines.join('\n'), budget)
}

export async function runExoSnapshot(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : MAX_SNAPSHOT_BYTES,
    DEFAULT_INLINE_MAX_BYTES,
  )
  const cwd = typeof args.cwd === 'string' ? args.cwd : undefined
  const text = await buildExoSnapshot(cwd, maxBytes, 3000)
  return { text }
}
