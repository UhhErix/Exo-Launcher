import { createElement, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { allowHover } from '../../lib/hoverGate'
import { answerRailTip, emptyRailTip, endRailTip, railTipRooms, startRailTip, type RailTipState } from '../../lib/railTip'
import { ChevronLeft, ChevronRight, Home, Plus, Shop, Volume } from '../../brand/icons'
import { cdnSized, type DiscordChannel, type DiscordGuild, type DiscordHome, type DiscordRailItem } from '../../lib/discord'
import { initials } from '../ui/artInitial'
import { channelGlyph } from './icons'
import { GuildMenu } from './ServerMenu'
import { Picture } from './presence'

/** Which of the room's places you are in: Home (your conversations, friends and what is happening), the Shop, or a server. */
export type PlaceTab = 'home' | 'shop' | 'servers'

/** The server list as it shows: a drop shows at once and stays while Discord saves it; the host moved its own list too, so home has it after. */
function useServerOrder(home: DiscordHome | null) {
  const [moved, setMoved] = useState<DiscordRailItem[] | null>(null)
  const save = (next: DiscordRailItem[]) => {
    setMoved(next)
    void host.discordUserSettings({ guildFolders: next }).catch(() => null).finally(() => setMoved(current => current === next ? null : current))
  }
  return { rail: moved ?? home?.rail ?? [], save }
}

const count = (n: number) => (n > 99 ? '99+' : String(n))
/** A count as a screen reader says it. */
export const mentionsSaid = (n: number) => `${n} mention${n === 1 ? '' : 's'}`

/**
 * The band across the room's top (DESIGN.md › Rooms › Discord): Home, then your servers in the order Discord keeps them
 * (folders flattened), and Add a server. The Shop stands beside Home. The place you are in is a white pill with its name; the rest
 * are faces with what is new on them. It scrolls sideways when the servers don't fit, with a step button at each end
 * that shows only while there is more that way, so no server is ever cut off unreachable. The Inbox stands at its end.
 */
export function Band({ home, tab, place, open, inbox, onTab, onServer, onAdd, onPeek }: {
  home: DiscordHome | null
  tab: PlaceTab
  place: string
  /** The channel you're in: where the server menu's Invite lands. */
  open: string | null
  inbox: React.ReactNode
  onTab: (tab: PlaceTab) => void
  onServer: (place: string) => void
  onAdd: () => void
  onPeek: (place: string | null) => void
}) {
  const { rail, save } = useServerOrder(home)
  const [menu, setMenu] = useState<{ guild: DiscordGuild; x: number; y: number } | null>(null)
  // The server's menu, asked for from its column's head (Channels.tsx ColumnHead dispatches exo-dc-guild-menu).
  const askedGuilds = home?.guilds
  useEffect(() => {
    const ask = (event: Event) => {
      const { guildId, x, y } = (event as CustomEvent<{ guildId: string; x: number; y: number }>).detail
      const guild = askedGuilds?.find(g => g.id === guildId)
      if (guild) setMenu({ guild, x, y })
    }
    window.addEventListener('exo-dc-guild-menu', ask)
    return () => window.removeEventListener('exo-dc-guild-menu', ask)
  }, [askedGuilds])
  const guilds = useMemo(() => {
    const byId = new Map((home?.guilds ?? []).map(g => [g.id, g]))
    const ordered = rail.flatMap(item => item.guildIds).map(id => byId.get(id)).filter((g): g is DiscordGuild => !!g)
    const seen = new Set(ordered.map(g => g.id))
    return [...ordered, ...(home?.guilds ?? []).filter(g => !seen.has(g.id))]
  }, [home?.guilds, rail])
  const tipCtl = useBandTip(guilds)
  const strip = useStrip(place, guilds.length)
  const go = (to: PlaceTab) => { tipCtl.clicked(null); onTab(to) }
  return (
    <nav className="exo-dc-band" aria-label="Servers" onPointerMove={tipCtl.moved}>
      <button type="button" className="exo-dc-band-step is-back" aria-label="Scroll servers back" hidden={!strip.back} onClick={() => strip.step(-1)}><ChevronLeft size={18} /></button>
      <div ref={strip.box} className={`exo-dc-band-strip${strip.back ? ' has-back' : ''}${strip.next ? ' has-next' : ''}`} onScroll={strip.measure}>
        <HomeTile home={home} chosen={tab === 'home'} onGo={() => go('home')} onPeek={onPeek} />
        <button type="button" className={`exo-dc-band-place${tab === 'shop' ? ' is-chosen' : ''}`} aria-current={tab === 'shop' ? 'true' : undefined} aria-label="Shop"
          onClick={() => go('shop')}>
          <span className="exo-dc-band-face is-glyph"><Shop size={20} /></span>
          {tab === 'shop' ? <span className="exo-dc-band-name">Shop</span> : null}
        </button>
        <span className="exo-dc-band-rule" aria-hidden="true" />
        {guilds.map(guild => (
          <BandServer key={guild.id} guild={guild} current={guild.id === place}
            onGo={() => { tipCtl.clicked(guild.id); onServer(guild.id) }} onPeek={onPeek} onMenu={(x, y) => { tipCtl.clicked(guild.id); setMenu({ guild, x, y }) }}
            tipShow={tipCtl.show} tipHide={tipCtl.hide} />
        ))}
        <button type="button" className="exo-dc-band-place is-add" aria-label="Add a server" onClick={onAdd}>
          <span className="exo-dc-band-face is-glyph"><Plus size={20} /></span>
        </button>
      </div>
      <button type="button" className="exo-dc-band-step is-next" aria-label="Scroll servers on" hidden={!strip.next} onClick={() => strip.step(1)}><ChevronRight size={18} /></button>
      <span className="exo-dc-band-end">{inbox}</span>
      {tipCtl.tip ? (
        <div className={`exo-dc-tip is-band${tipCtl.rooms.length ? ' is-voice' : ''}`} ref={tipCtl.box} style={{ left: tipCtl.left ?? tipCtl.tip.left, top: tipCtl.tip.top }}>
          <span>{tipCtl.tip.label}</span>
          {tipCtl.rooms.length ? <VoiceTip rooms={tipCtl.rooms} /> : null}
        </div>
      ) : null}
      {menu ? <GuildMenu key={menu.guild.id} {...menu} open={menu.guild.id === place ? open : null} rail={rail} onGo={onServer} onMove={save} onClose={() => setMenu(null)} /> : null}
    </nav>
  )
}

/** Home: your conversations, friends and what is happening. Its mark is your conversations' mentions, else a dot while one is unread. */
function HomeTile({ home, chosen, onGo, onPeek }: { home: DiscordHome | null; chosen: boolean; onGo: () => void; onPeek: (place: string | null) => void }) {
  const dms = (home?.dms ?? []).filter(d => !d.muted && !d.request && !d.spam)
  const unread = dms.filter(d => d.unread).length
  const mentions = dms.reduce((sum, d) => sum + d.mentions, 0)
  return (
    <button type="button" className={`exo-dc-band-place${chosen ? ' is-chosen' : ''}`} aria-current={chosen ? 'true' : undefined}
      aria-label={['Home', mentions ? mentionsSaid(mentions) : unread ? `${unread} unread` : null].filter(Boolean).join(', ')}
      onClick={onGo} onPointerEnter={event => { if (allowHover(event)) onPeek('@me') }} onPointerLeave={() => onPeek(null)}>
      <span className="exo-dc-band-face is-glyph"><Home size={20} /></span>
      {chosen ? <span className="exo-dc-band-name">Home</span> : null}
      {mentions ? <span className="exo-dc-badge" aria-hidden="true">{count(mentions)}</span> : unread && !chosen ? <span className="exo-dc-band-dot" aria-hidden="true" /> : null}
    </button>
  )
}

/**
 * The strip's sideways scroll: a wheel turned over it scrolls it sideways, the step buttons move it most of a view, and
 * the server you go to is brought into view.
 */
function useStrip(place: string, servers: number) {
  const box = useRef<HTMLDivElement>(null)
  const [ends, setEnds] = useState({ back: false, next: false })
  const measure = () => {
    const el = box.current
    if (!el) return
    const back = el.scrollLeft > 1
    const next = el.scrollLeft + el.clientWidth < el.scrollWidth - 1
    setEnds(now => now.back === back && now.next === next ? now : { back, next })
  }
  useLayoutEffect(() => {
    const el = box.current
    if (!el) return
    measure()
    const watch = new ResizeObserver(measure)
    watch.observe(el)
    // A wheel's turn moves the strip sideways (a trackpad's own sideways swipe passes through).
    const wheel = (event: WheelEvent) => {
      if (Math.abs(event.deltaX) > Math.abs(event.deltaY) || el.scrollWidth <= el.clientWidth) return
      event.preventDefault()
      el.scrollBy({ left: event.deltaY, behavior: 'auto' })
    }
    el.addEventListener('wheel', wheel, { passive: false })
    return () => { watch.disconnect(); el.removeEventListener('wheel', wheel) }
  }, [servers])
  // The place you are in is kept in view: a server opened from elsewhere (a link, Ctrl+K) never stays off the strip's end.
  useEffect(() => {
    const chosen = box.current?.querySelector<HTMLElement>('[aria-current="true"]')
    chosen?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' })
  }, [place])
  const step = (by: -1 | 1) => {
    const el = box.current
    if (el) el.scrollBy({ left: by * el.clientWidth * 0.8, behavior: 'smooth' })
  }
  return { box, measure, step, ...ends }
}

/** A server in the band: its face, with a white dot when something is new, its mentions, and who is in voice; the server you are in is the pill with its name. */
function BandServer({ guild, current, onGo, onPeek, onMenu, tipShow, tipHide }: {
  guild: DiscordGuild
  current: boolean
  onGo: () => void
  onPeek: (place: string | null) => void
  onMenu: (x: number, y: number) => void
  tipShow: (event: React.PointerEvent<HTMLElement>, label: string, guildId?: string | null) => void
  tipHide: () => void
}) {
  const unread = (guild.unread || guild.mentions > 0) && !guild.muted
  return (
    <button type="button" data-dc-place={guild.id}
      className={`exo-dc-band-place is-server${current ? ' is-chosen' : ''}${guild.muted ? ' is-muted' : ''}`}
      aria-current={current ? 'true' : undefined}
      aria-label={[guild.name, guild.mentions ? mentionsSaid(guild.mentions) : unread ? 'unread' : null, guild.inVoice ? `${guild.inVoice} in voice` : null].filter(Boolean).join(', ')}
      onClick={onGo}
      onPointerEnter={event => { if (!allowHover(event)) return; onPeek(guild.id); if (!current) tipShow(event, guild.name, guild.inVoice ? guild.id : null) }}
      onPointerLeave={() => { onPeek(null); tipHide() }}
      onContextMenu={event => { event.preventDefault(); onMenu(event.clientX, event.clientY) }}>
      <span className="exo-dc-band-face">
        {guild.icon ? <Picture key={guild.icon} src={cdnSized(guild.icon, 96)} size={44} fallback={<span className="exo-dc-initials">{initials(guild.name)}</span>} /> : <span className="exo-dc-initials">{initials(guild.name)}</span>}
      </span>
      {current ? <span className="exo-dc-band-name">{guild.name}</span> : null}
      {guild.inVoice ? <span className="exo-dc-band-voice" aria-hidden="true"><Volume size={11} /></span> : null}
      {guild.mentions && !guild.muted ? <span className="exo-dc-badge" aria-hidden="true">{count(guild.mentions)}</span>
        : unread && !current ? <span className="exo-dc-band-dot" aria-hidden="true" /> : null}
    </button>
  )
}

/** A hovered server's name under its face; one with people in voice adds who is in which of its rooms, once they answer.
    Its state and the races between a read and a click are in lib/railTip.ts (tested). */
function useBandTip(guilds: DiscordGuild[]) {
  const [state, setState] = useState<RailTipState<DiscordChannel>>(emptyRailTip)
  const tip = state.box
  // The face last pressed: its tip stays away until the pointer moves (a room change can re-mount it under the pointer,
  // which would read as a fresh hover of the server you just opened); any other face's tip shows at once.
  const clicked = useRef<string | null | false>(false)
  const show = (event: React.PointerEvent<HTMLElement>, label: string, guildId: string | null = null) => {
    if (clicked.current !== false && clicked.current === event.currentTarget.dataset.dcPlace) return
    const rect = event.currentTarget.getBoundingClientRect()
    setState(current => startRailTip(current, { guildId, label, left: rect.left + rect.width / 2, top: rect.bottom + 8 }))
  }
  // The pointer leaves: the tip ends. A press ends it too.
  const hide = () => setState(current => endRailTip(current))
  const endClicked = (place: string | null) => { clicked.current = place; setState(current => endRailTip(current)) }
  const moved = () => { clicked.current = false }
  const guildId = tip?.guildId ?? null
  const ask = state.ask
  const inVoice = guilds.find(g => g.id === guildId)?.inVoice ?? 0
  useEffect(() => {
    if (!guildId || inVoice <= 0) return
    host.discordChannels(guildId)
      .then(channels => setState(current => answerRailTip(current, ask, guildId, channels.channels.filter(c => c.kind === 'voice' && c.afk !== true && (c.members?.length ?? 0) > 0))))
      .catch(() => null)
  }, [guildId, inVoice, ask])
  const rooms = railTipRooms(state)
  const box = useRef<HTMLDivElement>(null)
  const [left, setLeft] = useState<number | null>(null)
  // The tip is centred under the face and kept inside the window's 8px edges.
  useLayoutEffect(() => {
    const el = box.current
    if (!tip || !el) { setLeft(null); return }
    const half = el.offsetWidth / 2
    setLeft(Math.min(Math.max(tip.left, 8 + half), window.innerWidth - 8 - half))
  }, [tip, rooms.length])
  return { tip, rooms, box, left, show, hide, clicked: endClicked, moved }
}

/** A hovered server's voice rooms in its tip, as Discord's own tooltip lists them: the channel, then its people's pictures stacked overlapping, then how many more. */
function VoiceTip({ rooms }: { rooms: DiscordChannel[] }) {
  return (
    <div className="exo-dc-tip-voice">
      {rooms.map(room => {
        const members = room.members ?? []
        const shown = members.slice(0, 6)
        return (
          <div key={room.id} className="exo-dc-tip-room">
            <div className="exo-dc-tip-channel">{createElement(channelGlyph(room), { size: 14 })}<span>{room.name}</span></div>
            <div className="exo-dc-tip-avatars">
              {shown.map(member => member.avatar
                ? <img key={member.id} className="exo-dc-tip-face" src={cdnSized(member.avatar, 20)} alt="" width={20} height={20} draggable={false} decoding="async" />
                : <span key={member.id} className="exo-dc-tip-face exo-dc-tip-initials" aria-hidden="true">{initials(member.name)}</span>)}
              {members.length > shown.length ? <span className="exo-dc-tip-more-inline">+{members.length - shown.length}</span> : null}
            </div>
          </div>
        )
      })}
    </div>
  )
}
