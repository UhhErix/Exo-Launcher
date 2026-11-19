// The upscaler update setting's words (Settings › General) and the quiet notice that offers a newer DLSS, FSR or XeSS the watch found while
// the setting is off. The notice's one button downloads and verifies it (dlss.downloadNewer); the download is that press, so it asks nothing.
import type { Notice } from './notify'

export const AUTO_UPSCALERS_LABEL = 'Update DLSS, FSR and XeSS automatically'
export const AUTO_UPSCALERS_DETAIL = 'Newer versions come from NVIDIA, AMD and Intel and go into your games by themselves, only if they signed them.'

export type NewerUpscaler = { family: string; version: string }

/** One notice for what was found: "A newer DLSS is available", or "Newer DLSS, XeSS are available". Null when nothing is newer. */
export function newerNotice(items: readonly NewerUpscaler[], onDownload: () => void): Notice | null {
  const families = [...new Set(items.map(item => item.family))]
  if (families.length === 0) return null
  const text = families.length === 1 ? `A newer ${families[0]} is available` : `Newer ${families.join(', ')} are available`
  return { id: 'upscalers-newer', text, action: { label: 'Download', onPress: onDownload } }
}
