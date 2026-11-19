import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { host, type AudioEndpointDetails, type AudioFormat, type AudioFormats, type AudioRead, type ExoVoiceSettings, type Peripheral } from '../../lib/host'
import { report } from '../../lib/report'
import { usePageActive } from '../../lib/pageActivity'
import { ChoiceRow, Row, SliderRow, State, SwitchRow } from '../settings/parts'
import { DevicePage, DevicePanel as Panel } from './DevicePage'
import { ApoPanel, GamesPanel, HeadphonePanel, NightPanel, SoundPanel, useEq } from './EqPanels'
import { errorText } from '../../lib/errorWords'

export type AudioLook = 'headphones' | 'iem' | 'headset'
/** What you wear on the jack, as you picked it ("ZiiGaat Lush", in-ear; AutoEq's id for it); null until you do. */
export type Wearing = { name: string; look: AudioLook; id?: string } | null

/**
 * Your audio's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on
 * the left is Exo Mouse's: what you wear, its picture large in its own light, its name and how it's linked, a readout of
 * what Windows plays it through and hears you on, and thanks to AutoEq and Equalizer APO at its foot. Its controls stand
 * in columns beside it, in this order: Exo EQ (Equalizer APO's install while it's missing), which pair it is (one pick:
 * Exo EQ corrects it, and the card's name and picture follow it), the sound, games and Night Shift (devices/EqPanels.tsx).
 * Then what Windows has for each (format, volume, mute), changeable here, and how Exo Voice is set, read every few
 * seconds only while it shows.
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
  const eq = useEq(output?.id)
  const worn = !!output && output.model !== 'speakers' && output.model !== 'monitor'
  const ready = eq.state?.apo.installed === true
  return (
    <DevicePage name={name} status={status} art={art}
      stats={heroStats(output, mic, details)}
      credit="Corrections by AutoEq, sound by Equalizer APO">
      <ApoPanel eq={eq} />
      {worn ? <HeadphonePanel eq={eq} wearing={wearing} onWearing={onWearing} /> : null}
      {ready ? <SoundPanel eq={eq} /> : null}
      {ready ? <GamesPanel eq={eq} /> : null}
      {ready ? <NightPanel eq={eq} /> : null}
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
