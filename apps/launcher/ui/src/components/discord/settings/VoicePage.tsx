import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { host, onHostEvent, type MicHeard, type MicLevel } from '../../../lib/host'
import { setDiscordPrefs, useDiscordPrefs, type DiscordClientSettings, type DiscordKeybind } from '../../../lib/discordPrefs'
import { Menu } from '../../ui/Menu'
import { Slider } from '../../ui/Slider'
import { ChoiceRow, Row, Section, SliderRow, State, SwitchRow } from '../../settings/parts'
import { KeybindRecorder } from './KeybindsPage'
import { LoadError, useClientSettings, useRemembered } from './parts'
import { cameraError } from '../../../lib/goLive'
import { usePageActive } from '../../../lib/pageActivity'
import { devicePicker } from '../../../lib/canUse'
import { ErrorNote } from '../../ui/ErrorNote'

type Audio = Awaited<ReturnType<typeof host.discordAudio>>
type Save = (change: Partial<DiscordClientSettings>) => Promise<string | null>

/** Discord settings > Voice & Video: devices and volumes, the microphone and its dials, attenuation and the camera. */
export function VoicePage() {
  const { settings, save, error, reload } = useClientSettings()
  return (
    <>
      <LoadError error={error} onRetry={reload} />
      <Devices />
      {settings ? <Microphone settings={settings} save={save} /> : null}
      {settings ? <Attenuation settings={settings} save={save} /> : null}
      <Camera />
    </>
  )
}

function Devices() {
  const [audio, setAudio] = useRemembered<Audio>('audio')
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    host.discordAudio({}).then(read => { setAudio(read); setError(null) }).catch(() => setError("Exo couldn't read your microphones and speakers."))
  }, [round, setAudio])
  const save = (change: Parameters<typeof host.discordAudio>[0]) => {
    setError(null)
    void host.discordAudio(change).then(setAudio).catch(() => setError("Exo didn't save that. Try again."))
  }
  if (!audio) return <LoadError error={error} onRetry={() => setRound(r => r + 1)} />
  const device = (side: 'input' | 'output') => {
    const list = side === 'input' ? audio.inputs : audio.outputs
    // None: say so. One or more: keep Default distinct from pinning that device so AudioDevices follows Windows changes.
    if (list.length === 0) return <State>{side === 'input' ? 'No microphone found' : 'No speakers found'}</State>
    const chosen = (side === 'input' ? audio.input : audio.output) ?? ''
    return (
      <Menu label={side === 'input' ? 'Input device' : 'Output device'} anchor={side === 'input' ? '--exo-dc-in' : '--exo-dc-out'} offset={4}
        triggerClassName="exo-shelf-sort-trigger exo-dc-settings-device" trigger={list.find(d => d.id === chosen)?.name ?? 'Default'}
        items={[{ id: '', label: 'Default' }, ...list.map(d => ({ id: d.id, label: d.name }))]} checked={chosen}
        onAction={id => save(side === 'input' ? { input: id } : { output: id })} />
    )
  }
  return (
    <Section title="Devices">
      {error ? <ErrorNote className="exo-dc-settings-note" message={error} /> : null}
      <Row label="Microphone">{device('input')}</Row>
      {audio.inputs.length === 0 ? null : <SliderRow label="Microphone volume" min={0} max={200} value={audio.inputVolume} render={v => `${v}%`} onCommit={v => save({ inputVolume: v })} />}
      <Row label="Speaker">{device('output')}</Row>
      {audio.outputs.length === 0 ? null : <SliderRow label="Speaker volume" min={0} max={200} value={audio.outputVolume} render={v => `${v}%`} onCommit={v => save({ outputVolume: v })} />}
      {/* Off is its mute; the volume stays saved under it, as it was. */}
      <SliderRow label="Soundboard volume" min={0} max={100} value={audio.soundboardMuted ? 0 : audio.soundboardVolume} render={v => v === 0 ? 'Off' : `${v}%`}
        onCommit={v => save(v === 0 ? { soundboardMuted: true } : { soundboardVolume: v, soundboardMuted: false })} />
    </Section>
  )
}

