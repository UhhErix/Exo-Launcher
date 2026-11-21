export const RECOVERY_CODE_LENGTH = 24

export function normalizeRecoveryCode(text: string): string {
  return text.toUpperCase().replace(/[\s-]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1')
}

export function recoveryCodeConfirmed(typed: string, code: string): boolean {
  const entered = normalizeRecoveryCode(typed)
  return entered.length === RECOVERY_CODE_LENGTH && entered === normalizeRecoveryCode(code)
}
