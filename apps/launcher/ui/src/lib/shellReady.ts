import { host } from './host'
import { readyOnce } from './readyOnce'

/**
 * The Library or first-run setup is on screen: the host hears it once per page, tried again if the call fails. After an
 * update, that is how the new build tells the installer it started and works (UpdateStartupHealth); the host never reads
 * the page for it. A call the host never hears rolls a working update back.
 */
export const markShellReady: () => void = readyOnce(async () => {
  const answer = await host.shellReady()
  if (!answer?.ok) throw new Error('shell.ready was not taken')
})
