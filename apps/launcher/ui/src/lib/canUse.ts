// What this PC can use, so a room draws only that (PRODUCT.md: no greyed-out rows, no "not supported" cards). Each answer
// comes from a probe the host already makes once and keeps (the GPU's maker, the main screen's HDR, a device's link,
// the stores found, the add-ons present); when a probe has not answered, the row shows, since hiding one that works is
// worse than showing one that doesn't. The game page's fixed set of lines is not decided here (GameSections.tsx).
import type { CompressState, HdrState, PeripheralKind } from './host'

type GpuVendor = 'nvidia' | 'amd' | 'intel'

/** What each graphics card maker's driver lets Exo set, by maker; null (no card Exo reaches) offers none of it. */
export function gpuOffers(vendor: GpuVendor | null | undefined, workstation = false) {
  return {
    /** NVIDIA's Game Ready or Studio beside Update; an RTX / Quadro card gets the one Enterprise driver. */
    driverType: vendor === 'nvidia' && !workstation,
    /** Intel's Low latency choice on Exo's profile row. */
    lowLatency: vendor === 'intel',
    /** AMD's Fluid Motion Frames 3 preview (its row still asks the driver). */
    afmf3: vendor === 'amd',
    /** The maker's own control panel chip, for what Exo doesn't set: AMD Software or Intel Graphics. On a GeForce, Exo is it. */
    makerPanel: vendor === 'amd' || vendor === 'intel',
  }
}

/** HDR's rows (PC › Display, Best settings): only on a main screen that can show HDR. Not read yet: nothing to show either. */
export const hdrShown = (screen: Pick<HdrState, 'supported'> | null | undefined) => screen?.supported === true

/** A setting the screen, driver or device takes one way only is not a setting: no row. */
export const isChoice = (options: readonly unknown[]) => new Set(options).size > 1

/**
 * A controller's light, sound, vibration and calibration are sent over its cable only; over Bluetooth they can't be,
 * so those rows go and one line says why.
 */
export const padSettable = (connection: string | null | undefined) => connection === 'usb'

/**
 * A device menu (microphone, speaker, camera): only with two or more to pick from. With one, Default is that one, so the
 * row just names it; with none, it says so.
 */
export const devicePicker = (count: number): 'none' | 'one' | 'menu' => count === 0 ? 'none' : count === 1 ? 'one' : 'menu'

/** The Riot ID row imports hours of Riot's games: only with the Riot Client on this PC, or an ID already saved to take out. */
export const riotIdShown = (stores: readonly string[], riotId: string | null | undefined) => stores.includes('riot') || !!riotId?.trim()

/** Devices: a card opens a page Exo has for it (Exo Mouse, the Controller Lab's pads, audio); one with no page isn't drawn. */
export const deviceHasPage = (kind: PeripheralKind, lab: boolean | null | undefined) =>
  kind !== 'keyboard' && (kind !== 'controller' || lab === true)

/** Properties › Compress: a game Exo will never compress (the PC list leaves it out too) has no row. */
export const compressShown = (compress: Pick<CompressState, 'state'> | null | undefined) => !!compress && compress.state !== 'refused'
