import { buildExoSnapshot } from './tools/exo-snapshot.js'
import { MAX_SNAPSHOT_BYTES } from './utils/budget.js'

function parseArg(flag: string): string | undefined {
  const idx = process.argv.indexOf(flag)
  if (idx >= 0 && idx + 1 < process.argv.length) {
    return process.argv[idx + 1]
  }
  return undefined
}

async function main(): Promise<void> {
  const agent = (parseArg('--agent') ?? 'claude').toLowerCase()
  const cwd = parseArg('--cwd') ?? process.cwd()

  try {
    const snapshot = await buildExoSnapshot(cwd, MAX_SNAPSHOT_BYTES, 250)
    if (agent === 'antigravity') {
      process.stdout.write(
        JSON.stringify({
          injectSteps: [{ systemMessage: snapshot }],
        }) + '\n',
      )
    } else {
      process.stdout.write(snapshot + '\n')
    }
  } catch {
    if (agent === 'antigravity') {
      process.stdout.write('{}\n')
    }
  }
}

void main()
