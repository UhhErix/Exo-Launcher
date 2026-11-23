import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const KIT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const DIST_DIR = path.join(KIT_ROOT, 'dist')

const BUNDLES = ['mcp-server.mjs', 'session-start.mjs', 'exo-orca-helper.mjs']

function toForwardSlash(p) {
  return p.replace(/\\/g, '/')
}

function discoverOrcaClaudeProfiles(appDataDir) {
  const root = path.join(appDataDir, 'orca', 'claude-profiles')
  if (!fs.existsSync(root)) return []
  try {
    return fs
      .readdirSync(root, { withFileTypes: true })
      .filter((d) => d.isDirectory())
      .map((d) => path.join(root, d.name, 'home', '.claude.json'))
      .sort()
  } catch {
    return []
  }
}

function buildConfigSnippets(homeDir, appDataDir) {
  const installRoot = path.join(homeDir, '.exo-agent-kit')
  const installRootFwd = toForwardSlash(installRoot)
  const mcpServerFwd = `${installRootFwd}/dist/mcp-server.mjs`
  const sessionStartFwd = `${installRootFwd}/dist/session-start.mjs`
  const refsFwd = `${installRootFwd}/refs`

  const claudeMcpEntry = {
    'exo-kit': {
      type: 'stdio',
      command: 'node',
      args: [mcpServerFwd],
      env: {
        EXO_PARITY_REFS: refsFwd,
        EXO_KIT_NO_NETWORK: '1',
      },
    },
  }

  const claudeSessionStartEntry = {
    hooks: [
      {
        type: 'command',
        command: `node ${sessionStartFwd} --agent claude`,
        timeout: 5,
      },
    ],
  }

  const antigravityPluginDir = path.join(homeDir, '.gemini', 'config', 'plugins', 'exo-agent-kit')
  const antigravityPluginJson = {
    name: 'exo-agent-kit',
    version: '1.0.0',
    description: 'Pinned offline speed & Exo Launcher domain toolkit',
  }
  const antigravityMcpConfig = {
    mcpServers: {
      'exo-kit': {
        command: 'node',
        args: [mcpServerFwd],
        env: {
          EXO_PARITY_REFS: refsFwd,
          EXO_KIT_NO_NETWORK: '1',
        },
        tools: {
          exo_run_gates: { eager: true },
          batch_search: { eager: true },
          exo_contract: { eager: true },
          exo_rune_api: { eager: true },
          quick_outline: { eager: true },
          exo_snapshot: { eager: true },
          exo_parity_snap: { eager: true },
          apply_edits: { eager: true },
        },
      },
    },
  }
  const antigravityHooksJson = {
    SessionStart: [
      {
        hooks: [
          {
            type: 'command',
            command: `node ${sessionStartFwd} --agent antigravity`,
            timeout: 5,
          },
        ],
      },
    ],
  }
  const antigravityRulesMd = [
    '# exo-agent-kit Routing Rules',
    '',
    '- Use `exo_contract` before editing bridge or RPC code (`host-v1.json`, `host.ts`, `WebHostBridge*.cs`, `exo-rpc`).',
    '- Use `exo_rune_api` before claiming any Rune widget, style, or token capability or absence.',
    '- Use `exo_run_gates` to run mapped verification gates concurrently with folded <=8.8 KB output.',
    '- Use `batch_search` for multi-query search/grep/read-only-git exploration.',
    '- Use `quick_outline` on unfamiliar files >200 lines before ranged `read_file` calls.',
    '- Use `apply_edits` for >=2 files or multi-hunk edits.',
    '- Use `exo_parity_snap` for native parity captures/diffs; do not inspect parity PNGs inside >150k-token sessions.',
  ].join('\n')

  return {
    installRoot,
    claudeJsonPath: path.join(homeDir, '.claude.json'),
    claudeSettingsPath: path.join(homeDir, '.claude', 'settings.json'),
    orcaClaudeJsonPaths: discoverOrcaClaudeProfiles(appDataDir),
    claudeMcpEntry,
    claudeSessionStartEntry,
    antigravityPluginDir,
    antigravityPluginJson,
    antigravityMcpConfig,
    antigravityHooksJson,
    antigravityRulesMd,
  }
}

