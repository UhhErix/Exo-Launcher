import { useRef } from 'react'
import { createPortal } from 'react-dom'
import { useDialogFocus } from '../../lib/focusTrap'
import { AccountStep } from '../onboarding/AccountStep'

/**
 * The account gate's sheet (lib/accountPrompt.ts): the same account step as onboarding (create, sign in, reset), with no
 * close button, no Not now, and no dismissal by Escape or a press outside it. It goes when the PC is signed in and its
 * recovery code, if one was issued, was typed back. The step's
 * own offline screen is the one way through without an account, for this run only.
 */
export function AccountSheet({ onOffline, onSignedIn }: { onOffline: () => void; onSignedIn: () => void }) {
  const sheet = useRef<HTMLDivElement>(null)
  useDialogFocus(sheet)
  return createPortal(
    <div className="exo-whats-new-back exo-portal">
      <div ref={sheet} className="exo-whats-new exo-account-sheet" role="dialog" aria-modal="true" aria-label="Exo account" tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <img className="exo-account-sheet-logo" src="logo.svg" alt="" width={56} height={56} draggable={false} />
        <h2 className="exo-whats-new-title">Your Exo account</h2>
        <AccountStep onDone={onSignedIn} onLater={onOffline} />
      </div>
    </div>,
    document.body,
  )
}
