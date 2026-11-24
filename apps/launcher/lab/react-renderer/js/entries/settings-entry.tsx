// SPDX-License-Identifier: MIT

import '../reconciler/bootstrap'
import { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { SettingsPanel } from '../../../../ui/src/components/SettingsPanel'
import { host, initHostBridge, type LauncherSettings, type StoreStatus } from '../../../../ui/src/lib/host'

initHostBridge()

function SettingsHarness() {
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  const [stores, setStores] = useState<StoreStatus[]>([])
  const [updateBusy, setUpdateBusy] = useState(false)

  useEffect(() => {
    let alive = true
    void host.getSettings().then(s => { if (alive) setSettings(s) }).catch(() => undefined)
    void host.storesMatrix().then(s => { if (alive) setStores(s) }).catch(() => undefined)
    return () => { alive = false }
  }, [])

  return (
    <div style={{ width: '100%', height: '100%', display: 'flex', flexDirection: 'column', background: '#000000' }}>
      <SettingsPanel
        settings={settings}
        stores={stores}
        updateBusy={updateBusy}
        updateError={null}
        updatePercent={null}
        updateAvailable={false}
        onCheckUpdate={async () => {
          setUpdateBusy(true)
          try {
            return await host.checkUpdate()
          } finally {
            setUpdateBusy(false)
          }
        }}
        onInstallUpdate={async () => {}}
        onSettings={next => setSettings(next)}
        onStores={next => setStores(next)}
        active={true}
        room="general"
      />
    </div>
  )
}

const root = createRoot(document.getElementById('root'))
root.render(<SettingsHarness />)
