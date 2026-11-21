import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Row, Section } from '../settings/parts'
import { call, message, type Snapshot } from './shaderCall'

/**
 * Settings › Library › Shaders: only the opt-in DLL recorder switch. First-launch compilation runs automatically
 * when you are away, and NVIDIA's cache setting is automated.
 */
export function ShadersSection() {
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Recording is Exo Shaders' own: without the add-on its row goes (unknown until asked: shown).
  const [present, setPresent] = useState(true)
  const read = useCallback(() => call('snapshot').then(next => setSnap(next as Snapshot | null)).catch((caught: unknown) => report(caught, 'ShaderTools.snapshot', { level: 'warn' })), [])
  useEffect(() => {
    void read()
    void host.shadersAddon().then(status => setPresent(status.present)).catch((caught: unknown) => report(caught, 'ShaderTools.addon', { level: 'warn' }))
    return onHostEvent('shaders.event', ({ name, data }) => {
      if (name === 'state') { setSnap(data as Snapshot); setPresent(true) }
      else if (name === 'addon' || name === 'ready') void read()
    })
  }, [read])
  const run = (method: string, params?: unknown) => {
    setError(null)
    void call(method, params).then(read, (caught: unknown) => {
      report(caught, `ShaderTools.${method}`, { level: 'warn' })
      setError(message(caught, 'The shader engine did not answer.'))
    })
  }
  if (!present) return null
  return (
    <Section id="shaders" title="Shaders">
      <Row label="Improve shader pre-compiling" detail="Adds a small d3d12.dll to supported games to cut shader stutter" error={error}>
        <Switch checked={!!snap?.recorder?.auto} label="Improve shader pre-compiling" onChange={on => run('recorder.auto', { on })} />
      </Row>
    </Section>
  )
}
