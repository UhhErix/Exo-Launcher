import { memo, useEffect } from 'react'
import { host, onHostEvent, type StoreId, type StoreOutage } from '../../lib/host'
import { notify } from '../../lib/notify'
import { report } from '../../lib/report'

/** The question Play offline asks in place, for a store with its own offline mode to restart into. */
const OFFLINE_RESTART: Partial<Record<StoreId, string>> = {
  steam: 'Restart Steam offline?',
}

/** The outages already said (id and state): one notice each, though the host's list and its event both bring it. */
const said = new Set<string>()

/**
 * Offers the store's own offline mode in a notice (`id` replaces the one there): Play offline asks in place
 * ("Restart Steam offline?"), then restarts the store into it and says how that went; `then` runs once it is on (Play
 * again). False when the store has no offline mode to restart into.
 */
export function offerOffline(store: StoreId, notice: { id: string; title: string; text: string }, then?: () => void): boolean {
  const question = OFFLINE_RESTART[store]
  if (!question) return false
  const { id, title } = notice
  notify({
    ...notice, sticky: true,
    action: {
      label: 'Play offline', primary: true, confirm: question,
      onPress: () => {
        notify({ id, title, text: 'Restarting in offline mode…', progress: null, lasting: true })
        void host.storesPlayOffline(store)
          .catch((e: unknown) => ({ ok: false, message: e instanceof Error ? e.message : null }))
          .then(answer => {
            notify(answer.ok
              ? { id, title, text: 'Offline mode is on', tone: 'good' }
              : { id, title, text: answer.message || 'Could not switch to offline mode. Try again.', tone: 'bad' })
            if (answer.ok) then?.()
          })
      },
    },
  })
  return true
}

function show(outage: StoreOutage) {
  const key = `${outage.id}:${outage.state}`
  if (said.has(key)) return
  said.add(key)
  const id = `outage:${outage.store}`
  if (outage.state === 'down' && outage.canPlayOffline && offerOffline(outage.store, { id, title: outage.title, text: outage.text })) return
  notify({ id, title: outage.title, text: outage.text, tone: outage.state === 'back' ? 'good' : undefined, sticky: outage.state === 'down' })
}

/**
 * A store's servers down, said once per outage in Exo's one stack (DESIGN.md › Notify), with Play offline where the
 * store has its own offline mode to restart into; and once more when the servers are back. Asking for the list starts
 * the host watching.
 */
export const StoreOutageNotices = memo(function StoreOutageNotices() {
  useEffect(() => {
    const off = onHostEvent('stores.outage', show)
    host.storesOutages().then(({ outages }) => outages?.forEach(show)).catch((e: unknown) => report(e, 'StoreOutageNotices.list'))
    return off
  }, [])
  return null
})
