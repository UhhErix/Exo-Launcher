import test from 'node:test'
import assert from 'node:assert/strict'
import { classifyCaptureResult, formatCaptureReason, isEnvironmentCaptureBlock } from './capture.mjs'

test('CopyFromScreen success returns ok and CopyFromScreen method', () => {
  const res = classifyCaptureResult({
    ok: true,
    captureMethod: 'CopyFromScreen',
    client: { x: 0, y: 0, w: 1920, h: 1080 },
    monitor: { x: 0, y: 0, w: 1920, h: 1080 },
  })
  assert.equal(res.ok, true)
  assert.equal(res.method, 'CopyFromScreen')
  assert.equal(res.info.client.w, 1920)
})

test('PrintWindow fallback from an exebox worker desktop returns ok and PrintWindow method', () => {
  const res = classifyCaptureResult({
    ok: true,
    captureMethod: 'PrintWindow',
    copyError: 'The handle is invalid. (Win32 6)',
    captureDiag: {
      sessionId: 1,
      activeConsoleSessionId: 1,
      winSta: 'WinSta0',
      threadDesktop: 'exebox-2FRND7QNENZMD7VGFAUMZMFHSA',
      inputDesktop: 'Default',
      inputDesktopError: 0,
      logonUiRunning: false,
    },
    client: { x: 0, y: 0, w: 1920, h: 1080 },
  })
  assert.equal(res.ok, true)
  assert.equal(res.method, 'PrintWindow')
})

test('failure on non-input worker desktop skips with exact desktop mismatch and never blames a lock', () => {
  const res = classifyCaptureResult({
    ok: false,
    captureMethod: 'none',
    copyError: 'The handle is invalid. (Win32 6)',
    printWindowError: 'PrintWindow(flags=2) returned false (Win32 error 5)',
    captureDiag: {
      sessionId: 1,
      activeConsoleSessionId: 1,
      winSta: 'WinSta0',
      threadDesktop: 'exebox-TEST1234',
      inputDesktop: 'Default',
      inputDesktopError: 0,
      logonUiRunning: false,
    },
  })
  assert.equal(res.ok, false)
  assert.equal(res.skip, true)
  assert.match(res.reason, /WinSta0\\exebox-TEST1234/)
  assert.match(res.reason, /WinSta0\\Default/)
  assert.doesNotMatch(res.reason, /locked/i)
})

test('failure in Session 0 or non-WinSta0 window station skips with exact session diagnosis', () => {
  const session0 = {
    sessionId: 0,
    activeConsoleSessionId: 1,
    winSta: 'Service-0x0-3e7$',
    threadDesktop: 'Default',
    inputDesktop: null,
    inputDesktopError: 5,
    logonUiRunning: false,
  }
  assert.equal(isEnvironmentCaptureBlock(session0), true)
  const reason = formatCaptureReason(session0, 'The handle is invalid. (Win32 6)', null)
  assert.match(reason, /Session 0/)
  assert.match(reason, /Service-0x0-3e7\$\\Default/)
})

test('failure on active WinSta0 Default does not skip so real capture regressions fail', () => {
  const res = classifyCaptureResult({
    ok: false,
    captureMethod: 'none',
    copyError: 'Access denied',
    printWindowError: 'PrintWindow(flags=2) returned false (Win32 error 5)',
    captureDiag: {
      sessionId: 1,
      activeConsoleSessionId: 1,
      winSta: 'WinSta0',
      threadDesktop: 'Default',
      inputDesktop: 'Default',
      inputDesktopError: 0,
      logonUiRunning: false,
    },
  })
  assert.equal(res.ok, false)
  assert.equal(res.skip, false)
  assert.match(res.reason, /WinSta0\\Default/)
})

test('failure when Winlogon input desktop or LogonUI is active skips as secure desktop', () => {
  const res = classifyCaptureResult({
    ok: false,
    captureMethod: 'none',
    copyError: 'The handle is invalid. (Win32 6)',
    printWindowError: 'PrintWindow(flags=2) returned false (Win32 error 0)',
    captureDiag: {
      sessionId: 1,
      activeConsoleSessionId: 1,
      winSta: 'WinSta0',
      threadDesktop: 'Default',
      inputDesktop: 'Winlogon',
      inputDesktopError: 0,
      logonUiRunning: true,
    },
  })
  assert.equal(res.ok, false)
  assert.equal(res.skip, true)
  assert.match(res.reason, /secure desktop/i)
  assert.match(res.reason, /Winlogon/)
})
