import { host } from '../../lib/host'

export type Tone = 'good' | 'warn' | 'primary' | 'muted' | 'bad'
export type Estimate = { seconds: number; lowSeconds?: number; learned: boolean }
export type ShaderGame = {
  recorder?: { on?: boolean; canSet?: boolean; refusal?: string | null; newPipelines?: number }
  needsFirstLaunch?: boolean
  id: string
  name: string
  installDir?: string
  statusText?: string
  tone?: Tone
  queued?: boolean
  compiling?: boolean
  percent?: number
  estimate?: Estimate | null
  inAddAllReady?: boolean
  actions?: { compile?: boolean; compileText?: string }
  error?: string | null
}
export type QueueItem = { id: string; name: string; stageText?: string; percent?: number; timeLeftSeconds?: number; error?: string | null }
export type Queue = { current?: QueueItem | null; waiting: QueueItem[]; finished?: QueueItem[]; running: boolean; paused: boolean; canStart: boolean }

export type Snapshot = {
  rev: number
  games: ShaderGame[]
  recorder?: { auto?: boolean }
  queue: Queue
  app: {
    scanning?: boolean
    readyCount?: number
    gpu?: {
      name?: string
      vendor?: string
      vendorName?: string
      driver?: string
      driverLine?: string
      shaderDelivery?: { works: boolean; text: string } | null
      windowsCleanup?: { kept: boolean; canUndo: boolean; text: string } | null
    }
    cache?: {
      usedText?: string
      limitCurrent?: string
      isDriverDefault?: boolean
      warn?: string | null
      off?: string | null
      configurable?: boolean
      largest?: boolean
      oldDriver?: number
      oldDriverNote?: string | null
      amdDx12?: { used: number; text: string; percent: number } | null
      autoShader?: string | null
    } | null
  }
}

export const call = (method: string, params?: unknown) => host.shadersCall(method, params ?? {})
  .then(reply => {
    if (!reply.ok) throw new Error(reply.message || 'The shader engine did not answer.')
    const res = reply.result as { ok?: boolean; text?: string } | null | undefined
    if (res && typeof res === 'object' && res.ok === false) {
      throw new Error(res.text || 'The shader engine could not apply this setting.')
    }
    return reply.result
  })

export const message = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback
