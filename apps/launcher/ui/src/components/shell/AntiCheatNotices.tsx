import { memo, useEffect } from 'react'
import { host, onHostEvent, type HostEventMap } from '../../lib/host'
import { notify } from '../../lib/notify'

type Missing = HostEventMap['anticheat.missing']

function show(event: Missing) {
  const id = `anticheat:${event.gameId}`
  const title = event.name
  notify({
    id, title: `${event.name} went with ${event.removed}`, text: `${event.title} needs it to start.`, sticky: true,
    action: {
      label: 'Repair', primary: true,
      onPress: () => {
        notify({ id, title, text: 'Installing…', progress: null, lasting: true })
        void host.runtimesInstall('missing', [event.dependencyId])
          .catch((e: unknown) => ({ ok: false, message: e instanceof Error ? e.message : null, results: [] }))
          .then(answer => notify(answer.ok
            ? { id, title, text: 'Installed', tone: 'good' }
            : { id, title, text: answer.results?.find(r => r.outcome === 'failed')?.message || answer.message || 'It couldn’t be installed. Try again.', tone: 'bad' }))
      },
    },
  })
}

/**
 * Easy Anti-Cheat taken from the PC with a game another one needs (a store's uninstall runs the anti-cheat's own
 * uninstall, and the service is shared by every copy): said at once in Exo's one stack, with Repair, which runs the other
 * game's own signed setup behind Windows' permission. Play asks the same before that game starts, if this was missed.
 */
export const AntiCheatNotices = memo(function AntiCheatNotices() {
  useEffect(() => onHostEvent('anticheat.missing', show), [])
  return null
})
