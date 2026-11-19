import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Search } from '../../brand/icons'
import { host, type AudioEndpointDetails, type AudioFormat, type AudioFormats, type AudioRead, type ExoVoiceSettings, type LucidHeadphone, type Peripheral } from '../../lib/host'
import { report } from '../../lib/report'
import { usePageActive } from '../../lib/pageActivity'
import { LucidAlertRows, LucidAppRows, LucidFollowRow, LucidGameRows, LucidGuessRow, LucidHeadphoneRow, useLucid, type Lucid } from '../settings/LucidSection'
import { ChoiceRow, Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { Wait, sample, times } from '../ui/Wait'
import { DevicePage, DevicePanel as Panel } from './DevicePage'
import { NightShiftRows } from './NightShiftRows'
import { errorText } from '../../lib/errorWords'

export type AudioLook = 'headphones' | 'iem' | 'headset'
/** What you wear on the jack, as you picked it ("Ziigaat Lush", in-ear; Lucid's id for it); null until you do. */
export type Wearing = { name: string; look: AudioLook; id?: string } | null

/** Lucid's kinds of headphone, as its catalog files them, to the picture of each. */
const LUCID_LOOK: Record<string, AudioLook> = { 'In-Ear Monitors': 'iem', 'Earbuds': 'iem', 'Gaming Headsets': 'headset', 'Headphones': 'headphones' }

const wearingOf = (headphone: LucidHeadphone): Wearing =>
  ({ name: `${headphone.brand} ${headphone.model}`.trim(), look: LUCID_LOOK[headphone.category] ?? 'headphones', id: headphone.id })

/**
 * The card follows Lucid: a headphone chosen in Lucid's own window (or before Exo kept Lucid's id) names the card and
 * draws its picture, once Lucid's list says what kind it is.
 */
function useFollowLucid(tunedId: string | null, wearing: Wearing, onWearing: (wearing: Wearing) => void) {
  const shownId = wearing?.id
  useEffect(() => {
    if (!tunedId || tunedId === shownId) return
    let live = true
    host.lucidHeadphones().then(result => {
      const headphone = result.ok ? result.headphones.find(h => h.id === tunedId) : undefined
      if (live && headphone) onWearing(wearingOf(headphone))
    }, (error: unknown) => report(error, 'AudioPage.followLucid', { level: 'warn' }))
    return () => { live = false }
  }, [tunedId, shownId, onWearing])
}

/**
 * Your audio's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on
 * the left is Exo Mouse's: what you wear, its picture large in its own light, its name and how it's linked, a readout of
 * what Windows plays it through and hears you on, and thanks to Lucid's maker at its foot. Its controls stand in columns
 * beside it, in this order: which pair it is and Lucid, then Lucid's modes (Game Mode and Night Shift) and its sounds.
 * With Lucid, which pair it is is one pick: Lucid's EQ is tuned for it, and the card's name and picture follow it. Then
 * what Windows has for each (format, volume, mute), changeable here, and how Exo Voice is set, read every few seconds
 * only while it shows.
 */
export function AudioPage({ name, status, art, output, mic, wearing, onWearing, visible }: {
  name: string
  status: string
  art: ReactNode
  output?: Peripheral
  mic?: Peripheral
  wearing: Wearing
  onWearing: (wearing: Wearing) => void
  visible: boolean
}) {
  const { details, patch } = useAudioDetails(output?.id ?? null, mic?.id ?? null, visible)
  const lucid = useLucid(output?.id)
  const found = lucid.state?.found === true
  const worn = !!output && output.model !== 'speakers' && output.model !== 'monitor'
  return (
    <DevicePage name={name} status={status} art={art}
      stats={heroStats(output, mic, details)}
      credit="Built on Lucid by tekkusai">
      <PairAndLucid lucid={lucid} worn={worn} wearing={wearing} onWearing={onWearing} />
      {found ? <Panel title="Modes"><LucidGameRows lucid={lucid} /><NightShiftRows /></Panel> : null}
      {found ? <Panel title="Sounds and notifications"><LucidAlertRows lucid={lucid} /></Panel> : null}
      {details ? <DetailPanels output={output} mic={mic} details={details} patch={patch} /> : null}
    </DevicePage>
  )
}

/** The hero's figures: what it plays on and hears you on, the format it mixes at, and its volume. */
function heroStats(output: Peripheral | undefined, mic: Peripheral | undefined, details: AudioRead | null) {
  const out = details?.output
  return [
    { label: 'Playing on', value: output?.name ?? null },
    { label: 'Microphone', value: mic?.name ?? null },
    { label: 'Format', value: out?.format ? `${out.format.rate / 1000} kHz` : null },
    { label: 'Volume', value: out?.volume != null ? (out.muted ? 'Muted' : `${out.volume}%`) : null },
  ]
}

/** The name of each is the hero's (Playing on, Microphone). */
function DetailPanels({ output, mic, details, patch }: { output: Peripheral | undefined; mic: Peripheral | undefined; details: AudioRead; patch: Patch }) {
  return (
    <>
      {output && details.output ? <Panel title="Output"><EndpointRows id={output.id} details={details.output} onChange={next => patch('output', next)} /></Panel> : null}
      {mic && details.mic ? <Panel title="Microphone"><EndpointRows id={mic.id} details={details.mic} onChange={next => patch('mic', next)} /></Panel> : null}
      <Panel title="Exo Voice"><VoiceRows voice={details.voice} /></Panel>
    </>
  )
}

/** What Windows has for the output and microphone, and how Exo Voice is set: read when the page shows, then every 3 seconds while it does. */
function useAudioDetails(output: string | null, mic: string | null, visible: boolean) {
  const [details, setDetails] = useState<AudioRead | null>(null)
  // Not read while Exo is hidden or behind a game; it reads again the moment it is back.
  const shown = usePageActive()
  // A read that began before a change knows less than the change: it's dropped, so it never pulls a slider back.
  const changes = useRef(0)
  const patch = useCallback<Patch>((which, next) => {
    changes.current += 1
    setDetails(last => last?.[which] ? { ...last, [which]: { ...last[which], ...next } } : last)
  }, [])
  useEffect(() => {
    if (!visible || !shown) return
    let current = true
    const read = () => {
      const began = changes.current
      return host.audioRead(output, mic).then(next => { if (current && next.ok && began === changes.current) setDetails(next) }, (error: unknown) => report(error, 'AudioPage.details', { level: 'warn' }))
    }
    void read()
    const again = window.setInterval(() => void read(), 3000)
    return () => { current = false; window.clearInterval(again) }
  }, [output, mic, visible, shown])
  return { details, patch }
}

type Patch = (which: 'output' | 'mic', next: Partial<AudioEndpointDetails>) => void

const CHANNELS: Record<number, string> = { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }

/** "Stereo · 48 kHz · 24-bit", as Windows' Sound settings name a format. */
const formatWords = (format: Pick<AudioFormat, 'rate' | 'bits' | 'channels'>) =>
  `${CHANNELS[format.channels] ?? `${format.channels} channels`} · ${format.rate / 1000} kHz · ${format.bits}-bit`

/** The formats the device really takes, asked of it when its page opens and again after a change. */
function useFormats(id: string) {
  const [offered, setOffered] = useState<AudioFormats | null>(null)
  const ask = useCallback(() => host.audioFormats(id).then(setOffered, (error: unknown) => report(error, 'AudioPage.formats', { level: 'warn' })), [id])
  useEffect(() => { void ask() }, [ask])
  return { offered, ask }
}

/** One device's format, volume and mute, each changed on the device itself; what it refuses says so on its row. */
function EndpointRows({ id, details, onChange }: { id: string; details: AudioEndpointDetails; onChange: (next: Partial<AudioEndpointDetails>) => void }) {
  const { offered, ask } = useFormats(id)
  const [volumeError, setVolumeError] = useState<string | null>(null)
  const was = details.volume
  const setVolume = (volume: number) => {
    setVolumeError(null)
    onChange({ volume })
    host.audioSetVolume(id, volume).then(result => {
      if (result.ok) return
      onChange({ volume: was })
      setVolumeError(errorText(result.message, 'Windows didn’t take it. Try again.'))
    }, (error: unknown) => {
      report(error, 'AudioPage.volume')
      onChange({ volume: was })
      setVolumeError('Windows didn’t take it. Try again.')
    })
  }
  const setMute = async (muted: boolean) => {
    const result = await host.audioSetMute(id, muted).catch((error: unknown) => { report(error, 'AudioPage.mute'); return { ok: false, message: null } })
    if (!result.ok) return errorText(result.message, 'Windows didn’t take it. Try again.')
    onChange({ muted })
    return null
  }
  const setFormat = async (key: string) => {
    const result = await host.audioSetFormat(id, key).catch((error: unknown) => { report(error, 'AudioPage.format'); return { ok: false, message: null } })
    await ask()
    if (!result.ok) return errorText(result.message, 'Windows didn’t take that format.')
    const picked = offered?.formats.find(format => format.key === key)
    if (picked) onChange({ format: { rate: picked.rate, bits: picked.bits, channels: picked.channels } })
    return null
  }
  const choices = (offered?.formats ?? []).map(format => ({ value: format.key, label: formatWords(format) }))
  return (
    <>
      {offered?.current && choices.length > 1 ? <ChoiceRow<string> label="Format" value={offered.current} choices={choices} onChange={setFormat} />
        : details.format ? <Row label="Format"><State>{formatWords(details.format)}</State></Row> : null}
      {details.volume != null ? <SliderRow label="Volume" min={0} max={100} value={details.volume} render={value => `${value}%`} error={volumeError} onCommit={setVolume} /> : null}
      {details.muted != null ? <SwitchRow label="Mute" checked={details.muted} onChange={setMute} /> : null}
    </>
  )
}

const NOISE: Record<string, string> = { exo: 'Exo Voice', standard: 'Standard', off: 'Off' }

/** How Exo Voice is set, as Discord's Voice settings have it. */
function VoiceRows({ voice }: { voice: ExoVoiceSettings }) {
  return (
    <>
      <Row label="Noise suppression"><State on={voice.noise !== 'off'}>{voice.custom ? NOISE[voice.noise] ?? voice.noise : 'Exo Voice'}</State></Row>
      <Row label="Talks by"><State>{voice.pushToTalk ? 'Push to talk' : 'Voice activity'}</State></Row>
      {!voice.pushToTalk ? <Row label="Input sensitivity"><State>{voice.sensitivity == null ? 'Automatic' : `${voice.sensitivity} dB`}</State></Row> : null}
      {voice.custom ? <Row label="Voice Focus"><State on={voice.focus}>{voice.focus ? 'On' : 'Off'}</State></Row> : null}
    </>
  )
}

/**
 * The page's first panels: which pair you wear, then Lucid. With Lucid's settings the pick is Lucid's own (its EQ is
 * tuned for it); without them, Exo's picker only names the card. On speakers there is nothing worn, and Lucid's pick
 * stays in its panel as the pair its EQ is tuned for.
 */
function PairAndLucid({ lucid, worn, wearing, onWearing }: { lucid: Lucid; worn: boolean; wearing: Wearing; onWearing: (wearing: Wearing) => void }) {
  const found = lucid.state?.found === true
  const tuned = found && lucid.state?.settings ? lucid.state.settings.headphone : null
  useFollowLucid(tuned?.chosen ? tuned.id : null, wearing, onWearing)
  const pickInLucid = <LucidHeadphoneRow lucid={lucid} label={worn ? 'Your headphones' : 'EQ tuned for'} onPicked={headphone => onWearing(wearingOf(headphone))} />
  return (
    <>
      {worn ? <Panel title="Headphones"><LucidGuessRow lucid={lucid} onPicked={headphone => onWearing(wearingOf(headphone))} />{tuned ? pickInLucid : <HeadphonePicker wearing={wearing} onWearing={onWearing} />}</Panel> : null}
      <Panel title="Lucid">{found ? <><LucidAppRows lucid={lucid} /><LucidFollowRow lucid={lucid} />{worn ? null : pickInLucid}</> : null}</Panel>
    </>
  )
}

/**
 * Which headphones you wear: a search of the models Lucid knows (hundreds of IEMs, headphones and headsets). The field
 * takes the row's button's place and its matches list under the row, across the panel.
 */
function HeadphonePicker({ wearing, onWearing }: { wearing: Wearing; onWearing: (wearing: Wearing) => void }) {
  const [list, setList] = useState<LucidHeadphone[] | null>(null)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  useEffect(() => {
    if (!open || list) return
    host.lucidHeadphones().then(result => setList(result.ok ? result.headphones : []), (error: unknown) => {
      report(error, 'AudioPage.headphones', { level: 'warn' })
      setList([])
    })
  }, [open, list])
  const words = query.trim().toLowerCase()
  const hits = (list ?? []).filter(headphone => !words || `${headphone.brand} ${headphone.model}`.toLowerCase().includes(words)).slice(0, 5)
  const pick = (headphone: LucidHeadphone) => {
    onWearing(wearingOf(headphone))
    setOpen(false)
    setQuery('')
  }
  return (
    <>
      <Row label="Your headphones" detail={wearing?.name}>
        {open ? (
          <label className="exo-field exo-devices-pick-field">
            <Search size={14} />
            <input type="search" value={query} autoFocus placeholder="Ziigaat Lush" aria-label="Search headphones" onChange={event => setQuery(event.target.value)}
              onKeyDown={event => { if (event.key === 'Escape') setOpen(false) }} />
          </label>
        ) : (
          <button type="button" className="exo-set-btn" onClick={() => setOpen(true)}>{wearing ? 'Change' : 'Pick'}</button>
        )}
      </Row>
      {open ? (
        <ul className="exo-devices-pick-list" aria-label="Headphones">
          {list === null ? <li><Wait label="Reading Lucid's list">{times(5, i => <button type="button"><strong>{sample(i)}</strong><span>In-ear</span></button>)}</Wait></li>
            : hits.length === 0 ? <li className="exo-devices-pick-wait">No match</li>
            : hits.map(headphone => (
              <li key={headphone.id}>
                <button type="button" onClick={() => pick(headphone)}><strong>{headphone.brand} {headphone.model}</strong><span>{headphone.category}</span></button>
              </li>
            ))}
        </ul>
      ) : null}
    </>
  )
}
