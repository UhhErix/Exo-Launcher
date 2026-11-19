import { useState, type ReactNode } from 'react'
import { Encrypted, Network, Performance, Signal } from '../../brand/icons'
import { host, type NetCheck, type NetSample, type NetworkItemKey, type PcHealth, type PcHealthItem } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { ActionButton } from '../ui/ConfirmButton'
import { errorText } from '../../lib/errorWords'

/** Each item's icon and its key for the host (system.health's network-on and network-off). */
const ITEMS: Record<NetworkItemKey, { icon: ReactNode; item: 'adapter' | 'dns' | 'tcp' }> = {
  'net-adapter': { icon: <Network size={16} />, item: 'adapter' },
  'net-dns': { icon: <Encrypted size={16} />, item: 'dns' },
  'net-tcp': { icon: <Performance size={16} />, item: 'tcp' },
}

const isItem = (item: PcHealthItem): item is PcHealthItem & { key: NetworkItemKey } => item.key in ITEMS

/**
 * Best settings › Network: each of its items with its own switch (on: set now, in one Windows prompt, and kept in Use
 * best settings; off: put back exactly as it was, and left out of Use best settings), then the quick check of the
 * connection, before and after the last press. Use best settings sets every item that's on in one prompt (BestSetup.tsx).
 */
export function NetworkGroup({ health, onHealth }: { health: PcHealth | null; onHealth: (next: PcHealth) => void }) {
  const [busy, setBusy] = useState<NetworkItemKey | null>(null)
  const [error, setError] = useState<{ key: NetworkItemKey | 'check'; text: string } | null>(null)
  const items = health?.items.filter(isItem) ?? []
  if (!health || items.length === 0) return null

  const run = async (key: NetworkItemKey, call: () => Promise<PcHealth>) => {
    if (busy) return
    setBusy(key)
    setError(null)
    try {
      const next = await call()
      onHealth(next)
      if (!next.ok) setError({ key, text: errorText(trimStop(next.message), 'Not changed. Try again.') })
    } catch (e) {
      report(e, `NetworkGroup.${key}`)
      setError({ key, text: errorText(e, 'Not changed. Try again.') })
    }
    setBusy(null)
  }

  return (
    <section className="exo-pc-menu-rows" aria-labelledby="pc-best-network">
      <h3 id="pc-best-network" className="exo-pc-best-group">Network</h3>
      {items.map(item => {
        const id = `pc-best-${item.key}`
        return (
          <Row key={item.key} label={item.label} labelId={id} icon={ITEMS[item.key].icon}
            detail={item.off ? undefined : item.detail}
            error={error?.key === item.key ? error.text : null}>
            {busy === item.key ? <Spinner label={`Changing ${item.label}`} /> : null}
            <Switch checked={!!item.applied} disabled={!!busy} labelledBy={id}
              onChange={on => void run(item.key, () => host.systemNetwork(on, ITEMS[item.key].item))} />
          </Row>
        )
      })}
      <Row label="Connection check" icon={<Signal size={16} />} detail={<CheckWords check={health.check ?? null} />}
        error={error?.key === 'check' ? error.text : null}>
        <ActionButton className="exo-set-btn" disabled={!!busy} why="none" context="NetworkGroup.check" fallback="The connection wasn’t checked. Try again."
          onPress={() => { setError(null); return host.systemHealth('netcheck') }} onFailed={failure => setError({ key: 'check', text: failure.text })}
          onDone={next => onHealth(next as PcHealth)}>Check</ActionButton>
      </Row>
    </section>
  )
}

/** The check in words: before and after the last press with what changed, or the numbers now. */
function CheckWords({ check }: { check: NetCheck | null }) {
  if (!check) return null
  if (!check.before) return <>{sample(check.after)}</>
  return <>Before: {sample(check.before)}. After: {sample(check.after)}. {check.said}</>
}

/** "21 ms ping, 4 ms jitter, 38 ms lookup": only what answered. */
function sample(s: NetSample): string {
  const parts = [
    s.ping != null ? `${s.ping} ms ping` : null,
    s.jitter != null ? `${s.jitter} ms jitter` : null,
    s.lookup != null ? `${s.lookup} ms lookup` : null,
  ].filter(part => part !== null)
  return parts.length ? parts.join(', ') : 'no answer'
}
