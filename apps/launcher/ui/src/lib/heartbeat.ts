import { host, isHost } from './host'
import { onPageActivityChanged } from './pageActivity'
import { beatWanted, createBeat } from './heartbeatCore'

// The launcher's hang watchdog (ExoLauncher/Helpers/HangWatchdog.cs) reads one beat a second, and only while Exo is on
// screen and in front. The page beats whenever the host can see it (heartbeatCore.beatWanted), which covers that. A beat is a
// fire-and-forget call that changes nothing on screen. The timer runs only while the page is wanted to beat (heartbeatCore.ts).
let started = false

export function startHeartbeat() {
  if (started || !isHost()) return
  started = true
  const { sync } = createBeat({
    active: () => beatWanted(document.documentElement.dataset),
    send: () => host.heartbeat(document.visibilityState === 'visible'),
    every: (run, ms) => window.setInterval(run, ms),
    stop: timer => window.clearInterval(timer),
  })
  onPageActivityChanged(sync)
  sync()
}
