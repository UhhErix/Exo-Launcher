// SPDX-License-Identifier: MIT

import Reconciler from 'react-reconciler'
import { DefaultEventPriority } from 'react-reconciler/constants'
import {
  RuneElement,
  RuneText,
  allocNodeId,
  enqueueOp,
  flushOps,
  kebabCase,
  logError,
} from './domShim'

type Props = Record<string, unknown>
type Container = RuneElement

let currentUpdatePriority: number = DefaultEventPriority

function isEventProp(k: string): boolean {
  return k.length > 2 && k.startsWith('on') && k[2] === k[2].toUpperCase()
}

function serializeProps(el: RuneElement, nextProps: Props, prevProps?: Props): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  const eventTypes: string[] = []

  if (prevProps) {
    for (const k of Object.keys(prevProps)) {
      if (k === 'children' || k === 'key' || k === 'ref') continue
      if (!(k in nextProps)) {
        if (isEventProp(k)) {
          el.reactHandlers.delete(k)
        } else if (k === 'className' || k === 'class') {
          el.className = ''
          out.className = ''
        } else if (k === 'style') {
          el.styleMap.clear()
          out.style = {}
        } else {
          el.attributesMap.delete(k)
          out[k] = null
        }
      }
    }
  }

  for (const [k, v] of Object.entries(nextProps)) {
    if (k === 'children' || k === 'key' || k === 'ref' || k === 'dangerouslySetInnerHTML') continue
    if (isEventProp(k)) {
      if (typeof v === 'function') {
        el.reactHandlers.set(k, v as (ev: unknown) => void)
      } else {
        el.reactHandlers.delete(k)
      }
      continue
    }
    if (k === 'className' || k === 'class') {
      const str = v == null ? '' : String(v)
      el.attributesMap.set('class', str)
      ;(el as unknown as { _className: string })._className = str
      out.className = str
      continue
    }
    if (k === 'style') {
      const styleObj: Record<string, string> = {}
      el.styleMap.clear()
      if (v && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v as Record<string, unknown>)) {
          if (sv == null || sv === false) continue
          const cssKey = kebabCase(sk)
          const cssVal = typeof sv === 'number' && !sk.startsWith('--') && sk !== 'opacity' && sk !== 'fontWeight' && sk !== 'zIndex' && sk !== 'flexGrow' && sk !== 'flexShrink' && sk !== 'order' && sk !== 'lineHeight' && sk !== 'flex'
            ? `${sv}px`
            : String(sv)
          el.styleMap.set(cssKey, cssVal)
          styleObj[cssKey] = cssVal
        }
      }
      out.style = styleObj
      continue
    }
    if (k === 'value' || k === 'defaultValue') {
      const str = v == null ? '' : String(v)
      ;(el as unknown as { _value: string })._value = str
      el.attributesMap.set('value', str)
      out.value = str
      continue
    }
    if (k === 'checked' || k === 'defaultChecked') {
      ;(el as unknown as { _checked: boolean })._checked = Boolean(v)
      out.checked = Boolean(v)
      continue
    }
    if (k === 'disabled') {
      ;(el as unknown as { _disabled: boolean })._disabled = Boolean(v)
      if (v) el.attributesMap.set('disabled', 'true')
      else el.attributesMap.delete('disabled')
      out.disabled = Boolean(v)
      continue
    }
    if (k === 'hidden') {
      ;(el as unknown as { _hidden: boolean })._hidden = Boolean(v)
      if (v) el.attributesMap.set('hidden', 'true')
      else el.attributesMap.delete('hidden')
      out.hidden = Boolean(v)
      continue
    }
    if (v == null || v === false) {
      el.attributesMap.delete(k)
      out[k] = null
    } else if (v === true) {
      el.attributesMap.set(k, 'true')
      out[k] = 'true'
    } else if (typeof v === 'string' || typeof v === 'number') {
      const str = String(v)
      el.attributesMap.set(k, str)
      out[k] = str
    }
  }

  for (const ek of el.reactHandlers.keys()) {
    eventTypes.push(ek)
  }
  out.__events = eventTypes
  return out
}

