import type { PeripheralPort } from '../../lib/host'

/** Where a device meets the PC, in words ("Through the chipset", "Processor's own USB"). */
export function portWords(port: PeripheralPort): string {
  const hubs = port.hubs === 1 ? 'a hub' : `${port.hubs} hubs`
  switch (port.where) {
    case 'cpu': return port.hubs ? `Processor's own USB, through ${hubs}` : "Processor's own USB"
    case 'chipset': return port.hubs ? `Through ${hubs} and the chipset` : 'Through the chipset'
    case 'addin': return port.hubs ? `Through ${hubs} and an extra USB chip` : 'On an extra USB chip'
    case 'dock': return 'Through a dock'
    case 'bluetooth': return 'Bluetooth'
    default: return 'Unknown'
  }
}

/**
 * The port in a word or two, for a card's tag at its top right ("Chipset USB"); null over Bluetooth or when Exo can't
 * tell. The controller page's USB port panel gives the whole sentence (portWords).
 */
export function portShort(port: PeripheralPort): string | null {
  const via = port.hubs ? ' via hub' : ''
  switch (port.where) {
    case 'cpu': return `CPU USB${via}`
    case 'chipset': return `Chipset USB${via}`
    case 'addin': return `Add-in USB${via}`
    case 'dock': return 'Dock'
    default: return null
  }
}
