/** Drives by the names people say ("Samsung 990 PRO"), not the long ones Windows reports ("Samsung SSD 990 PRO with Heatsink 2TB"). */

const MARKS = /\((?:R|TM|C)\)|®|™/gi

const tidy = (text: string) => text.replace(MARKS, '').replace(/\s+/g, ' ').trim()

/** "Samsung SSD 990 PRO with Heatsink 2TB" → "Samsung 990 PRO", "WD_BLACK SN850X 2000GB" → "WD_BLACK SN850X". */
export function driveName(full: string): string {
  return tidy(full)
    .replace(/\b(?:SSD|NVMe|M\.2|with Heatsink|Solid State Drive|Series)\b/gi, '')
    .replace(/\b\d+(?:\.\d+)?\s?(?:TB|GB)\b/gi, '')
    .replace(/\s+/g, ' ').trim()
}
