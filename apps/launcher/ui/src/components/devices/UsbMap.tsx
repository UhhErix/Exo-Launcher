import { useEffect, useState } from 'react'
import { host, type UsbMapController, type UsbMapDevice } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, State } from '../settings/parts'
import { Dialog } from '../ui/Dialog'

const SPEED = ['1.5 Mbit/s', '12 Mbit/s', '480 Mbit/s', '5 Gbit/s or more']
const PLACE: Record<string, string> = { cpu: 'On the processor', chipset: 'On the chipset', addin: 'On an extra chip', dock: 'On a dock' }
const PLAYS = new Set(['mouse', 'keyboard', 'controller'])

/** What the hub reports: its speed, its hubs and, for what you play with, the fastest it may send. A claim about the device, never a measure. */
function facts(device: UsbMapDevice) {
  const parts: string[] = []
  if (device.speed != null) parts.push(SPEED[device.speed] ?? '5 Gbit/s or more')
  if (device.hubs > 0) parts.push(device.hub ? `through ${device.hub}` : `through ${device.hubs} ${device.hubs === 1 ? 'hub' : 'hubs'}`)
  if (device.interval != null) parts.push(`asks for up to ${Math.round(1_000_000 / device.interval).toLocaleString('en-US')} Hz`)
  return parts.join(' · ')
}

/** What the map is for: what else sits on the controller of something you play with. */
function sharing(controller: UsbMapController) {
  const playing = controller.devices.filter(d => d.kind && PLAYS.has(d.kind))
  const others = controller.devices.filter(d => !d.kind || !PLAYS.has(d.kind))
  if (playing.length === 0 || others.length === 0) return null
  return `${playing.map(d => d.name).join(' and ')} ${playing.length === 1 ? 'shares' : 'share'} this controller with ${others.map(d => d.name).join(', ')}`
}

/**
 * The USB map (DESIGN.md › Devices): every USB controller, from the processor's outward, and what hangs from it, from Devices' title line. Read only, from what the
 * hubs report. Windows can't say which socket is which, so it names none; a controller's place comes from its PCI ids and is an estimate, and it says so.
 */
export function UsbMap({ onClose }: { onClose: () => void }) {
  const [map, setMap] = useState<UsbMapController[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let current = true
    host.devicesUsbMap().then(next => { if (current) { setMap(next.controllers); setError(next.ok ? null : next.message ?? 'Exo couldn’t read your USB.') } }, (cause: unknown) => {
      report(cause, 'UsbMap.read')
      if (current) setError('Exo couldn’t read your USB.')
    })
    return () => { current = false }
  }, [])
  return (
    <Dialog title="USB map" className="exo-usb-map" onClose={onClose} actions={<button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Done</button>}>
      {error ? <Row label={error} /> : null}
      {map?.map((controller, i) => (
        <section key={`${controller.name}:${i}`} className="exo-usb-map-group">
          <Row label={controller.name} detail={sharing(controller) ?? undefined}>
            <State>{PLACE[controller.where] ?? 'Place not known'}{controller.interrupts === 'line' ? ' · shared line' : ''}</State>
          </Row>
          {controller.devices.map((device, j) => (
            <Row key={`${device.name}:${j}`} label={device.name} detail={facts(device) || undefined} />
          ))}
          {controller.devices.length === 0 ? <Row label="Nothing plugged in" /> : null}
        </section>
      ))}
      {map && map.length > 0 ? <Row label="A controller’s place is an estimate from its PCI ids. Windows can’t say which socket is which." /> : null}
    </Dialog>
  )
}
