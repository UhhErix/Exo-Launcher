// SPDX-License-Identifier: MIT

import '../reconciler/bootstrap'
import { createRoot } from 'react-dom/client'
import { DRIVER_VENDOR_IDS, installExoHid } from '../../../../mouse/src/exo-hid'
import { App } from '../../../../mouse/src/App'
import { MockApp } from '../../../../mouse/src/dev-mock'
import { followHostLook } from '../../../../mouse/src/host-look'

installExoHid(DRIVER_VENDOR_IDS)
followHostLook()

const mode = (globalThis as unknown as { __rune_boot_mode?: string }).__rune_boot_mode ?? 'mock'
const root = createRoot(document.getElementById('root'))
if (mode === 'live') {
  root.render(<App />)
} else {
  root.render(<MockApp />)
}