function Microphone({ settings, save }: { settings: DiscordClientSettings; save: Save }) {
  const live = useMicLevel()
  const ptt = settings.keybinds.find(k => k.action === 'PUSH_TO_TALK')
  const setPtt = (keys: number[]) => {
    const others = settings.keybinds.filter(k => k !== ptt)
    const next: DiscordKeybind[] = [{ action: 'PUSH_TO_TALK', keys }, ...others]
    void save({ keybinds: next })
  }
  return (
    <Section title="Microphone">
      <LiveVoice live={live} />
      <HearYourVoice />
      <NoiseSuppression settings={settings} save={save} live={live} />
      <OnlyMyVoice settings={settings} save={save} />
      {settings.noiseSuppression ? null : (
        <>
          <SwitchRow label="Noise suppression for others" checked={settings.othersNoiseSuppression} onChange={value => save({ othersNoiseSuppression: value })} />
          <SwitchRow label="Even out voices" checked={settings.evenVoices} onChange={value => save({ evenVoices: value })} />
          <SwitchRow label="Echo cancellation" checked={settings.echoCancellation} onChange={value => save({ echoCancellation: value })} />
          <Sensitivity settings={settings} save={save} live={live} />
        </>
      )}
      <SwitchRow label="Push to talk" checked={settings.pushToTalk} onChange={value => save({ pushToTalk: value })} />
      {settings.pushToTalk ? (
        <>
          <Row label="Push to talk keys">
            <KeybindRecorder label="Push to talk keys" keys={ptt?.keys ?? []} onChange={setPtt} />
          </Row>
          <SliderRow label="Release delay" min={0} max={2000} step={10} value={settings.pttDelay}
            render={v => (v >= 1000 ? `${(v / 1000).toFixed(2)}s` : `${v} ms`)} onCommit={v => void save({ pttDelay: v })} />
        </>
      ) : null}
    </Section>
  )
}

type Suppression = 'exo' | 'standard' | 'none'

/** Why what's chosen isn't what runs, on the row it concerns. */
const NOT_RUNNING: Partial<Record<MicLevel['stages']['suppression'], string>> = {
  missing: 'Not available',
  mic: 'Your microphone already does this',
  windows: 'Windows is doing this',
}

/** Noise suppression: Exo Voice (Exo's own AI chain), Standard (WebRTC), or the mic as it is. */
function NoiseSuppression({ settings, save, live }: { settings: DiscordClientSettings; save: Save; live: MicLevel | null }) {
  const value: Suppression = !settings.noiseSuppression ? 'none' : settings.noiseSuppressionKind === 'standard' ? 'standard' : 'exo'
  const choices: { value: Suppression; label: string }[] = [
    { value: 'exo', label: 'Exo Voice' },
    { value: 'standard', label: 'Standard' },
    { value: 'none', label: 'None' },
  ]
  return (
    <ChoiceRow label="Noise suppression" value={value} choices={choices} detail={live ? NOT_RUNNING[live.stages.suppression] : undefined}
      onChange={next => void save(next === 'none' ? { noiseSuppression: false } : { noiseSuppression: true, noiseSuppressionKind: next })} />
  )
}

/** "2 min 30 s" of speech, "45 s" under a minute. */
function speechTime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  if (s < 60) return `${s} s`
  return s % 60 ? `${Math.floor(s / 60)} min ${s % 60} s` : `${s / 60} min`
}

/**
 * Only My Voice, with nothing to teach: while it is on, Exo Voice learns the voice by itself from the first 15 seconds of
 * talking (in a call or on the live meter), keeps learning from speech that is clearly the user's, and learns it again if
 * it ever holds the user back. The row says how much it has learned from; Start over deletes the voiceprint, and it is
 * learned again from the next 15 seconds (with Only My Voice off, Forget deletes it and nothing is learned).
 */
function OnlyMyVoice({ settings, save }: { settings: DiscordClientSettings; save: Save }) {
  const learned = settings.myVoiceLearned === true
  const startOver = () => void host.discordMyVoice('forget').then(() => save({})).catch(() => { })
  return (
    <>
      <SwitchRow label="Only my voice" checked={settings.onlyMyVoice} onChange={value => save({ onlyMyVoice: value })} />
      {settings.onlyMyVoice || learned ? (
        <Row label="Your voice">
          <State>{learned ? speechTime(settings.myVoiceSeconds ?? 0) : 'Learning'}</State>
          {learned ? <button type="button" className="exo-set-btn" onClick={startOver}>{settings.onlyMyVoice ? 'Start over' : 'Forget'}</button> : null}
        </Row>
      ) : null}
    </>
  )
}

