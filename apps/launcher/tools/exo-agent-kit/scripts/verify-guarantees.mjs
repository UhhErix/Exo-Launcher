import fs from 'node:fs'
import path from 'node:path'

const BANNED_NET_MODULES = [
  'http',
  'https',
  'http2',
  'net',
  'tls',
  'dgram',
  'dns',
  'undici',
  'node:http',
  'node:https',
  'node:http2',
  'node:net',
  'node:tls',
  'node:dgram',
  'node:dns',
]

const NET_IMPORT_RX = new RegExp(
  `(?:\\bfrom\\s*|\\bimport\\s*\\(\\s*|\\brequire\\s*\\(\\s*)['"](?:${BANNED_NET_MODULES.map((m) => m.replace(':', '\\:')).join('|')})['"]`,
  'g',
)

const BANNED_PATTERNS = [
  {
    id: 'no-network-import',
    rx: NET_IMPORT_RX,
    message: 'Banned network module import detected',
  },
  {
    id: 'no-network-api',
    rx: /\b(?:globalThis\.)?(?:fetch\s*\(|WebSocket\b|XMLHttpRequest\b|EventSource\b)/g,
    message: 'Banned runtime network API detected',
  },
  {
    id: 'no-shell-true',
    rx: /\bshell\s*:\s*(?:true|!0|['"`][^'"`]+['"`])/g,
    message: 'Forbidden shell: true (or shell path) detected',
  },
  {
    id: 'no-child-exec',
    rx: /\b(?:execSync|exec)\s*\(\s*['"`]/g,
    message: 'Forbidden shell-based child_process.exec/execSync detected',
  },
  {
    id: 'no-hardcoded-user-path',
    rx: /[A-Za-z]:[\\\/]Users[\\\/][A-Za-z0-9._-]+/g,
    message: 'Hardcoded Windows user profile path detected',
  },
  {
    id: 'no-cwd-plugin-lookup',
    rx: /(?:\.\/plugin\.json|\.agents[\\\/]plugins)/g,
    message: 'Forbidden CWD workspace plugin lookup detected',
  },
]

function listFiles(dir, exts) {
  const out = []
  if (!fs.existsSync(dir)) return out
  const walk = (cur) => {
    for (const entry of fs.readdirSync(cur, { withFileTypes: true })) {
      const full = path.join(cur, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.isFile() && exts.some((e) => entry.name.endsWith(e))) out.push(full)
    }
  }
  walk(dir)
  return out.sort()
}

export function scanFileContent(relPath, content) {
  const violations = []
  for (const rule of BANNED_PATTERNS) {
    rule.rx.lastIndex = 0
    const match = rule.rx.exec(content)
    if (match) {
      const line = content.slice(0, match.index).split('\n').length
      violations.push({
        rule: rule.id,
        file: relPath,
        line,
        message: `${rule.message}: ${match[0]}`,
      })
    }
  }
  return violations
}

export function verifyGuarantees({ srcDir, distDir }) {
  const violations = []
  const srcFiles = listFiles(srcDir, ['.ts', '.mts', '.js', '.mjs'])
  const distFiles = listFiles(distDir, ['.mjs', '.js'])

  for (const file of srcFiles) {
    const rel = path.relative(path.dirname(srcDir), file).replace(/\\/g, '/')
    const text = fs.readFileSync(file, 'utf8')
    violations.push(...scanFileContent(rel, text))

    const base = path.basename(file)
    if (base !== 'git.ts' && /\b(?:runCommand|spawn|execFile)\s*\(\s*['"]git['"]/.test(text)) {
      violations.push({
        rule: 'readonly-git-wrapper',
        file: rel,
        line: 1,
        message: 'Direct git spawn bypasses src/utils/git.ts read-only wrapper',
      })
    }
  }

  for (const file of distFiles) {
    const rel = path.relative(path.dirname(distDir), file).replace(/\\/g, '/')
    const text = fs.readFileSync(file, 'utf8')
    violations.push(...scanFileContent(rel, text))
  }

  const budgetFile = path.join(srcDir, 'utils', 'budget.ts')
  if (!fs.existsSync(budgetFile)) {
    violations.push({
      rule: 'inline-cap',
      file: 'src/utils/budget.ts',
      line: 1,
      message: 'Missing src/utils/budget.ts',
    })
  } else {
    const budgetText = fs.readFileSync(budgetFile, 'utf8')
    if (!/DEFAULT_INLINE_MAX_BYTES\s*=\s*8_?800\b/.test(budgetText)) {
      violations.push({
        rule: 'inline-cap',
        file: 'src/utils/budget.ts',
        line: 1,
        message: 'DEFAULT_INLINE_MAX_BYTES must be set to 8800',
      })
    }
  }

  const mcpProtoFile = path.join(srcDir, 'protocol', 'mcp.ts')
  if (!fs.existsSync(mcpProtoFile)) {
    violations.push({
      rule: 'inline-cap',
      file: 'src/protocol/mcp.ts',
      line: 1,
      message: 'Missing src/protocol/mcp.ts',
    })
  } else {
    const mcpText = fs.readFileSync(mcpProtoFile, 'utf8')
    if (!mcpText.includes('DEFAULT_INLINE_MAX_BYTES') || !mcpText.includes('truncateToJsonBytes')) {
      violations.push({
        rule: 'inline-cap',
        file: 'src/protocol/mcp.ts',
        line: 1,
        message: 'MCP protocol dispatcher must enforce DEFAULT_INLINE_MAX_BYTES via truncateToJsonBytes',
      })
    }
  }

  const gitFile = path.join(srcDir, 'utils', 'git.ts')
  if (!fs.existsSync(gitFile)) {
    violations.push({
      rule: 'readonly-git-flags',
      file: 'src/utils/git.ts',
      line: 1,
      message: 'Missing src/utils/git.ts',
    })
  } else {
    const gitText = fs.readFileSync(gitFile, 'utf8')
    const requiredFlags = [
      '--no-optional-locks',
      'core.fsmonitor=false',
      'diff.external=',
      'gpg.program=false',
    ]
    for (const flag of requiredFlags) {
      if (!gitText.includes(flag)) {
        violations.push({
          rule: 'readonly-git-flags',
          file: 'src/utils/git.ts',
          line: 1,
          message: `Missing mandatory read-only git flag "${flag}"`,
        })
      }
    }
  }

  return {
    ok: violations.length === 0,
    checkedSrcFiles: srcFiles.length,
    checkedDistFiles: distFiles.length,
    violations,
  }
}
