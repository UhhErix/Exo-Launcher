import { classOf, write } from './signal.js'

export const TARGETS = [
  ['updates', 'https://updates.getexo.app/health'],
  ['reports', 'https://reports.getexo.app/health'],
  ['covers', 'https://covers.getexo.app/health'],
  ['riot', 'https://riot.getexo.app/health'],
]

export async function selfTest(env, fetchImpl = (...args) => fetch(...args)) {
  if (!env.SIGNAL) return
  for (const [name, url] of TARGETS) {
    let klass = 'err'
    try {
      const response = await fetchImpl(url, { redirect: 'manual', signal: AbortSignal.timeout(10000), headers: { 'user-agent': 'exo-selftest' } })
      klass = classOf(response.status)
      await response.body?.cancel()
    } catch {}
    write(env, 'selftest', name, klass)
  }
}
