export interface BiosVendorHints {
  vendor: 'ASUS' | 'MSI' | 'Gigabyte' | 'ASRock'
  xmp: string
  rebar: string
  secureBoot: string
  tpm: string
}

const HINTS: Record<BiosVendorHints['vendor'], BiosVendorHints> = {
  ASUS: {
    vendor: 'ASUS',
    xmp: 'Ai Tweaker → Ai Overclock Tuner',
    rebar: 'Advanced → PCI Subsystem Settings → Re-Size BAR Support',
    secureBoot: 'Boot → Secure Boot',
    tpm: 'Advanced → PCH-FW Configuration / AMD fTPM',
  },
  MSI: {
    vendor: 'MSI',
    xmp: 'OC → A-XMP / EXPO',
    rebar: 'Settings → Advanced → PCIe Subsystem Settings',
    secureBoot: 'Settings → Security → Secure Boot',
    tpm: 'Settings → Security → Trusted Computing',
  },
  Gigabyte: {
    vendor: 'Gigabyte',
    xmp: 'Tweaker → Extreme Memory Profile (X.M.P.)',
    rebar: 'Settings → IO Ports → Above 4G Decoding & Re-Size BAR',
    secureBoot: 'Boot → Secure Boot',
    tpm: 'Settings → Miscellaneous → Intel PTT / AMD CPU fTPM',
  },
  ASRock: {
    vendor: 'ASRock',
    xmp: 'OC Tweaker → DRAM Configuration',
    rebar: 'Advanced → PCI Configuration → Above 4G Decoding & C.A.M.',
    secureBoot: 'Security → Secure Boot',
    tpm: 'Advanced → CPU / Security → fTPM / PTT',
  },
}

const VENDOR_PATTERNS: ReadonlyArray<readonly [RegExp, BiosVendorHints['vendor']]> = [
  [/\b(?:asus|asustek)\b/i, 'ASUS'],
  [/\b(?:msi|micro-star)\b/i, 'MSI'],
  [/\bgigabyte\b/i, 'Gigabyte'],
  [/\basrock\b/i, 'ASRock'],
]

/** Vendor-specific BIOS menu paths for common firmware settings, matched from motherboard or maker strings. */
export function biosVendorHints(motherboard: string | null | undefined): BiosVendorHints | null {
  if (!motherboard) return null
  for (const [pattern, vendor] of VENDOR_PATTERNS)
    if (pattern.test(motherboard)) return HINTS[vendor]
  return null
}
