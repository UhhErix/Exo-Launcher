import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import { useSaid } from './useSaid'

type LogsReader = (error: string, area: string) => Promise<{ ok: boolean; text?: string; copied?: boolean }>
let readLogs: LogsReader | null = null

/**
 * Where Copy details gets its report: the app's own host, set once by main.tsx before anything draws. Set, not imported, so
 * the pages every Exo app shares (the Exo Profile's) can say an error without reaching Launcher's host; with none set,
 * an error shows without Copy details.
 */
export function setLogsReader(next: LogsReader): void {
  readLogs = next
}

/**
 * An error as a page says it, with Copy details beside it (DESIGN.md › Controls): Exo's version, the error, where it
 * happened and the log entries that explain it (AppLog.ReportFor), ready to paste into a report. Where is the room it
 * sits in (the nearest data-log-area: Library, Devices, PC, Discord and so on).
 */
export function ErrorNote({ message, className, as: Tag = 'p', role = 'alert', children }: {
  message: string | null | undefined
  className?: string
  as?: 'p' | 'span' | 'div'
  role?: 'alert' | 'status'
  children?: ReactNode
}) {
  return (
    <Tag className={cn('exo-error-note', className)} role={role}>
      <span className="exo-error-note-text">{children ?? message}</span>
      <CopyLogs error={message ?? ''} />
    </Tag>
  )
}

/** The button alone, for an error drawn its own way (a state word, a row's line). */
export function CopyLogs({ error }: { error: string }) {
  const [said, say] = useSaid()
  const reader = readLogs
  if (!reader) return null
  async function copy(reader: LogsReader, button: HTMLButtonElement) {
    const area = button.closest('[data-log-area]')?.getAttribute('data-log-area') ?? ''
    try {
      const result = await reader(error, area)
      if (!result.ok || !result.text) throw new Error('No report')
      // The host puts it on the clipboard itself; the page's own write is the fallback for an older host.
      if (!result.copied) await navigator.clipboard.writeText(result.text)
      say('Copied')
    } catch {
      say('Couldn’t copy')
    }
  }
  return (
    <button type="button" className="exo-set-btn is-compact exo-copy-logs" onClick={event => void copy(reader, event.currentTarget)}>
      {said ?? 'Copy details'}
    </button>
  )
}
