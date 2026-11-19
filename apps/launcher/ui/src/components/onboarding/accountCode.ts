/** A recovery code is 24 characters (CLIENT.md 4.1 C). The service's alphabet has 0 and 1 where O, I and L would be. */
export const RECOVERY_CODE_LENGTH = 24

/** Dashes, spaces and case are ignored, and O, I and L read as 0, 1 and 1: what the person types is compared as the code. */
export function normalizeRecoveryCode(text: string): string {
  return text.toUpperCase().replace(/[\s-]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1')
}

/** Continue unlocks only when the typed text reads back as the same 24 characters. */
export function recoveryCodeConfirmed(typed: string, code: string): boolean {
  const entered = normalizeRecoveryCode(typed)
  return entered.length === RECOVERY_CODE_LENGTH && entered === normalizeRecoveryCode(code)
}
