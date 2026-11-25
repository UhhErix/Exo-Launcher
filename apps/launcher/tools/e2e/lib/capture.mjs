export function isEnvironmentCaptureBlock(diag) {
  if (!diag || typeof diag !== 'object') return false
  if (diag.sessionId === 0) return true
  if (diag.winSta && diag.winSta.toLowerCase() !== 'winsta0') return true
  if (diag.logonUiRunning === true) return true
  if (diag.inputDesktop && diag.inputDesktop.toLowerCase() === 'winlogon') return true
  if (diag.inputDesktopError && diag.inputDesktopError !== 0) return true
  if (diag.threadDesktop && diag.inputDesktop && diag.threadDesktop.toLowerCase() !== diag.inputDesktop.toLowerCase()) return true
  return false
}

export function formatCaptureReason(diag, copyError = null, printWindowError = null) {
  const parts = []
  if (diag && typeof diag === 'object') {
    const winSta = diag.winSta || '?'
    const threadDesktop = diag.threadDesktop || '?'
    const inputDesktop = diag.inputDesktop || (diag.inputDesktopError ? `unavailable (Win32 ${diag.inputDesktopError})` : '?')
    if (diag.sessionId === 0) {
      parts.push(`process runs in non-interactive Session 0 (${winSta}\\${threadDesktop})`)
    } else if (diag.winSta && diag.winSta.toLowerCase() !== 'winsta0') {
      parts.push(`process window station is non-interactive (${winSta}\\${threadDesktop}, session ${diag.sessionId ?? '?'})`)
    } else if (diag.logonUiRunning === true || (diag.inputDesktop && diag.inputDesktop.toLowerCase() === 'winlogon')) {
      parts.push(`secure desktop is active (input desktop ${winSta}\\${inputDesktop}, LogonUI running=${Boolean(diag.logonUiRunning)})`)
    } else if (diag.threadDesktop && diag.inputDesktop && diag.threadDesktop.toLowerCase() !== diag.inputDesktop.toLowerCase()) {
      parts.push(`process desktop ${winSta}\\${threadDesktop} is not the active input desktop ${winSta}\\${inputDesktop} (session ${diag.sessionId ?? '?'})`)
    } else if (diag.inputDesktopError && diag.inputDesktopError !== 0) {
      parts.push(`cannot open input desktop from ${winSta}\\${threadDesktop} (Win32 ${diag.inputDesktopError}, session ${diag.sessionId ?? '?'})`)
    } else {
      parts.push(`desktop ${winSta}\\${threadDesktop} (input ${inputDesktop}, session ${diag.sessionId ?? '?'})`)
    }
  }
  if (copyError) parts.push(`CopyFromScreen: ${copyError}`)
  if (printWindowError) parts.push(`PrintWindow: ${printWindowError}`)
  return parts.join('; ') || 'screen capture failed with no diagnostic details'
}

export function classifyCaptureResult(result) {
  if (result && result.ok !== false && result.client) {
    return {
      ok: true,
      method: result.captureMethod || 'CopyFromScreen',
      info: result,
    }
  }
  const diag = result?.captureDiag ?? null
  return {
    ok: false,
    skip: isEnvironmentCaptureBlock(diag),
    reason: formatCaptureReason(diag, result?.copyError, result?.printWindowError),
    diag,
  }
}
