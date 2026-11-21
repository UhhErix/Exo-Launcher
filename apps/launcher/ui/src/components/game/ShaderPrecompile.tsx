import { useEffect, useState } from 'react'
import { host, onHostEvent, type ShaderStatus } from '../../lib/host'
import { onPageActivityChanged, pageIsVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { GameLine, type LineWord } from './GameLine'
import { sheetMemory } from './useGameDetails'

const shaderRead = sheetMemory<ShaderStatus>()

export type ShaderPrecompileProps = {
  gameId: string
  /** Opens PC › Graphics at Shaders (Exo Shaders), where the game is compiled. */
  onOpenShaders: (gameId: string) => void
}

const SHADERS_RELEASES = 'https://getexo.app/source'

/**
 * The game page's Shaders: one row from the Exo Shaders engine that opens PC › Graphics at Shaders, where
 * the compile runs with its queue and time. Compiled when it compiled the game for this driver, Not
 * supported when it can't; a shining word while it is read. Without Exo Shaders, the row opens its releases page.
 */
export function ShaderPrecompile({ gameId, onOpenShaders }: ShaderPrecompileProps) {
  const status = useShaderStatus(gameId)
  if (!status || status.state === 'unsupported') return null
  const noApp = status.state === 'noApp'
  return (
    <GameLine
      name="Shaders"
      word={wordFor(status)}
      state={status.state}
      external={noApp}
      onOpen={() => { if (noApp) void host.openUrl(SHADERS_RELEASES); else onOpenShaders(gameId) }}
    />
  )
}

function wordFor(status: ShaderStatus | null): LineWord | null {
  if (!status) return null
  switch (status.state) {
    case 'warmed': return { text: 'Compiled' }
    case 'stale': return { text: status.reason ?? 'Compile again' }
    case 'ready': case 'record': return { text: 'Not compiled' }
    case 'unsupported': return { text: 'Not supported in this game' }
    case 'noApp': return { text: 'Get Exo Shaders' }
    case 'error': return { text: 'Could not check', bad: true }
    default: return null
  }
}

/**
 * The game's line, read again whenever the app changes its list or a game's record (the host sends that at most once a
 * second). The last read shows at once. Hidden or minimized, changes are read once, when Exo shows again.
 */
function useShaderStatus(gameId: string) {
  const [status, setStatus] = useState<ShaderStatus | null>(() => shaderRead.get(gameId))

  useEffect(() => {
    let live = true
    let missed = false
    const load = async () => {
      if (!pageIsVisible()) { missed = true; return }
      missed = false
      try {
        const next = await host.shadersStatus(gameId)
        if (!live) return
        if (next.ok) shaderRead.set(gameId, next)
        setStatus(next)
      } catch (e) {
        report(e, 'ShaderPrecompile.status')
        if (live) setStatus({ ok: false, state: 'error' })
      }
    }
    setStatus(shaderRead.get(gameId))
    void load()
    const offChanged = onHostEvent('shaders.changed', () => { void load() })
    const offPage = onPageActivityChanged(() => { if (missed && pageIsVisible()) void load() })
    return () => { live = false; offChanged(); offPage() }
  }, [gameId])

  return status
}
