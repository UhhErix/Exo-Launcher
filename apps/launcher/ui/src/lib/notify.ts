import { useEffect, useRef, useSyncExternalStore, type ReactNode } from 'react'

/** `confirm`: the button asks it in place first ("Restart Steam offline?"), and runs on its second press. */
export type NoticeAction = { label: string; onPress: () => void; primary?: boolean; danger?: boolean; busy?: boolean; disabled?: boolean; confirm?: string }

/**
 * One notification (DESIGN.md › Notify): what finished or happened unattended, said once, in the stack under the Bar:
 * a download, an update, a missing runtime, a Discord message, an undoable removal. A press's own result shows at the
 * press (its ring, its row's state or error line, a Copy's "Copied"), never here, unless its control went away with it
 * (a menu row). A question asks in place, on the control pressed (ConfirmButton.tsx), a notice's own button included
 * (NoticeAction `confirm`). Its shape is fixed: a title, one short line, at most one action.
 */
export type Notice = {
  /** The same id replaces that notice where it is (a conversation's newer message, an update's next step). */
  id?: string
  /** Who or what, in bold (a person, "Exo 3.15.5"); a notice of one sentence has none. */
  title?: string
  text: string
  /** Beside the title, quieter: where it happened (#channel · Server). */
  where?: string
  tone?: 'good' | 'bad'
  /** A picture of its own (a person's face); its tone's glyph otherwise. */
  icon?: ReactNode
  /** The one thing to do about it (Install, Open, Undo), at the right. */
  action?: NoticeAction
  /** A wait it reports, along its foot: its share done (0..1), null while unknown. Its words then end in the live percent. */
  progress?: number | null
  /** Waits for Dismiss. Failures, ones with a wait and ones with an action do; the rest go after `stayFor`. */
  sticky?: boolean
  /** How many times it came ("3 new"). */
  count?: number
  /** Pressing it (not its button) opens what it is about, and it goes. */
  onOpen?: () => void
  /** The user dismissed it (its ×, a swipe, Escape). */
  onDismiss?: () => void
  /** It can't be dismissed (an update installing): no ×. */
  lasting?: boolean
}

export type ShownNotice = Notice & { id: string; key: number; at: number; leaving: boolean }

/** The most on screen at once. Further ones wait their turn (a passing one without a button is moved out by a newer arrival; an Undo is not), never a wall. */
const MAX = 3
/** The most waiting; the oldest passing one goes first. */
const QUEUE_MAX = 8
/** The exit's length in dialog.css. */
const LEAVE_MS = 200
const STAY_MIN_MS = 4000
const STAY_MAX_MS = 10000
/** With a button to decide on (Undo), time to read it and reach for it. */
const STAY_ACTION_MS = 7000

/** How long a passing notice stays: the time to notice it plus its words at about 20 a second; 4 s at least, 10 s at most. */
export function stayFor(notice: Pick<Notice, 'title' | 'text' | 'where' | 'action'>): number {
  const chars = (notice.title?.length ?? 0) + (notice.where?.length ?? 0) + notice.text.length
  return Math.min(STAY_MAX_MS, Math.max(notice.action ? STAY_ACTION_MS : STAY_MIN_MS, 2000 + chars * 50))
}

type Waiting = Notice & { id: string; queuedAt: number }

let notices: ShownNotice[] = []
let queue: Waiting[] = []
let keys = 0
const listeners = new Set<() => void>()

function emit() {
  for (const listener of listeners) listener()
}

/** Whether a notice waits for Dismiss. */
export function isSticky(notice: Notice): boolean {
  return notice.sticky ?? (notice.tone === 'bad' || Boolean(notice.action) || notice.progress !== undefined)
}

const liveCount = () => notices.filter(shown => !shown.leaving).length

