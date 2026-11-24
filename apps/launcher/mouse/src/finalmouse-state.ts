import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { FINALMOUSE_INTENT_TRIGGER, finalmouseIsUltraclick } from '@openmouse/protocol/finalmouse'

export const LATE_FIRMWARE = '1.1.1'
export const CUSTOM_LOD_FIRMWARE = '1.1.0'
export const INTENT_FIRMWARE = '1.2.0'
export const ULTRACLICK_FIRMWARE = '1.2.8'
export const ULTRACLICK_ACTUATION_STEPS = 10

export type ClickChoice = 'mechanical' | 'analog' | 'ultraclick'
export type FeatureSupport = { supported: boolean; needs: string | null }

export function parseFirmware(status: MouseStatus): Map<string, string> {
  const map = new Map<string, string>()
  for (const line of status.firmware ?? []) {
    const m = /^(.*?)\s*(\d+(?:\.\d+)+)/.exec(line)
    if (m) map.set(m[1].trim().toLowerCase(), m[2])
  }
  return map
}

export function versionOlder(version: string, than: string): boolean {
  const a = version.split('.').map(Number)
  const b = than.split('.').map(Number)
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) return (a[i] ?? 0) < (b[i] ?? 0)
  }
  return false
}

export function firmwarePartAtLeast(status: MouseStatus, part: string, version: string): boolean | null {
  const found = parseFirmware(status).get(part)
  return found ? !versionOlder(found, version) : null
}

export function finalmouseIntentSupport(status: MouseStatus): FeatureSupport {
  if (status.finalmouseClickIntentL != null || status.finalmouseClickIntentR != null) {
    return { supported: true, needs: null }
  }
  const mouseOld = firmwarePartAtLeast(status, 'mouse', INTENT_FIRMWARE) === false
  const dongleOld = status.connectionType !== 'Wired' && firmwarePartAtLeast(status, 'dongle rf', INTENT_FIRMWARE) === false
  if (mouseOld && dongleOld) return { supported: false, needs: `Needs mouse and dongle firmware ${INTENT_FIRMWARE}` }
  if (mouseOld) return { supported: false, needs: `Needs mouse firmware ${INTENT_FIRMWARE}` }
  if (dongleOld) return { supported: false, needs: `Needs dongle firmware ${INTENT_FIRMWARE}` }
  return { supported: firmwarePartAtLeast(status, 'mouse', INTENT_FIRMWARE) === true, needs: null }
}

export function finalmouseUltraclickSupport(status: MouseStatus): FeatureSupport {
  if (finalmouseIsUltraclick(status.finalmouseClickIntentL) || finalmouseIsUltraclick(status.finalmouseClickIntentR)) {
    return { supported: true, needs: null }
  }
  const mouseOld = firmwarePartAtLeast(status, 'mouse', ULTRACLICK_FIRMWARE) === false
  const dongleOld = status.connectionType !== 'Wired' && firmwarePartAtLeast(status, 'dongle rf', INTENT_FIRMWARE) === false
  if (mouseOld && dongleOld) return { supported: false, needs: `Needs mouse ${ULTRACLICK_FIRMWARE} and dongle ${INTENT_FIRMWARE}` }
  if (mouseOld) return { supported: false, needs: `Needs mouse firmware ${ULTRACLICK_FIRMWARE}` }
  if (dongleOld) return { supported: false, needs: `Needs dongle firmware ${INTENT_FIRMWARE}` }
  const hasIntent = status.finalmouseClickIntentL != null && status.finalmouseClickIntentR != null
  const mouseReady = firmwarePartAtLeast(status, 'mouse', ULTRACLICK_FIRMWARE) === true
  return { supported: hasIntent || mouseReady, needs: null }
}

export function clickChoiceOf(mode: number, intent: number | null | undefined): ClickChoice {
  if (mode !== 1) return 'mechanical'
  return finalmouseIsUltraclick(intent) ? 'ultraclick' : 'analog'
}

export function nextIntentForChoice(choice: ClickChoice, current: number | null | undefined): number | null {
  if (choice === 'ultraclick') return (current ?? FINALMOUSE_INTENT_TRIGGER.on) | FINALMOUSE_INTENT_TRIGGER.ultraclickBit
  return current == null ? null : current & ~FINALMOUSE_INTENT_TRIGGER.ultraclickBit
}

export function buildClickModeArgs(
  modeL: number,
  modeR: number,
  relL: number | null | undefined,
  relR: number | null | undefined,
  intL: number | null | undefined,
  intR: number | null | undefined,
): number[] {
  if (intL != null || intR != null) {
    return [modeL, modeR, relL ?? 0, relR ?? 0, intL ?? FINALMOUSE_INTENT_TRIGGER.on, intR ?? FINALMOUSE_INTENT_TRIGGER.on]
  }
  if (relL != null || relR != null) return [modeL, modeR, relL ?? 0, relR ?? 0]
  return [modeL, modeR]
}
