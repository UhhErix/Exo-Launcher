import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './tokens.css'
import './styles/glyphs.css'
import './exo-shell.css'
import './styles/rooms.css'
import './styles/hosted-room.css'
import { host, initHostBridge } from './lib/host'
import { installGlobalErrorReporting, setReportSink } from './lib/report'
import { ErrorBoundary } from './components/shell/ErrorBoundary'
import { setLogsReader } from './components/ui/ErrorNote'
import { installFocusKeeper } from './lib/focusKeeper'
import { startHeartbeat } from './lib/heartbeat'
import { installMenuFit } from './lib/menuFit'
import { installInputModality } from './lib/motionPreference'
import { installBrokenImageGuard } from './lib/brokenImages'
import { setCoverBase } from './lib/coverThumb'
import { installHairline } from './lib/hairline'
import App from './App'

// Attach the WebView message listener before any child effect can post RPCs.
// React runs child useEffects first; deferring init to App caused dropped
// settings/library responses on cold start.
initHostBridge()
// Asked first, so the address is known before the library arrives: the host answers it at once.
host.artBase().then(result => setCoverBase(result.covers), () => {})
setReportSink(host.log)
setLogsReader(host.logsFor)
installGlobalErrorReporting()
installFocusKeeper()
installMenuFit()
installInputModality()
installBrokenImageGuard()
installHairline()
// The host bridge is attached above; the page now reports that it is alive to the launcher's hang watchdog.
startHeartbeat()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary label="Exo">
      <App />
    </ErrorBoundary>
  </StrictMode>,
)