/** Shows a notice, or puts it in place of the one with its id; its id, to dismiss it with. A full stack makes the arrival wait. */
export function notify(notice: Notice): string {
  const id = notice.id ?? `notice:${notice.title ?? ''}:${notice.text}`
  const same = notices.find(shown => shown.id === id && !shown.leaving)
  const waiting = queue.findIndex(item => item.id === id)
  if (same) {
    notices = notices.map(shown => shown === same ? { ...notice, id, key: same.key, at: Date.now(), leaving: false } : shown)
  } else if (waiting >= 0) {
    queue = queue.map((item, index) => index === waiting ? { ...notice, id, queuedAt: item.queuedAt } : item)
  } else {
    if (liveCount() >= MAX) {
      const oldest = [...notices].reverse().find(shown => !shown.leaving && !isSticky(shown) && !shown.action)
      if (oldest) notices = notices.map(shown => shown === oldest ? leave(shown) : shown)
    }
    if (liveCount() >= MAX) {
      queue = [...queue, { ...notice, id, queuedAt: Date.now() }]
      if (queue.length > QUEUE_MAX) {
        const stale = queue.findIndex(item => !isSticky(item))
        queue = queue.filter((_, index) => index !== (stale >= 0 ? stale : 0))
      }
    } else {
      show({ ...notice, id })
    }
  }
  emit()
  return id
}

function show(notice: Notice & { id: string }) {
  notices = [{ ...notice, key: ++keys, at: Date.now(), leaving: false }, ...notices]
}

/** Lets waiting notices into the room that opened; one that waited past its own time (a passing one) is no longer news. */
function promote() {
  while (queue.length && liveCount() < MAX) {
    const [next, ...rest] = queue
    queue = rest
    if (isSticky(next) || Date.now() - next.queuedAt < stayFor(next)) show(next)
  }
}

/** Takes a notice away, with its exit; nothing when it's already gone. */
export function dismissNotice(id: string) {
  const waiting = queue.length
  queue = queue.filter(item => item.id !== id)
  if (notices.some(shown => shown.id === id && !shown.leaving)) {
    notices = notices.map(shown => shown.id === id && !shown.leaving ? leave(shown) : shown)
    promote()
    emit()
  } else if (queue.length !== waiting) {
    emit()
  }
}

/** The notice on screen with this id. */
export function findNotice(id: string): ShownNotice | undefined {
  return notices.find(shown => shown.id === id && !shown.leaving)
}

function leave(shown: ShownNotice): ShownNotice {
  const left = { ...shown, leaving: true }
  window.setTimeout(() => {
    notices = notices.filter(other => other !== left)
    emit()
  }, LEAVE_MS)
  return left
}

/** Every notice gets its full time again (after the pointer or focus held the stack, or Exo came back to the front). */
export function renewNotices() {
  const now = Date.now()
  notices = notices.map(shown => shown.leaving ? shown : { ...shown, at: now })
  emit()
}

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

export function useNotices(): ShownNotice[] {
  return useSyncExternalStore(subscribe, () => notices)
}

/**
 * A notice that state holds (an update, a missing runtime, a failed download): shown while `notice` is given, gone
 * when it isn't or the caller unmounts. It waits for Dismiss unless it says otherwise, and its button always calls the
 * latest handler, so a parent's render doesn't show it again.
 */
export function useNotice(id: string, notice: Omit<Notice, 'id'> | null | false | undefined) {
  const latest = useRef(notice || null)
  useEffect(() => { latest.current = notice || null })
  const key = notice ? JSON.stringify({
    ...notice, icon: undefined,
    action: notice.action && [notice.action.label, notice.action.primary, notice.action.danger, notice.action.busy, notice.action.disabled],
    onOpen: Boolean(notice.onOpen), onDismiss: Boolean(notice.onDismiss),
  }) : null
  useEffect(() => {
    const shown = latest.current
    if (!key || !shown) {
      dismissNotice(id)
      return
    }
    notify({
      ...shown, id, sticky: shown.sticky ?? true,
      action: shown.action && { ...shown.action, onPress: () => latest.current?.action?.onPress() },
      onOpen: shown.onOpen && (() => latest.current?.onOpen?.()),
      onDismiss: () => latest.current?.onDismiss?.(),
    })
  }, [id, key])
  useEffect(() => () => dismissNotice(id), [id])
}