/** Hear Your Voice: your microphone played back through the chain exactly as the call sends it, live. It ends when the page goes (or Settings closes). */
function HearYourVoice() {
  const [on, setOn] = useState(false)
  const [speakers, setSpeakers] = useState(false)
  useEffect(() => () => { setOn(false); setSpeakers(false); void host.discordMicMonitor(false).catch(() => { }) }, [])
  const toggle = (value: boolean) => {
    void host.discordMicMonitor(value).then(r => { setOn(r.on); setSpeakers(r.on && !r.onHead) }).catch(() => { })
  }
  return (
    <SwitchRow label="Hear your voice" checked={on} onChange={toggle}
      detail={speakers ? 'Speakers echo into the mic: use headphones' : undefined} />
  )
}

/** A level as a meter's fill, -100 to 0 dB across its track. */
const fill = (db: number) => ({ '--fill': Math.max(0, Math.min(100, db + 100)) / 100 }) as CSSProperties

/** What to do about the mic's own sound, when anything. */
function micAdvice(h: MicHeard | undefined): string | undefined {
  if (!h || h.speechDb == null) return undefined
  if (h.clipping) return 'Clipping: turn its gain down'
  if (h.gated) return 'Gated before Exo: turn that gate off'
  if (h.quiet) return 'Quiet: turn its gain up'
  return undefined
}

/**
 * The microphone right now, through the call's own chain: how loud it comes in and what is sent (green while the gate lets
 * the voice through).
 */
function LiveVoice({ live }: { live: MicLevel | null }) {
  return (
    <Row label="Your mic, live" stacked detail={micAdvice(live?.heard)}>
      <div className="exo-dc-live-meters">
        <LiveMeter label="Microphone" db={live?.level ?? -100} />
        <LiveMeter label="Sent" db={live?.after ?? -100} voiced={live?.voiced} />
      </div>
    </Row>
  )
}

function LiveMeter({ label, db, voiced = false }: { label: string; db: number; voiced?: boolean }) {
  return (
    <>
      <span className="exo-dc-tune-note">{label}</span>
      <div className={`exo-dc-settings-meter${voiced ? ' is-voiced' : ''}`} role="meter" aria-label={label} aria-valuemin={-100} aria-valuemax={0} aria-valuenow={Math.round(db)}>
        <span className="exo-dc-settings-meter-track"><span className="exo-dc-settings-meter-level" style={fill(db)} /></span>
      </div>
    </>
  )
}

/** Input Sensitivity: automatic or a threshold, over the level the gate judges (after noise suppression; green while it is open). */
function Sensitivity({ settings, save, live }: { settings: DiscordClientSettings; save: Save; live: MicLevel | null }) {
  const level = live ?? { gateLevel: -100, voiced: false }
  const [threshold, setThreshold] = useState<number | null>(null)
  const shown = threshold ?? settings.sensitivity
  const commit = (value: number) => { setThreshold(null); if (value !== settings.sensitivity) void save({ sensitivity: value }) }
  return (
    <>
      <SwitchRow label="Automatic input sensitivity" checked={settings.autoSensitivity} onChange={value => save({ autoSensitivity: value })} />
      <Row label="Input sensitivity" stacked detail={settings.autoSensitivity ? undefined : `${shown} dB`}>
        <div className={`exo-dc-settings-meter${level.voiced ? ' is-voiced' : ''}`} role="meter" aria-label="Microphone level"
          aria-valuemin={-100} aria-valuemax={0} aria-valuenow={Math.round(level.gateLevel)} aria-valuetext={level.voiced ? 'Speaking' : 'Not speaking'}>
          <span className="exo-dc-settings-meter-track"><span className="exo-dc-settings-meter-level" style={fill(level.gateLevel)} /></span>
          {settings.autoSensitivity ? null : (
            <Slider className="is-bare" min={-100} max={0} value={shown} aria-label="Input sensitivity"
              onChange={event => setThreshold(Number(event.target.value))}
              onPointerUp={event => commit(Number(event.currentTarget.value))} onKeyUp={event => commit(Number(event.currentTarget.value))} />
          )}
        </div>
      </Row>
    </>
  )
}