function main() {
  const argv = process.argv.slice(2)
  const apply = argv.includes('--apply')
  const dryRun = !apply

  const missing = BUNDLES.filter((f) => !fs.existsSync(path.join(DIST_DIR, f)))
  if (missing.length > 0) {
    console.error(`Missing built bundle(s) in ${DIST_DIR}: ${missing.join(', ')}. Run "npm run build" first.`)
    process.exit(1)
  }

  const homeDir = os.homedir()
  const appDataDir = process.env.APPDATA || path.join(homeDir, 'AppData', 'Roaming')
  const cfg = buildConfigSnippets(homeDir, appDataDir)

  const targetDistDir = path.join(cfg.installRoot, 'dist')
  const targetCacheDir = path.join(cfg.installRoot, 'cache')
  const srcVersionPath = path.join(DIST_DIR, 'VERSION')
  const dstVersionPath = path.join(cfg.installRoot, 'VERSION')

  console.log(`=== exo-agent-kit install (${dryRun ? 'DRY RUN — no files written' : 'APPLY — copying bundles only'}) ===`)
  console.log(`Target runtime dir: ${cfg.installRoot}`)

  if (apply) {
    fs.mkdirSync(targetDistDir, { recursive: true })
    fs.mkdirSync(targetCacheDir, { recursive: true })
    for (const bundle of BUNDLES) {
      const src = path.join(DIST_DIR, bundle)
      const dst = path.join(targetDistDir, bundle)
      fs.copyFileSync(src, dst)
      console.log(`  [COPIED] ${dst}`)
    }
    if (fs.existsSync(srcVersionPath)) {
      fs.copyFileSync(srcVersionPath, dstVersionPath)
      console.log(`  [COPIED] ${dstVersionPath}`)
    }
  } else {
    for (const bundle of BUNDLES) {
      console.log(`  [WOULD COPY] ${path.join(DIST_DIR, bundle)} -> ${path.join(targetDistDir, bundle)}`)
    }
    if (fs.existsSync(srcVersionPath)) {
      console.log(`  [WOULD COPY] ${srcVersionPath} -> ${dstVersionPath}`)
    }
    console.log(`  [WOULD MKDIR] ${targetCacheDir}`)
    console.log('Pass --apply to write the bundles to %USERPROFILE%\\.exo-agent-kit\\.')
  }

  console.log('')
  console.log('=== Manual Config Edits (NEVER auto-applied by this script) ===')

  console.log('')
  console.log('1. Claude Code MCP registration — merge into "mcpServers" in BOTH:')
  console.log(`   - ${cfg.claudeJsonPath}`)
  if (cfg.orcaClaudeJsonPaths.length > 0) {
    for (const p of cfg.orcaClaudeJsonPaths) {
      console.log(`   - ${p}`)
    }
  } else {
    console.log(`   - ${path.join(appDataDir, 'orca', 'claude-profiles', '<profile-id>', 'home', '.claude.json')}`)
  }
  console.log(JSON.stringify(cfg.claudeMcpEntry, null, 2))

  console.log('')
  console.log(`2. Claude Code SessionStart hook — append to "hooks.SessionStart" ONLY in:`)
  console.log(`   - ${cfg.claudeSettingsPath}`)
  console.log('   (Orca settings-sync.json syncs settings.json into managed profiles automatically; keep existing orca-agent-hook.exe entries and do NOT add PreToolUse/PostToolUse hooks.)')
  console.log(JSON.stringify(cfg.claudeSessionStartEntry, null, 2))

  console.log('')
  console.log(`3. Google Antigravity pinned plugin — create under ${cfg.antigravityPluginDir}:`)
  console.log('')
  console.log(`   3a. ${path.join(cfg.antigravityPluginDir, 'plugin.json')}`)
  console.log(JSON.stringify(cfg.antigravityPluginJson, null, 2))
  console.log('')
  console.log(`   3b. ${path.join(cfg.antigravityPluginDir, 'mcp_config.json')}`)
  console.log(JSON.stringify(cfg.antigravityMcpConfig, null, 2))
  console.log('')
  console.log(`   3c. ${path.join(cfg.antigravityPluginDir, 'hooks.json')}`)
  console.log(JSON.stringify(cfg.antigravityHooksJson, null, 2))
  console.log('')
  console.log(`   3d. ${path.join(cfg.antigravityPluginDir, 'rules', 'exo-kit.md')}`)
  console.log(cfg.antigravityRulesMd)
}

main()
