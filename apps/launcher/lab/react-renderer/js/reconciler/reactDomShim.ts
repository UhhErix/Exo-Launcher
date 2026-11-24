// SPDX-License-Identifier: MIT

import type { ReactNode } from 'react'
import { ConcurrentRoot } from 'react-reconciler/constants'
import { RuneReconciler } from './hostConfig'
import { RuneElement, logError, rootEl } from './domShim'

export function createRoot(container: RuneElement | null) {
  const target = container ?? rootEl
  const fiberRoot = RuneReconciler.createContainer(
    target,
    ConcurrentRoot,
    null,
    false,
    null,
    '',
    (err: unknown) => logError('react-recoverable', err),
    (err: unknown) => logError('react-caught', err),
    (err: unknown) => logError('react-uncaught', err),
    null,
  )
  return {
    render(element: ReactNode) {
      RuneReconciler.updateContainerSync(element, fiberRoot, null, () => {})
      RuneReconciler.flushSyncWork()
    },
    unmount() {
      RuneReconciler.updateContainerSync(null, fiberRoot, null, () => {})
      RuneReconciler.flushSyncWork()
    },
  }
}

export function createPortal(children: ReactNode, container: RuneElement, key?: string | null) {
  return RuneReconciler.createPortal(children, container ?? rootEl, null, key ?? null)
}

export function flushSync<T>(fn: () => T): T {
  return RuneReconciler.flushSyncFromReconciler(fn)
}

export function unstable_batchedUpdates<T>(fn: () => T): T {
  return fn()
}

export default {
  createRoot,
  createPortal,
  flushSync,
  unstable_batchedUpdates,
}
