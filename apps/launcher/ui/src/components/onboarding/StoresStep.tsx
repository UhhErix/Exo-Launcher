import { CompanyLogo } from '../../brand/CompanyLogo'
import type { StoreStatus } from '../../lib/host'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { Wait, sample, times } from '../ui/Wait'
import { canConnectStore, signsInInOwnApp, storeState } from '../../lib/stores'
import type { MissingRuntime, StoreCheckState } from './model'

export interface StoresStepProps {
  storeCheckState: StoreCheckState
  onRetryStoreCheck: () => void
  storeRows: StoreStatus[]
  storeBusy: Record<string, boolean | undefined>
  storeErrors: Record<string, string | undefined>
  onStoreAction: (store: StoreStatus) => void
  missingRuntimes: MissingRuntime[]
  runtimeBusy: boolean
  onInstallRuntimes: () => void
  canAddFolder: boolean
  folderBusy: boolean
  onAddFolder: () => void
}

function StoreRow({ store, busy, error, onConnect }: {
  store: StoreStatus
  busy: boolean | undefined
  error: string | undefined
  onConnect: () => void
}) {
  const state = storeState(store)
  const connect = canConnectStore(store)
  const handoff = signsInInOwnApp(store)
  return (
    <Row label={store.displayName} icon={<CompanyLogo company={store.store} size={22} />} error={error}>
      <State on={state.on}>{state.label}</State>
      {connect || handoff ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={onConnect}>
          {busy ? <Spinner label={connect ? 'Signing in' : `Opening ${store.displayName}`} /> : null}{connect ? 'Sign in' : `Open ${store.displayName}`}
        </button>
      ) : null}
    </Row>
  )
}

export function StoresStep({
  storeCheckState,
  onRetryStoreCheck,
  storeRows,
  storeBusy,
  storeErrors,
  onStoreAction,
  missingRuntimes,
  runtimeBusy,
  onInstallRuntimes,
  canAddFolder,
  folderBusy,
  onAddFolder,
}: StoresStepProps) {
  return (
    <div className="exo-set-list">
      {storeCheckState === 'checking' && storeRows.length === 0 ? (
        <Wait label="Looking for stores on this PC">{times(3, i => <Row label={sample(i)} detail={sample(i + 2)} icon={<svg width="22" height="22" />}><span className="exo-set-btn">Connect</span></Row>)}</Wait>
      ) : storeCheckState === 'error' && storeRows.length === 0 ? (
        <Row label="Couldn’t check for store apps">
          <button type="button" className="exo-set-btn" onClick={onRetryStoreCheck}>Try again</button>
        </Row>
      ) : storeRows.length === 0 ? (
        <Row label="No store apps found" />
      ) : storeRows.map(store => (
        <StoreRow
          key={store.store}
          store={store}
          busy={storeBusy[store.store]}
          error={storeErrors[store.store]}
          onConnect={() => onStoreAction(store)}
        />
      ))}
      {missingRuntimes.length > 0 ? (
        <Row label="Game runtimes" detail={missingRuntimes.map(item => item.name).join(', ')}>
          <State>{missingRuntimes.length} missing</State>
          <button type="button" className="exo-set-btn" disabled={runtimeBusy} onClick={onInstallRuntimes}>
            {runtimeBusy ? <Spinner label="Installing game runtimes" /> : null}Install
          </button>
        </Row>
      ) : null}
      {canAddFolder ? (
        <Row label="Games outside a store">
          <button type="button" className="exo-set-btn" disabled={folderBusy} onClick={onAddFolder}>
            {folderBusy ? <Spinner label="Adding a game folder" /> : null}Add a folder
          </button>
        </Row>
      ) : null}
    </div>
  )
}