const hostConfig = {
  rendererPackageName: 'exo-rune-react-renderer',
  rendererVersion: '0.1.0',
  supportsMutation: true,
  supportsPersistence: false,
  supportsHydration: false,
  isPrimaryRenderer: true,
  noTimeout: -1,
  supportsMicrotasks: true,
  scheduleMicrotask: (fn: () => void) => {
    Promise.resolve().then(fn)
  },
  scheduleTimeout: setTimeout,
  cancelTimeout: clearTimeout,

  getRootHostContext: () => ({}),
  getChildHostContext: (parentCtx: Record<string, unknown>) => parentCtx,
  getPublicInstance: (instance: RuneElement | RuneText) => instance,

  prepareForCommit: () => null,
  resetAfterCommit: () => {
    flushOps()
  },
  preparePortalMount: () => {},

  shouldSetTextContent: () => false,

  createInstance(type: string, props: Props): RuneElement {
    const id = allocNodeId()
    const el = new RuneElement(id, type)
    const serialized = serializeProps(el, props)
    enqueueOp({ op: 'create', id, tag: type.toLowerCase(), props: serialized })
    return el
  },

  createTextInstance(text: string): RuneText {
    const id = allocNodeId()
    const node = new RuneText(id, text)
    enqueueOp({ op: 'createText', id, text })
    return node
  },

  appendInitialChild(parent: RuneElement, child: RuneElement | RuneText): void {
    child.parentNode = parent
    parent.childNodes.push(child)
    enqueueOp({ op: 'append', parent: parent.nodeId, child: child.nodeId })
  },

  finalizeInitialChildren(): boolean {
    return false
  },

  appendChild(parent: RuneElement, child: RuneElement | RuneText): void {
    if (child.parentNode) {
      const idx = child.parentNode.childNodes.indexOf(child)
      if (idx >= 0) child.parentNode.childNodes.splice(idx, 1)
    }
    child.parentNode = parent
    parent.childNodes.push(child)
    enqueueOp({ op: 'append', parent: parent.nodeId, child: child.nodeId })
  },

  appendChildToContainer(container: Container, child: RuneElement | RuneText): void {
    if (child.parentNode) {
      const idx = child.parentNode.childNodes.indexOf(child)
      if (idx >= 0) child.parentNode.childNodes.splice(idx, 1)
    }
    child.parentNode = container
    container.childNodes.push(child)
    enqueueOp({ op: 'append', parent: container.nodeId, child: child.nodeId })
  },

  insertBefore(parent: RuneElement, child: RuneElement | RuneText, before: RuneElement | RuneText): void {
    if (child.parentNode) {
      const old = child.parentNode.childNodes.indexOf(child)
      if (old >= 0) child.parentNode.childNodes.splice(old, 1)
    }
    const idx = parent.childNodes.indexOf(before)
    child.parentNode = parent
    if (idx >= 0) {
      parent.childNodes.splice(idx, 0, child)
      enqueueOp({ op: 'insertBefore', parent: parent.nodeId, child: child.nodeId, before: before.nodeId })
    } else {
      parent.childNodes.push(child)
      enqueueOp({ op: 'append', parent: parent.nodeId, child: child.nodeId })
    }
  },

  insertInContainerBefore(container: Container, child: RuneElement | RuneText, before: RuneElement | RuneText): void {
    hostConfig.insertBefore(container, child, before)
  },

  removeChild(parent: RuneElement, child: RuneElement | RuneText): void {
    const idx = parent.childNodes.indexOf(child)
    if (idx >= 0) parent.childNodes.splice(idx, 1)
    child.parentNode = null
    enqueueOp({ op: 'remove', parent: parent.nodeId, child: child.nodeId })
  },

  removeChildFromContainer(container: Container, child: RuneElement | RuneText): void {
    hostConfig.removeChild(container, child)
  },

  commitUpdate(instance: RuneElement, _type: string, prevProps: Props, nextProps: Props): void {
    const serialized = serializeProps(instance, nextProps, prevProps)
    enqueueOp({ op: 'props', id: instance.nodeId, props: serialized })
  },

  commitTextUpdate(textInstance: RuneText, _oldText: string, newText: string): void {
    textInstance.textContent = newText
  },

  resetTextContent(instance: RuneElement): void {
    instance.childNodes = []
    enqueueOp({ op: 'clear', id: instance.nodeId })
  },

  clearContainer(container: Container): void {
    container.childNodes = []
    enqueueOp({ op: 'clear', id: container.nodeId })
  },

  hideInstance(instance: RuneElement): void {
    instance.style.setProperty('display', 'none')
  },

  unhideInstance(instance: RuneElement): void {
    instance.style.removeProperty('display')
  },

  hideTextInstance(textInstance: RuneText): void {
    textInstance.textContent = ''
  },

  unhideTextInstance(textInstance: RuneText, text: string): void {
    textInstance.textContent = text
  },

  detachDeletedInstance(): void {},
  commitMount(): void {},

  getCurrentUpdatePriority: () => currentUpdatePriority,
  setCurrentUpdatePriority: (p: number) => {
    currentUpdatePriority = p
  },
  resolveUpdatePriority: () => currentUpdatePriority || DefaultEventPriority,
  maySuspendCommit: () => false,
  preloadInstance: () => true,
  startSuspendingCommit: () => {},
  suspendInstance: () => {},
  waitForCommitToBeReady: () => null,
  NotPendingTransition: null,
  HostTransitionContext: { $$typeof: Symbol.for('react.context'), _currentValue: null, _currentValue2: null, _threadCount: 0 },
  resetFormInstance: () => {},
  requestPostPaintCallback: (cb: (time: number) => void) => {
    Promise.resolve().then(() => cb(performance.now()))
  },
  shouldAttemptEagerTransition: () => false,
  trackSchedulerEvent: () => {},
  resolveEventType: () => null,
  resolveEventTimeStamp: () => performance.now(),
  beforeActiveInstanceBlur: () => {},
  afterActiveInstanceBlur: () => {},
  getInstanceFromNode: () => null,
  prepareScopeUpdate: () => {},
  getInstanceFromScope: () => null,
  bindToConsole: (_methodName: string, args: unknown[]) => () => logError('react', args.map(String).join(' ')),
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export const RuneReconciler = Reconciler(hostConfig as any)
