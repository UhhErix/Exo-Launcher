import { useEffect, useState } from 'react'
import { host, type Afmf3State } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'

/**
 * AMD's unreleased Fluid Motion Frames 3, on the Radeons and drivers it was reported on, and nowhere else. Off until pressed, off again in one press, never with Best
 * settings; it says what it is and that nobody knows all it changes. Once AMD releases it, the row goes, and one line says so if Exo took its preview off.
 */
export function Afmf3Row() {
  const [state, setState] = useState<Afmf3State | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let current = true
    host.gpuAfmf3().then(next => { if (current) setState(next) }, (cause: unknown) => report(cause, 'Afmf3Row.read', { level: 'warn' }))
    return () => { current = false }
  }, [])
  const flip = async (on: boolean) => {
    setBusy(true)
    setError(null)
    try {
      const next = await host.gpuAfmf3(on)
      setState(next)
      if (!next.ok) setError(next.message ?? 'Not changed. Try again.')
    } catch (cause) {
      report(cause, 'Afmf3Row.flip')
      setError('Not changed. Try again.')
    } finally {
      setBusy(false)
    }
  }
  if (state?.note && !state.offer) return <Row label={state.note} />
  if (!state?.offer) return null
  return (
    <Row label="Fluid Motion Frames 3 preview" error={error ?? state.note}
      detail="AMD’s unreleased preview: nobody outside AMD knows all it changes">
      <Switch label="Fluid Motion Frames 3 preview" checked={state.on} disabled={busy} onChange={next => void flip(next)} />
    </Row>
  )
}
