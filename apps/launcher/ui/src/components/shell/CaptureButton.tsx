import { Capture, Gallery, History, Notes, Plus, Record, Scissors } from '../../brand/icons'
import { host } from '../../lib/host'
import { clock, keysLabel, openCaptures, useCapture } from '../../lib/capture'
import { openNotes } from '../../lib/notes'
import { report } from '../../lib/report'
import { usePageClock } from '../../lib/pageActivity'
import { reportFailure } from './useActionStatus'
import { Menu, type MenuItem } from '../ui/Menu'
import { errorText } from '../../lib/errorWords'

type Action = 'saveReplay' | 'record' | 'snip' | 'note' | 'captures' | 'notes'

const sinceStart = () => performance.now()

/** While recording: the time so far from the add-on's last report; it stops ticking while Exo is minimized. */
function RecordingClock({ ms, at }: { ms: number; at: number }) {
  const now = usePageClock(500, sinceStart)
  return <>{clock(ms + Math.max(0, now - at))}</>
}

/**
 * The title bar's Capture button, beside the mini player: a quiet green dot while
 * instant replay holds the last seconds; Save replay, Record and Snip, each with its
 * keys, then New note and Captures. Opening notes and settings are the sheet's. While recording
 * it is a red timer pill that stops the recording. Without Exo Capture the menu is New note and Notes.
 */
export function CaptureButton() {
  const { state, settings, at } = useCapture()
  // Until Exo answers, an empty slot of the button's size: the Bar centers its Rooms between its ends, so a button that
  // arrives late moved them. Notes needs no add-on.
  if (!state) return <span className="exo-capture-btn" aria-hidden="true" inert />
  const present = state.present

  async function run(action: Action) {
    if (action === 'captures') { openCaptures(); return }
    if (action === 'note' || action === 'notes') { openNotes({ fresh: action === 'note' }); return }
    try {
      const result = await host.captureAct(action)
      if (!result.ok) {
        report(new Error(result.message ?? 'Capture failed.'), 'capture.act', { level: 'warn' })
        reportFailure(errorText(result.message, 'Exo Capture didn’t answer. Try again.'))
      }
    } catch (error) {
      report(error, 'capture.act', { level: 'warn' })
      reportFailure(errorText(error, 'Exo Capture didn’t answer. Try again.'))
    }
  }

  if (present && state.recording) {
    return (
      <button type="button" className="exo-capture-rec" onClick={() => void run('record')} aria-label="Stop recording">
        <span className="exo-capture-rec-dot" aria-hidden />
        <RecordingClock ms={state.recordingMs} at={at} />
      </button>
    )
  }

  // Save replay needs instant replay holding the last seconds. The snip freezes the screen as it is, so the menu is gone
  // first (instant).
  const hint = (keys: string | undefined) => keys ? keysLabel(keys) : undefined
  const items: MenuItem[] = present ? [
    { id: 'saveReplay', label: 'Save replay', hint: hint(settings?.saveReplayKeys), icon: <History size={16} />, disabled: !state.buffering },
    { id: 'record', label: 'Record', hint: hint(settings?.recordKeys), icon: <Record size={16} /> },
    { id: 'snip', label: 'Snip', hint: hint(settings?.snipKeys), icon: <Scissors size={16} />, instant: true },
    { id: 'note', label: 'New note', icon: <Plus size={16} /> },
    { id: 'captures', label: 'Captures', icon: <Gallery size={16} /> },
  ] : [
    { id: 'note', label: 'New note', icon: <Plus size={16} /> },
    { id: 'notes', label: 'Notes', icon: <Notes size={16} /> },
  ]
  return (
    <Menu label={state.buffering ? 'Capture, instant replay on' : 'Capture'} anchor="--exo-capture" offset={8}
      triggerClassName="exo-capture-btn" className="exo-capture-menu" items={items} onAction={action => void run(action as Action)}
      trigger={<><Capture size={16} />{state.buffering ? <span className="exo-capture-live" aria-hidden /> : null}</>} />
  )
}
