import { useRef } from 'react'
import { createPortal } from 'react-dom'
import type { WhatsNewRelease } from '../../lib/whatsNew'
import { useDismiss } from '../../lib/dismiss'
import { focusWhenActive, useDialogFocus } from '../../lib/focusTrap'
import { Progress, ProgressWords } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { updateWords, type AppUpdate } from './useAppUpdate'
import { ErrorNote } from '../ui/ErrorNote'
import { useHoldWindow, WindowButtons } from './HoldWindow'

/**
 * Changelog: a sheet of large glass over the dimmed window, its items one under another (an icon in
 * a soft circle, a title, one line on what it does for you), then Continue. It holds the window until Continue (Escape
 * is Continue too; a press on the dim does nothing): nothing under it scrolls or takes a key (HoldWindow). Releases
 * after the first follow under their own small heading. While an update with notes is offered, "Coming in Exo
 * <version>" leads, with Install update beside Continue. Opened after an update (LauncherApp) and from Settings › About.
 */
export function WhatsNewSheet({ releases, update, onClose }: { releases: WhatsNewRelease[] | null; update: AppUpdate; onClose: () => void }) {
  const upcoming = update.upcoming && (update.available || update.busy) ? update.upcoming : null
  return releases && (releases.length || upcoming) ? <Sheet releases={releases} upcoming={upcoming} update={update} onClose={onClose} /> : null
}

function Sheet({ releases, upcoming, update, onClose }: { releases: WhatsNewRelease[]; upcoming: WhatsNewRelease | null; update: AppUpdate; onClose: () => void }) {
  const sheet = useRef<HTMLDivElement>(null)
  // A layer of its own that a press outside never closes, so a sheet under it (Settings) isn't closed by one either.
  useDismiss(sheet, null, { modal: true })
  useDialogFocus(sheet)
  useHoldWindow(sheet, true, onClose)

  const sections = upcoming ? [upcoming, ...releases] : releases
  const starts = sections.map((_, index) => sections.slice(0, index).reduce((count, release) => count + release.items.length, 0))
  return createPortal(
    <div className="exo-whats-new-back exo-portal">
      <WindowButtons />
      <div ref={sheet} className="exo-whats-new" role="dialog" aria-modal="true" aria-labelledby="exo-whats-new-title" tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <h2 id="exo-whats-new-title">{upcoming ? `Coming in Exo ${upcoming.version}` : 'Changelog'}</h2>
        <div className="exo-whats-new-list">
          {sections.map((release, index) => (
            <section key={release.version} className="exo-whats-new-release">
              {(index > 0 || !upcoming) && <h3>{release.version}</h3>}
              <ul>
                {release.items.map(({ icon: Icon, title, detail }, item) => (
                  <li key={title} style={{ animationDelay: `${Math.min(starts[index] + item, 7) * 24}ms` }}>
                    <span className="exo-whats-new-icon"><Icon size={20} /></span>
                    <span className="exo-whats-new-words">
                      <strong>{title}</strong>
                      {detail ? <span>{detail}</span> : null}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
        {upcoming && update.error && <ErrorNote className="exo-whats-new-error" message={update.error} />}
        <div className="exo-whats-new-foot">
          <button type="button" className={`exo-set-btn${upcoming ? '' : ' is-primary'}`} data-autofocus onClick={onClose}>Continue</button>
          {upcoming && (
            <button type="button" className="exo-set-btn is-primary" disabled={update.busy} onClick={() => void update.install()}>
              {/* How far it is, the update's notice says with its bar; the button only that it is under way. */}
              {update.busy ? <Spinner label={update.status} /> : null}
              {update.busy ? update.status : 'Install update'}
            </button>
          )}
        </div>
      </div>
    </div>,
    document.body,
  )
}

/**
 * Update on open (DESIGN.md › Chrome › Update on open): the launch's check found a newer release, so as the window first
 * opens a small sheet in the Changelog's look holds it: "Update available", "Exo Launcher <version>", Update now. No list of
 * changes: those are the Changelog's, after the restart. Update now downloads ("Downloading · 54%" over the Exo bar where
 * the button was), installs ("Installing", the bar gliding) and restarts into the new version. A failure says why in red,
 * with Not now and Try again; Escape is Not now then, and only then. Not now lasts this launch. Never over the boot mark.
 */
export function UpdateSheet({ update, shown }: { update: AppUpdate; shown: boolean }) {
  return update.sheet && shown ? <UpdateCard update={update} /> : null
}

function UpdateCard({ update }: { update: AppUpdate }) {
  const sheet = useRef<HTMLDivElement>(null)
  useDismiss(sheet, null, { modal: true })
  useDialogFocus(sheet)
  useHoldWindow(sheet, true, update.notNow && !update.busy ? update.setAside : null)
  const failed = !!update.error && !update.busy
  return createPortal(
    <div className="exo-whats-new-back exo-portal">
      <WindowButtons />
      <div ref={sheet} className="exo-whats-new is-update" role="alertdialog" aria-modal="true" aria-labelledby="exo-update-title" aria-describedby="exo-update-version" tabIndex={-1}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <div className="exo-update-head">
          <h2 id="exo-update-title">Update available</h2>
          <p id="exo-update-version">Exo Launcher {update.latest}</p>
        </div>
        {failed && <ErrorNote className="exo-whats-new-error" message={update.error} />}
        {update.busy ? <UpdateProgress update={update} /> : (
          <div className="exo-whats-new-foot">
            {update.notNow && <button type="button" className="exo-set-btn" onClick={update.setAside}>Not now</button>}
            <button key={failed ? 'again' : 'now'} ref={focusWhenActive} type="button" className="exo-set-btn is-primary" data-autofocus onClick={() => void update.install()}>
              {failed ? 'Try again' : 'Update now'}
            </button>
          </div>
        )}
      </div>
    </div>,
    document.body,
  )
}

/** The update going in, where the buttons were: what and how far over the Exo bar, measured while it downloads. */
function UpdateProgress({ update }: { update: AppUpdate }) {
  const { words, share } = updateWords(update.status, update.percent)
  return (
    <div className="exo-update-progress" role="status">
      <span><ProgressWords words={words} value={share} /></span>
      <Progress size="large" value={share} label={words} />
    </div>
  )
}
