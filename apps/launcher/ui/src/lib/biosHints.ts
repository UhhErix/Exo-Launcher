export interface BiosVendorHints {
  vendor: 'ASUS' | 'MSI' | 'Gigabyte' | 'ASRock' | 'Biostar' | 'NZXT' | 'EVGA' | 'Supermicro' | 'Dell' | 'HP' | 'Lenovo'
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
  Biostar: {
    vendor: 'Biostar',
    xmp: 'Tweaker → Memory Clock Mode / XMP',
    rebar: 'Advanced → PCI Subsystem Settings → Re-Size BAR Support',
    secureBoot: 'Security → Secure Boot',
    tpm: 'Advanced → Trusted Computing',
  },
  NZXT: {
    vendor: 'NZXT',
    xmp: 'OC Tweaker → DRAM Configuration',
    rebar: 'Advanced → PCI Configuration → Above 4G Decoding & C.A.M.',
    secureBoot: 'Security → Secure Boot',
    tpm: 'Advanced → CPU / Security → fTPM / PTT',
  },
  EVGA: {
    vendor: 'EVGA',
    xmp: 'Memory → Memory Profile (XMP)',
    rebar: 'Advanced → PCIe Configuration → Resizable BAR',
    secureBoot: 'Boot → Secure Boot',
    tpm: 'Advanced → Trusted Computing',
  },
  Supermicro: {
    vendor: 'Supermicro',
    xmp: 'Advanced → Memory Configuration',
    rebar: 'Advanced → PCIe/PCI/PnP Configuration → Above 4G Decoding',
    secureBoot: 'Security → Secure Boot',
    tpm: 'Advanced → Trusted Computing',
  },
  Dell: {
    vendor: 'Dell',
    xmp: 'Advanced → Performance Options → XMP Memory',
    rebar: 'Advanced → PCIe Configuration → Resizable BAR',
    secureBoot: 'Boot Configuration → Secure Boot',
    tpm: 'Security → TPM 2.0 Security',
  },
  HP: {
    vendor: 'HP',
    xmp: 'Advanced → Memory Profile (XMP)',
    rebar: 'Advanced → PCIe Configuration → Resizable BAR',
    secureBoot: 'Security → Secure Boot Configuration',
    tpm: 'Security → TPM Device',
  },
  Lenovo: {
    vendor: 'Lenovo',
    xmp: 'Configuration → Memory Profile (XMP)',
    rebar: 'Devices → PCIe Configuration → Resizable BAR',
    secureBoot: 'Security → Secure Boot',
    tpm: 'Security → Intel Platform Trust Technology / AMD PSP',
  },
}

const VENDOR_PATTERNS: ReadonlyArray<readonly [RegExp, BiosVendorHints['vendor']]> = [
  [/\b(?:asus|asustek)\b/i, 'ASUS'],
  [/\b(?:msi|micro-star)\b/i, 'MSI'],
  [/\bgigabyte\b/i, 'Gigabyte'],
  [/\basrock\b/i, 'ASRock'],
  [/\bbiostar\b/i, 'Biostar'],
  [/\bnzxt\b/i, 'NZXT'],
  [/\bevga\b/i, 'EVGA'],
  [/\bsupermicro\b/i, 'Supermicro'],
  [/\b(?:dell|alienware)\b/i, 'Dell'],
  [/\b(?:hp|hewlett)\b/i, 'HP'],
  [/\blenovo\b/i, 'Lenovo'],
]

/** Vendor-specific BIOS menu paths for common firmware settings, matched from motherboard or maker strings. */
export function biosVendorHints(motherboard: string | null | undefined): BiosVendorHints | null {
  if (!motherboard) return null
  for (const [pattern, vendor] of VENDOR_PATTERNS)
    if (pattern.test(motherboard)) return HINTS[vendor]
  return null
}
