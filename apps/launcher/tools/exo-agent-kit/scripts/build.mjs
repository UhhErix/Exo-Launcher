import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'
import { verifyGuarantees } from './verify-guarantees.mjs'

const KIT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const SRC_DIR = path.join(KIT_ROOT, 'src')
const DIST_DIR = path.join(KIT_ROOT, 'dist')

function runTypecheck() {
  const tscJs = path.join(KIT_ROOT, 'node_modules', 'typescript', 'bin', 'tsc')
  execFileSync(process.execPath, [tscJs, '--noEmit', '-p', path.join(KIT_ROOT, 'tsconfig.json')], {
    cwd: KIT_ROOT,
    stdio: 'inherit',
  })
}

function resolveGitVersion() {
  try {
    return execFileSync(
      'git',
      [
        '--no-optional-locks',
        '-c',
        'core.fsmonitor=false',
        '-c',
        'diff.external=',
        '-c',
        'gpg.program=false',
        'rev-parse',
        '--short=12',
        'HEAD',
      ],
      { cwd: KIT_ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] },
    ).trim()
  } catch {
    return 'unknown'
  }
}

async function main() {
  runTypecheck()

  fs.mkdirSync(DIST_DIR, { recursive: true })

  const entrypoints = [
    { in: path.join(SRC_DIR, 'mcp-server.ts'), out: path.join(DIST_DIR, 'mcp-server.mjs') },
    { in: path.join(SRC_DIR, 'session-start.ts'), out: path.join(DIST_DIR, 'session-start.mjs') },
    { in: path.join(SRC_DIR, 'exo-orca-helper.ts'), out: path.join(DIST_DIR, 'exo-orca-helper.mjs') },
  ]

  for (const ep of entrypoints) {
    await build({
      entryPoints: [ep.in],
      outfile: ep.out,
      bundle: true,
      platform: 'node',
      format: 'esm',
      target: 'node22',
      legalComments: 'none',
      minifyWhitespace: true,
      minifySyntax: false,
      minifyIdentifiers: false,
      sourcemap: false,
      treeShaking: true,
    })
  }

  const version = resolveGitVersion()
  fs.writeFileSync(path.join(DIST_DIR, 'VERSION'), `${version}\n`, 'utf8')

  const check = verifyGuarantees({ srcDir: SRC_DIR, distDir: DIST_DIR })
  if (!check.ok) {
    for (const v of check.violations) {
      console.error(`[GUARANTEE FAIL] ${v.file}:${v.line} (${v.rule}): ${v.message}`)
    }
    process.exit(1)
  }

  const sizes = entrypoints.map((ep) => {
    const bytes = fs.statSync(ep.out).size
    return `${path.basename(ep.out)}=${bytes}B`
  })
  console.log(
    `Build OK (rev=${version}, checked ${check.checkedSrcFiles} src + ${check.checkedDistFiles} dist): ${sizes.join(', ')}`,
  )
}

main().catch((err) => {
  console.error(err)
  process.exit(1)
})
