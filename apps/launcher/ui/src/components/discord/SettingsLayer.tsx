import { useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useChildRef, useDismiss } from '../../lib/dismiss'

/**
 * A Discord settings panel (yours, a server's, a channel's) floating over the whole Discord room on a dimmed backdrop,
 * whichever part of the room opened it; a press outside the panel closes it.
 */
export function SettingsLayer({ onClose, hidden = false, children }: { onClose: () => void; hidden?: boolean; children: ReactNode }) {
  const back = useRef<HTMLDivElement>(null)
  useDismiss(useChildRef(back), onClose, { modal: true, on: !hidden })
  const layer = (
    <div ref={back} className="exo-dc-settings-backdrop" hidden={hidden}>
      {children}
    </div>
  )
  const room = document.querySelector('main.exo-dc')
  return room ? createPortal(layer, room) : layer
}
