import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button } from './ui/Button'
import { ActionButton } from './ui/ConfirmButton'
import { host } from '../lib/host'
import { useSaid } from './ui/useSaid'
import { useGlide } from '../lib/glide'
import { useDismiss } from '../lib/dismiss'

const STORES = ['steam', 'epic', 'gog'] as const
const STORE_LABELS: Record<(typeof STORES)[number], string> = { steam: 'Steam', epic: 'Epic', gog: 'GOG' }
const OPEN_FAILED = 'Exo couldn’t open the redeem page. Try again.'

/**
 * Centered key-activation dialog. It hands the key to the store's own redeem
 * page and closes; LauncherApp listens for `keys.found` so the confirmation
 * still lands after this dialog is gone.
 */
export function ActivateKeySheet({
  open,
  onClose,
  onStatus,
}: {
  open: boolean
  onClose: () => void
  onStatus: (message: string, fail?: boolean) => void
}) {
  const inputRef = useRef<HTMLInputElement>(null)
  const openRef = useRef<HTMLSpanElement>(null)
  const chipGlide = useGlide<HTMLDivElement>()
  const panelRef = useRef<HTMLDivElement>(null)
  const sessionRevision = useRef(0)
  const edited = useRef(false)
  const [key, setKey] = useState('')
  const [store, setStore] = useState<(typeof STORES)[number]>('steam')
  const [copied, sayCopied] = useSaid()
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')

  useEffect(() => {
    const revision = ++sessionRevision.current
    if (!open) return
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setBusy(false)
    setNote('')
    edited.current = false
    setKey('')
    setStore('steam')
    void host.keysPreview().then((preview) => {
      if (revision !== sessionRevision.current || edited.current) return
      if (preview.looksLikeKey && preview.key) {
        setKey(preview.key)
        if (preview.store === 'steam' || preview.store === 'epic' || preview.store === 'gog') {
          setStore(preview.store)
        }
      }
    }).catch(() => {
      // Clipboard detection is optional; manual key entry remains available.
    })
    const focusFrame = window.requestAnimationFrame(() => inputRef.current?.focus())
    return () => {
      sessionRevision.current = revision + 1
      window.cancelAnimationFrame(focusFrame)
      const back = opener?.isConnected ? opener : document.querySelector<HTMLElement>('.exo-library-more')
      back?.focus({ preventScroll: true })
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Tab' && panelRef.current) {
        // Portaled to the end of body: Tab wraps inside instead of falling out behind the library.
        const stops = Array.from(panelRef.current.querySelectorAll<HTMLElement>('input, button:not(:disabled)'))
        const edge = event.shiftKey ? stops[0] : stops[stops.length - 1]
        if (!edge || document.activeElement === edge || !panelRef.current.contains(document.activeElement)) {
          event.preventDefault()
          ;(event.shiftKey ? stops[stops.length - 1] : stops[0])?.focus()
        }
        return
      }
      if (event.key !== 'Escape') return
      event.preventDefault()
      onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])
  useDismiss(panelRef, onClose, { on: open, modal: true })

  if (!open) return null

  const trimmed = key.trim()
  // Open says how it went in itself (ActionButton): its ring while the store's page opens, its check, and then the
  // popover closes with where to finish; a refusal turns it Bad with the reason in the popover's note.
  const activate = async () => {
    setBusy(true)
    setNote('')
    try {
      // Epic's page cannot take the key in its address, so it goes to the clipboard for the paste.
      if (store === 'epic') await navigator.clipboard.writeText(trimmed).catch(() => undefined)
      return await host.keysActivate(trimmed, store)
    } finally {
      setBusy(false)
    }
  }
  const opened = (result: unknown) => {
    onStatus((result as { message?: string | null } | undefined)?.message ?? `Finish in ${STORE_LABELS[store]}.`)
    onClose()
  }

  return createPortal(
    <div className="exo-key-backdrop exo-portal">
      <div ref={panelRef} className="exo-key-popover" role="dialog" aria-modal="true" aria-labelledby="exo-key-popover-title">
        <h2 id="exo-key-popover-title">Activate key</h2>
        <input
          ref={inputRef}
          className="exo-field"
          value={key}
          onChange={(event) => { edited.current = true; setKey(event.target.value) }}
          onKeyDown={(event) => { if (event.key === 'Enter' && trimmed && !busy) { event.preventDefault(); openRef.current?.querySelector<HTMLButtonElement>('.exo-action')?.click() } }}
          placeholder="Product key"
          autoComplete="off"
          spellCheck={false}
          aria-label="Product key"
        />
        <div ref={chipGlide} className="exo-shelf-chips exo-glide" role="group" aria-label="Store">
          {STORES.map((id) => (
            <button
              key={id}
              type="button"
              className="exo-shelf-chip"
              aria-pressed={id === store}
              disabled={busy}
              onClick={() => { edited.current = true; setStore(id) }}
            >
              {STORE_LABELS[id]}
            </button>
          ))}
        </div>
        {/* Only ever a failure (the page didn't open, the copy didn't take), so it is announced as one. */}
        {note ? <p className="exo-key-popover-note" role="alert">{note}</p> : null}
        <div className="exo-key-popover-actions">
          <Button
            type="button"
            size="sm"
            className="exo-set-btn"
            isDisabled={busy || !trimmed}
            onPress={() => {
              void navigator.clipboard.writeText(trimmed)
                .then(() => { setNote(''); sayCopied('Copied') })
                .catch(() => setNote('Select the key and press Ctrl+C to copy it.'))
            }}
          >
            {copied ?? 'Copy key'}
          </Button>
          <Button type="button" size="sm" className="exo-set-btn" onPress={onClose}>
            Cancel
          </Button>
          <span ref={openRef} className="contents">
            <ActionButton className="exo-set-btn is-primary" disabled={!trimmed} context="ActivateKeySheet.keysActivate"
              fallback={OPEN_FAILED} why="none" onFailed={error => setNote(error.text)} onPress={activate} onDone={opened}>
              Open {STORE_LABELS[store]}
            </ActionButton>
          </span>
        </div>
      </div>
    </div>,
    document.body,
  )
}
