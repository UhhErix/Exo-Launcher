import { useCallback, useEffect, useState } from 'react'
import {
  host,
  type AutoTune,
  type DisplayState,
  type DriverState,
  type DrivesState,
  type GpuProfile,
  type Interrupts,
  type PcAbout,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { report } from '../../lib/report'
import { savedBranch } from './DriverRow'

export type PcState = {
  about: PcAbout | null
  profile: GpuProfile | null
  driver: DriverState | null
  health: PcHealth | null
  interrupts: Interrupts | null
  tuning: Tuning | null
  display: DisplayState | null
  drives: DrivesState | null
  tune: AutoTune | null
}

const last: PcState = {
  about: null,
  profile: null,
  driver: null,
  health: null,
  interrupts: null,
  tuning: null,
  display: null,
  drives: null,
  tune: null,
}

let lastReadAt = 0
const listeners = new Set<() => void>()

function emitChange() {
  for (const listener of listeners) listener()
}

export function usePcState(active: boolean) {
  const [state, setState] = useState<PcState>(() => ({ ...last }))

  useEffect(() => {
    const sync = () => setState({ ...last })
    listeners.add(sync)
    return () => { listeners.delete(sync) }
  }, [])

  const put = useCallback(<K extends keyof PcState>(key: K, value: PcState[K]) => {
    if (last[key] === value) return
    last[key] = value
    emitChange()
  }, [])

  const read = useCallback(async () => {
    lastReadAt = Date.now()
    const one = <K extends keyof PcState>(key: K, call: () => Promise<PcState[K]>) =>
      call().then(value => put(key, value), (error: unknown) => report(error, `PcState.${key}`, { level: 'warn' }))
    await Promise.all([
      one('about', () => host.systemAbout()),
      one('profile', () => host.gpuProfile()),
      one('driver', () => host.gpuDriver('state', savedBranch())),
      one('health', () => host.systemHealth()),
      one('interrupts', () => host.systemInterrupts()),
      one('tuning', () => host.systemTuning()),
      one('display', () => host.gpuDisplay('state')),
      one('tune', () => host.systemTune()),
      one('drives', async () => {
        const drives = await host.systemDrives()
        if (!drives.drives?.some(drive => drive.kind === 'Hard drive')) return drives
        put('drives', drives)
        return host.systemDrives(undefined, true)
      }),
    ])
  }, [put])

  useEffect(() => {
    if (!active) return
    if (Date.now() - lastReadAt >= 45_000) void read()
    let focusTimer: number | undefined
    const again = () => {
      window.clearTimeout(focusTimer)
      focusTimer = window.setTimeout(() => {
        if (Date.now() - lastReadAt >= 45_000) void read()
      }, 250)
    }
    window.addEventListener('focus', again)
    return () => {
      window.clearTimeout(focusTimer)
      window.removeEventListener('focus', again)
    }
  }, [active, read])

  return { state, put, read }
}
