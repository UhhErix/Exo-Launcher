export function formatUs(us: number): string {
  if (us >= 1_000_000) return `${(us / 1_000_000).toFixed(2)} s`
  if (us >= 1_000) return `${(us / 1_000).toFixed(1)} ms`
  return `${Math.round(us)} µs`
}

export function formatCauseLabel(cause: 'steady' | 'pc_busy' | 'usb_uneven' | 'both' | 'insufficient'): string {
  switch (cause) {
    case 'steady': return 'Steady delivery'
    case 'pc_busy': return 'PC busy (DPC/ISR)'
    case 'usb_uneven': return 'USB or mouse uneven'
    case 'both': return 'PC busy and USB uneven'
    default: return 'Need more movement'
  }
}

export function formatPlacement(placement: string): string {
  switch (placement) {
    case 'cpu': return 'CPU direct'
    case 'chipset': return 'Chipset'
    case 'addin': return 'Add-in'
    case 'dock': return 'Dock'
    default: return 'Unknown'
  }
}
