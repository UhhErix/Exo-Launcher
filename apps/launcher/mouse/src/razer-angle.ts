import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'

/**
 * Razer's sensor angle (Synapse's "angle rotation"), which OpenMouse's Razer driver does not have yet. Exo adds it to
 * the driver's client for the models it is known on, so the Sensor panel's angle row (brands/core-extras.tsx) shows it
 * and the rest of the room treats it like any other brand's angle.
 *
 * Decoded from a USB capture of Synapse on a Viper V3 Pro on its cable (1532:00C0, firmware as shipped), changing the
 * angle to +10, -10, +30 and 0. Each change is one SET_REPORT (feature report 0, interface 3) of Razer's 90-byte
 * control report, answered by a GET_REPORT with status 0x02 (ok) echoing the request:
 *
 *   byte  0     status (0x00 sent; 0x02 ok in the reply)
 *   byte  1     transaction id (Synapse counted 0x00, 0x01, ... per command; the cable took any)
 *   bytes 2-4   remaining packets, protocol type: 0
 *   byte  5     data size 0x03
 *   byte  6     command class 0x0b (sensor)
 *   byte  7     command id 0x14 (set angle)
 *   bytes 8-10  args 0x01 0x01 <angle>: the angle a signed byte in degrees (0x0a +10, 0xf6 -10, 0x1e +30, 0x00 0)
 *   byte  88    CRC: XOR of bytes 2 to 87
 *
 *   +10: 00 00 00 00 00 03 0b 14 01 01 0a ... 16 00
 *   -10: 00 01 00 00 00 03 0b 14 01 01 f6 ... ea 00
 *   +30: 00 06 00 00 00 03 0b 14 01 01 1e ... 02 00
 *     0: 00 07 00 00 00 03 0b 14 01 01 00 ... 1c 00
 *
 * The first 0x01 matches the persistent store byte every other V3 Pro setting uses (RAZER_STORAGE); the second was
 * 0x01 in every packet, 0 degrees included, so both are sent as captured and never varied. Synapse sent no read of
 * the angle in the capture. Razer pairs each set with a get whose command id has the high bit set (0x0b/0x05 lift-off
 * with 0x0b/0x85), so Exo asks 0x0b/0x94 with the same two arguments once per connection; the mouse answers a get it
 * doesn't know with status 0x05 and nothing changes. That read is not in the capture: when it is not answered, or
 * after a set answers with another angle, the row says the angle wasn't read and shows what Exo last set on this
 * connection (0 before that, a placeholder Measure never corrects from). A set counts as taken on its status 0x02
 * reply, as in the capture.
 *
 * The transaction id is the driver's for the model (0x1f on both of the V3 Pro's ids), which every other verified V3
 * Pro command uses; OpenRazer, razer-macos and librazermacos have no sensor angle command to compare with.
 *
 * Synapse's slider range is not in the capture (it went as far as +30); Exo offers -30 to +30, as for every other brand.
 */

export const RAZER_ANGLE_MIN = -30
export const RAZER_ANGLE_MAX = 30

/** Razer's vendor id, and the models whose angle command is known: the Viper V3 Pro on its cable and its HyperSpeed receiver. */
const RAZER = 0x1532
export const RAZER_ANGLE_PRODUCTS: ReadonlySet<number> = new Set([0x00c0, 0x00c1])

/** One of the driver's commands, as `encodeRazerRequest` takes it. */
export type RazerCommand = { commandClass: number; commandId: number; dataSize: number; args?: number[] }

/** The bytes before the angle, as captured. */
const ANGLE_ARGS = [0x01, 0x01]

export function razerSetAngleCommand(degrees: number): RazerCommand {
  if (!Number.isInteger(degrees) || degrees < RAZER_ANGLE_MIN || degrees > RAZER_ANGLE_MAX) {
    throw new RangeError(`The sensor angle must be a whole number of degrees from ${RAZER_ANGLE_MIN} to ${RAZER_ANGLE_MAX}.`)
  }
  return { commandClass: 0x0b, commandId: 0x14, dataSize: 0x03, args: [...ANGLE_ARGS, degrees & 0xff] }
}

/** The get that pairs with the set (not captured; see above). */
export const RAZER_READ_ANGLE: RazerCommand = { commandClass: 0x0b, commandId: 0x94, dataSize: 0x03, args: [...ANGLE_ARGS] }

/** The angle in a reply's arguments (a signed byte after the two captured ones), or null when it is out of any range Exo offers. */
export function decodeRazerAngle(args: ArrayLike<number>): number | null {
  if (args.length < 3) return null
  const byte = args[2] & 0xff
  const degrees = byte > 127 ? byte - 256 : byte
  return degrees >= -90 && degrees <= 90 ? degrees : null
}

/** The part of OpenMouse's Razer client Exo uses: the device and its one-command exchange, which checks status and CRC. */
type RazerClient = {
  device: HIDDevice
  readStatus(): Promise<MouseStatus>
  request(command: RazerCommand): Promise<Uint8Array>
  setAngleTuning?: (degrees: number) => Promise<number>
}

/**
 * What the angle row reads besides `angleTuning`: the mouse did not answer the read (`razerAngleUnread`), and nor has
 * Exo set an angle on this connection, so the 0 shown is a placeholder, not the mouse's angle (`razerAngleUnknown`):
 * Measure never corrects from it.
 */
export type RazerAngleStatus = MouseStatus & { razerAngleUnread?: boolean; razerAngleUnknown?: boolean }

export function hasRazerAngle(device: Pick<HIDDevice, 'vendorId' | 'productId'>): boolean {
  return device.vendorId === RAZER && RAZER_ANGLE_PRODUCTS.has(device.productId)
}

/**
 * Gives a Razer client for a model in RAZER_ANGLE_PRODUCTS the angle: `angleTuning` in its status (read from the mouse
 * once per connection) and `setAngleTuning`. Any other client is left as it is, so no other mouse gets the row.
 */
export function attachRazerAngle(client: unknown): void {
  const c = client as Partial<RazerClient> | null
  if (!c || !c.device || typeof c.request !== 'function' || typeof c.readStatus !== 'function' || !hasRazerAngle(c.device)) return
  const razer = c as RazerClient
  const read = razer.readStatus.bind(razer)
  /** undefined until asked; null when the mouse didn't answer. */
  let known: number | null | undefined
  let written: number | null = null
  const ask = async () => {
    try { return decodeRazerAngle(await razer.request(RAZER_READ_ANGLE)) } catch { return null }
  }
  razer.readStatus = async () => {
    const status = await read()
    if (known === undefined) known = await ask()
    return {
      ...status,
      angleTuning: known ?? written ?? 0,
      ...(known === null ? { razerAngleUnread: true } : {}),
      ...(known === null && written === null ? { razerAngleUnknown: true } : {}),
    } as RazerAngleStatus
  }
  razer.setAngleTuning = async (degrees: number) => {
    // The request throws unless the mouse answers status 0x02, the one confirmation the capture shows.
    await razer.request(razerSetAngleCommand(degrees))
    written = degrees
    if (known === null) return degrees
    // A read that answered before but doesn't say the angle just taken isn't reading the angle (a model can answer a
    // get with zeros): from then on the row says so instead of showing it.
    const now = await ask()
    known = now === degrees ? now : null
    return degrees
  }
}
