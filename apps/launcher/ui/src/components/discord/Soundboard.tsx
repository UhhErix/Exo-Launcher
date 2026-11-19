import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { emojiUrl } from './emojiUrl'
import { Icons } from './icons'
import { useDismiss } from '../../lib/dismiss'
import { Wait, sample, times } from '../ui/Wait'
import { ErrorNote } from '../ui/ErrorNote'
import { Slider } from '../ui/Slider'
import { errorText } from '../../lib/errorWords'

type Sound = { sound_id: string; name: string; volume?: number; emoji_id?: string | null; emoji_name?: string | null; guild_id?: string | null; available?: boolean }
type GuildSounds = { id: string; name: string; items: Sound[] }
type SoundLibrary = { premium: boolean; defaults: Sound[]; guild: Sound[]; guilds: GuildSounds[] }

function SoundTile({ sound, onPlay }: { sound: Sound; onPlay: () => void }) {
  return (
    <button type="button" className="exo-dc-sound" onClick={onPlay} disabled={sound.available === false}>
      {sound.emoji_id ? <img src={emojiUrl(sound.emoji_id, false, 40)} alt="" width={18} height={18} decoding="async" draggable={false} /> : sound.emoji_name ? <span aria-hidden="true">{sound.emoji_name}</span> : null}
      <span className="exo-dc-sound-name">{sound.name}</span>
    </button>
  )
}

function SoundSection({ heading, items, onPlay }: { heading: string; items: Sound[]; onPlay: (sound: Sound) => void }) {
  if (!items.length) return null
  return (
    <section>
      <p className="exo-dc-picker-head">{heading}</p>
      <div className="exo-dc-sound-grid">{items.map(sound => <SoundTile key={sound.sound_id} sound={sound} onPlay={() => onPlay(sound)} />)}</div>
    </section>
  )
}

function SoundVolumeControl({ volume, muted, onCommit, onToggleMute }: {
  volume: number
  muted: boolean
  onCommit: (next: number) => void
  onToggleMute: () => void
}) {
  const [draft, setDraft] = useState<number | null>(null)
  const shown = muted ? 0 : draft ?? volume
  return (
    <div className="exo-dc-soundboard-vol" role="group" aria-label="Local soundboard volume">
      <button type="button" className="exo-dc-icon-btn" aria-label={muted ? 'Unmute soundboard' : 'Mute soundboard'} aria-pressed={muted} onClick={onToggleMute}>
        {muted || shown === 0 ? <Icons.SoundOff size={16} /> : <Icons.Sound size={16} />}
      </button>
      <Slider compact min={0} max={100} value={shown} aria-label="Soundboard volume"
        onChange={e => setDraft(Number(e.target.value))}
        onPointerUp={e => { const v = Number(e.currentTarget.value); setDraft(null); onCommit(v) }}
        onKeyUp={e => { const v = Number(e.currentTarget.value); setDraft(null); onCommit(v) }} />
      <span className="exo-dc-soundboard-vol-pct">{Math.round(shown)}%</span>
    </div>
  )
}

/**
 * Discord's Soundboard in a call: this server's sounds, other servers' sounds with Nitro, and Discord's defaults;
 * pressing one plays it for everyone in the channel (and here at once, as Discord does).
 */