// The live microphone: the host opens it while this shows and Exo is in use, renewed every 5 s, and stops it when it
// goes, Settings closes or Exo is left (a game in front, Exo minimized). Null until the first level arrives. The host
// sends a level about every 30 ms, sometimes several in one frame: the page draws the latest once a frame.
function useMicLevel() {
  const [level, setLevel] = useState<MicLevel | null>(null)
  const active = usePageActive()
  useEffect(() => {
    if (!active) return
    const renew = () => void host.discordMicTest(true).catch(() => { })
    renew()
    const timer = window.setInterval(renew, 5000)
    let frame = 0
    let latest: MicLevel | null = null
    const off = onHostEvent('discord.micLevel', next => {
      latest = next
      frame ||= requestAnimationFrame(() => { frame = 0; setLevel(latest) })
    })
    return () => { window.clearInterval(timer); cancelAnimationFrame(frame); off(); setLevel(null); void host.discordMicTest(false).catch(() => { }) }
  }, [active])
  return active ? level : null
}

function Attenuation({ settings, save }: { settings: DiscordClientSettings; save: Save }) {
  return (
    <Section title="Attenuation">
      <SliderRow label="Attenuation strength" min={0} max={100} value={settings.attenuation} render={v => `${v}%`} onCommit={v => void save({ attenuation: v })} />
      <SwitchRow label="When I speak" checked={settings.attenuateSelf} disabled={settings.attenuation === 0} onChange={value => save({ attenuateSelf: value })} />
      <SwitchRow label="When others speak" checked={settings.attenuateOthers} disabled={settings.attenuation === 0} onChange={value => save({ attenuateOthers: value })} />
    </Section>
  )
}

/** Camera: which camera calls use, and Test Video to see it. */
function Camera() {
  const prefs = useDiscordPrefs()
  const [cameras, setCameras] = useState<MediaDeviceInfo[]>([])
  const [testing, setTesting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const video = useRef<HTMLVideoElement>(null)
  // The test ends with the page or when Settings closes, never left running unseen.
  useEffect(() => () => setTesting(false), [])
  useEffect(() => {
    navigator.mediaDevices?.enumerateDevices().then(list => setCameras(list.filter(d => d.kind === 'videoinput'))).catch(() => { })
  }, [testing])
  useEffect(() => {
    if (!testing) return
    let stream: MediaStream | null = null
    let live = true
    navigator.mediaDevices.getUserMedia({ video: prefs.cameraId ? { deviceId: { exact: prefs.cameraId } } : true, audio: false }).then(s => {
      if (!live) { s.getTracks().forEach(t => t.stop()); return }
      stream = s
      if (video.current) video.current.srcObject = s
    }).catch((why: unknown) => { if (live) { setError(cameraError(why)); setTesting(false) } })
    return () => { live = false; stream?.getTracks().forEach(t => t.stop()) }
  }, [testing, prefs.cameraId])
  // Before the camera was first allowed, Windows lists cameras without ids or names: only Default can be chosen then.
  const named = cameras.filter(c => c.deviceId)
  return (
    <Section title="Camera">
      {cameras.length === 0 ? <Row label="Camera"><State>No camera found</State></Row>
        : devicePicker(named.length) === 'one' && !prefs.cameraId ? <Row label="Camera"><State>{named[0].label || 'Camera'}</State></Row> : (
        <ChoiceRow<string> label="Camera" detail={named.length === 0 ? 'Test video once to choose' : undefined} value={prefs.cameraId ?? ''}
          onChange={id => setDiscordPrefs({ cameraId: id || null })}
          choices={[{ value: '', label: 'Default' }, ...named.map((c, i) => ({ value: c.deviceId, label: c.label || `Camera ${i + 1}` }))]} />
      )}
      {cameras.length === 0 ? null : (
        <Row label="Test video" error={error}>
          <button type="button" className="exo-set-btn" onClick={() => { setError(null); setTesting(t => !t) }}>{testing ? 'Stop' : 'Test'}</button>
        </Row>
      )}
      {testing ? <video className="exo-dc-settings-video" ref={video} autoPlay muted playsInline /> : null}
    </Section>
  )
}