export function SoundboardButton({ guildId, channelId, compact = false }: { guildId: string | null; channelId: string; compact?: boolean }) {
  const [open, setOpen] = useState(false)
  const [sounds, setSounds] = useState<SoundLibrary | null>(null)
  const [query, setQuery] = useState('')
  const [audio, setAudio] = useState({ volume: 100, muted: false })
  const [error, setError] = useState<string | null>(null)
  const btn = useRef<HTMLButtonElement>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, () => setOpen(false), { on: open })

  useEffect(() => {
    if (!open) return
    let live = true
    setError(null)
    host.discordSoundboard('list', { guildId }).then(result => {
      if (!live) return
      if (!result.ok) { setError(errorText(result.message, 'Discord didn’t answer.')); return }
      const items = (result.guild as { items?: Sound[] } | null)?.items ?? []
      const guilds = Array.isArray(result.guilds)
        ? result.guilds.map(g => ({ id: g.id, name: g.name, items: Array.isArray(g.items) ? g.items as Sound[] : [] })).filter(g => g.items.length > 0)
        : []
      setSounds({
        premium: result.premium === true,
        defaults: Array.isArray(result.defaults) ? result.defaults as Sound[] : [],
        guild: items,
        guilds,
      })
    }).catch((err: unknown) => { report(err, 'Soundboard.list'); if (live) setError('Discord didn’t answer.') })
    host.discordAudio({}).then(a => {
      if (live) setAudio({ volume: a.soundboardVolume, muted: a.soundboardMuted })
    }).catch(() => { })
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('keydown', escape)
    return () => { live = false; document.removeEventListener('keydown', escape) }
  }, [open, guildId])

  useLayoutEffect(() => {
    const el = box.current, trigger = btn.current
    if (!open || !compact || !el || !trigger) return
    const at = trigger.getBoundingClientRect()
    Object.assign(el.style, { left: '0px', top: '0px', visibility: 'hidden' })
    const pop = el.getBoundingClientRect()
    const left = Math.max(8, Math.min(at.left, window.innerWidth - pop.width - 8))
    const top = Math.max(8, at.top - 8 - pop.height)
    Object.assign(el.style, { left: `${left}px`, top: `${top}px`, visibility: 'visible' })
  }, [open, compact, sounds, error, query])

  const filtered = useMemo(() => {
    if (!sounds) return null
    const q = query.trim().toLowerCase()
    const keep = (items: Sound[]) => q ? items.filter(s => s.name.toLowerCase().includes(q)) : items
    const guild = keep(sounds.guild)
    const defaults = keep(sounds.defaults)
    const guilds = sounds.guilds.map(g => ({ ...g, items: keep(g.items) })).filter(g => g.items.length > 0)
    const total = guild.length + defaults.length + guilds.reduce((n, g) => n + g.items.length, 0)
    return { guild, defaults, guilds, total }
  }, [sounds, query])

  function play(sound: Sound) {
    setError(null)
    host.discordSoundboard('play', { channelId, soundId: sound.sound_id, volume: sound.volume ?? 1, guildId: sound.guild_id ?? null, emojiId: sound.emoji_id ?? null, emojiName: sound.emoji_name ?? null })
      .then(result => { if (!result.ok) setError(errorText(result.message, 'Discord didn’t play it.')) })
      .catch(() => setError('Discord didn’t play it.'))
  }

  function saveVolume(change: { soundboardVolume?: number; soundboardMuted?: boolean }) {
    setAudio(prev => ({ volume: change.soundboardVolume ?? prev.volume, muted: change.soundboardMuted ?? prev.muted }))
    void host.discordAudio(change).then(a => setAudio({ volume: a.soundboardVolume, muted: a.soundboardMuted })).catch(() => { })
  }

  const popover = open ? (
    <div ref={box} className={`exo-dc-soundboard-pop${compact ? ' is-panel' : ''}`} role="dialog" aria-label="Soundboard">
      <div className="exo-dc-soundboard-head">
        <input type="search" className="exo-dc-find exo-dc-soundboard-search" placeholder="Filter sounds" aria-label="Filter sounds"
          value={query} onChange={e => setQuery(e.target.value)} />
        <SoundVolumeControl volume={audio.volume} muted={audio.muted}
          onCommit={v => saveVolume(v === 0 ? { soundboardMuted: true } : { soundboardVolume: v, soundboardMuted: false })}
          onToggleMute={() => saveVolume({ soundboardMuted: !audio.muted })} />
      </div>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
      {sounds === null && !error ? <Wait label="Loading sounds"><div className="exo-dc-sound-grid">{times(8, i => <span className="exo-dc-sound"><span className="exo-dc-sound-name">{sample(i)}</span></span>)}</div></Wait> : null}
      {filtered ? (
        <>
          <SoundSection heading="This server" items={filtered.guild} onPlay={play} />
          {filtered.guilds.map(g => <SoundSection key={g.id} heading={g.name} items={g.items} onPlay={play} />)}
          <SoundSection heading="Discord" items={filtered.defaults} onPlay={play} />
          {filtered.total === 0 ? <p className="exo-dc-note">{query.trim() ? 'No sounds match that filter.' : 'No sounds available here.'}</p> : null}
        </>
      ) : null}
    </div>
  ) : null

  const room = compact ? document.querySelector('main.exo-dc') : null

  return (
    <span className={`exo-dc-soundboard${compact ? ' is-panel' : ''}`}>
      <button ref={btn} type="button" className={compact ? `exo-dc-icon-btn${open ? ' is-live' : ''}` : `exo-dc-voice-btn${open ? ' is-live' : ''}`}
        aria-label="Soundboard" aria-expanded={open} onClick={() => setOpen(value => !value)}>
        <Icons.Soundboard size={compact ? 18 : 20} />{compact ? null : <span className="exo-dc-voice-word">Soundboard</span>}
      </button>
      {popover && room ? createPortal(popover, room) : popover}
    </span>
  )
}
