import { useEffect, useState, type ComponentType, type ReactNode } from 'react'
import { Board, Cpu, Drive, GraphicsCard, Memory, Monitor, Network, ThisPc as ThisPcIcon, type IconProps, Wifi } from '../../brand/icons'
import { biosVendorHints } from '../../lib/biosHints'
import { host, type DriveInfo, type GpuProfile, type KeepStatus, type PcAbout, type PcHealth } from '../../lib/host'
import { driveName } from '../../lib/partNames'
import { report } from '../../lib/report'
import { cn, trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Meter } from '../ui/Meter'
import { Wait, sample, times } from '../ui/Wait'
import type { PcState } from './PcRoom'

/**
 * One part of the PC: what it is, in a line beside its glyph (its name, `label`, is for screen readers). A drive adds its
 * free space and how full it is. Nothing else sits under a part unless something is wrong with it (`note`, in Warn or Bad).
 */
type Part = {
  key?: string; label: string; icon: ComponentType<IconProps>; value: string | null | undefined; detail?: string | null
  share?: number | null; note?: string | null; tone?: 'is-warn' | 'is-bad' | null
}

const month = (day: string | null | undefined) => day ? new Date(`${day}T00:00:00`).toLocaleDateString(undefined, { month: 'long', year: 'numeric' }) : null
const joined = (parts: (string | null | undefined | false)[], between = ' ') => parts.filter(Boolean).join(between) || null
const dotted = (parts: (string | null | undefined | false)[]) => joined(parts, ' · ')

type Release = NonNullable<PcAbout['windowsRelease']>

/** Windows as the host reads it against Microsoft's newest; an older host's plain words as a release without a newest. */
export function releaseOf(about: PcAbout): Release | null {
  if (about.windowsRelease) return about.windowsRelease
  if (!about.windows) return null
  return { name: about.windows, version: about.windowsVersion, build: about.build ?? '', insider: false }
}

/** "Windows 11 26H2", or "Windows 11 Insider 26300" (an Insider's build is its only name). A newer one is Updates' row. */
const windowsName = (release: Release) =>
  release.insider ? joined([`${release.name} Insider`, release.build]) : joined([release.name, release.version])

/** What this PC is made of, in the parts a player asks after, by name. The rest of each lives where it is acted on. */
function parts(state: PcState, kept: KeepStatus['pc']): Part[] {
  const { about, health } = state
  const release = about ? releaseOf(about) : null
  const pcieWarn = health?.items.find(item => item.key === 'pcie')?.detail
  const cableWarn = health?.items.find(item => item.key === 'gpucable')?.detail
  const gpuWords = keptWords(kept)
  return [
    { label: 'Processor', icon: Cpu, value: about?.processor },
    {
      label: 'Graphics', icon: GraphicsCard, value: cardName(state),
      ...gpuWords,
      ...(!gpuWords.note && pcieWarn ? { note: pcieWarn, tone: 'is-warn' as const } : {}),
    },
    { label: 'Memory', icon: Memory, value: about?.memoryGb ? joined([`${about.memoryGb} GB`, about.memoryType]) : null },
    { label: 'Screen', icon: Monitor, value: screenName(state.profile), note: cableWarn, tone: cableWarn ? 'is-warn' as const : null },
    ...(about ? [board(about), network(about, state.health)] : []),
    { label: 'Windows', icon: ThisPcIcon, value: release ? windowsName(release) : null },
    ...(state.drives?.drives.map(drivePart) ?? []),
  ].filter(part => !!part.value)
}

/** What Exo did for its driver settings since a driver update: put them back by itself, or one waits for the player's OK. */
function keptWords(kept: KeepStatus['pc']): Pick<Part, 'detail' | 'note' | 'tone'> {
  if (kept === 'putBackAfterDriverUpdate') return { detail: 'Settings put back after the driver update' }
  if (kept === 'needsYourOk') return { note: 'A setting needs your OK', tone: 'is-warn' }
  return {}
}

/** The PC's word from what Exo keeps in place (keep.status): read when the room opens. */
function useKept(): KeepStatus['pc'] {
  const [kept, setKept] = useState<KeepStatus['pc']>('none')
  useEffect(() => {
    let live = true
    host.keepStatus().then(status => { if (live) setKept(status.pc) }).catch(e => report(e, 'ThisPc.kept'))
    return () => { live = false }
  }, [])
  return kept
}

/** The card by its name. */
const cardName = ({ profile, about, driver }: PcState) => profile?.gpu || about?.graphics || driver?.card

/** The main screen: its name and its refresh, the one number a player buys it for ("VG248 · 165 Hz"). */
function screenName(profile: GpuProfile | null) {
  const main = profile?.screen
  return dotted([main?.name || (main?.hz ? 'Main screen' : null), main?.hz ? `${main.hz} Hz` : null])
}

/** The board; what anti-cheat asks of it only while it is off (the rest is the board's menu, BoardRows). */
function board(about: PcAbout): Part {
  const wrong = joined([about.secureBoot === false && 'Secure Boot off', about.tpm === 'None' && 'No TPM'], ' · ')
  return { label: 'Motherboard', icon: Board, value: about.motherboard, note: wrong, tone: wrong ? 'is-warn' : null }
}

/** The connection Windows routes through, by its kind and speed; a cable holding a gigabit adapter to 100 Mbps says so under it. */
function network(about: PcAbout, health: PcHealth | null): Part {
  const mbps = about.networkMbps
  return {
    label: 'Network', icon: about.network === 'Wi-Fi' ? Wifi : Network,
    value: dotted([about.network, !mbps ? null : mbps >= 1000 ? `${+(mbps / 1000).toFixed(1)} Gbps` : `${Math.round(mbps)} Mbps`])
      ?? about.networkAdapter?.replace(/\((?:R|TM)\)/g, '').replace(/\s+/g, ' ').trim() ?? null,
    note: health?.items.find(item => item.key === 'netlink')?.detail,
    tone: 'is-warn',
  }
}

const GIB = 1024 ** 3
const bytes = (n: number) => n >= 1000 * GIB ? `${+(n / 1024 ** 4).toFixed(1)} TB` : `${Math.max(1, Math.round(n / GIB))} GB`

/** Its space when the host has read it, its volumes' otherwise (space is read only when asked): "309 GB free of 1.8 TB". */
function space(drive: DriveInfo) {
  const total = drive.space?.total ?? drive.volumes.reduce((sum, v) => sum + v.totalGb, 0) * GIB
  const free = drive.space?.free ?? drive.volumes.reduce((sum, v) => sum + v.freeGb, 0) * GIB
  return total ? { words: `${bytes(free)} free of ${bytes(total)}`, used: 1 - free / total } : null
}

/**
 * A drive: its letters and name, then its free space of its total and how full it is; the worst thing wrong with it
 * under that, in Warn or Bad (its heat only while it runs hot). Its settings are Best settings'.
 */
function drivePart(drive: DriveInfo): Part {
  const letters = drive.volumes.map(volume => volume.letter).join(' ')
  const t = drive.temperature
  const hot = t?.warn ? (t.now >= t.warn ? 'is-bad' : t.now >= t.warn - 5 ? 'is-warn' : null) : null
  // A game that can't use DirectStorage's fast path is the game's own business, not the drive's.
  const wrong = drive.verdicts.filter(verdict => !verdict.key.startsWith('bypass:')).sort((a, b) => +b.bad - +a.bad)[0]
  return {
    key: drive.id, label: letters ? `Drive ${letters}` : 'Drive', icon: Drive,
    value: joined([letters, drive.model ? driveName(drive.model) : drive.kind]),
    detail: space(drive)?.words,
    share: space(drive)?.used,
    note: wrong?.text ?? (hot ? 'Running hot' : null),
    tone: wrong ? (wrong.bad ? 'is-bad' : 'is-warn') : hot,
  }
}

/** A part that opens its own menu: the card (its driver profile), the screen (Display), the board (BIOS and security). */
export type PcPartMenu = 'graphics' | 'display' | 'board'

const MENU_OF: Partial<Record<string, PcPartMenu>> = { Graphics: 'graphics', Screen: 'display', Motherboard: 'board' }

/**
 * PC › This PC, the room's hero: the PC's name large (its maker's model, else the name Windows gives it) with `chips` at
 * the line's right end, then its parts on hairlines, shining until the room has read them. The card, the screen and the
 * board open their menus (`onMenu`).
 */
export function ThisPc({ state, chips, onMenu }: {
  state: PcState
  chips: ReactNode
  onMenu: (menu: PcPartMenu) => void
}) {
  const { about, profile, driver } = state
  const known = parts(state, useKept())
  return (
    <div className="exo-pc-about">
      <div className="exo-pc-about-line">
        <h1 className="exo-pc-about-name">{about?.name || profile?.gpu || driver?.card || 'This PC'}</h1>
        <div className="exo-pc-chips">{chips}</div>
      </div>
      {known.length > 0 ? (
        <ul className="exo-pc-facts">
          {known.map(part => <Fact key={part.key ?? part.label} part={part} menu={MENU_OF[part.label]} onMenu={onMenu} />)}
        </ul>
      ) : (
        <Wait label="Reading this PC">
          <ul className="exo-pc-facts">
            {times(10, i => <li><div className="exo-pc-fact"><svg width="16" height="16" /><span className="exo-pc-fact-words"><span className="exo-pc-fact-value">{sample(i)}</span></span></div></li>)}
          </ul>
        </Wait>
      )}
    </div>
  )
}

/** One part of the readout; the card, the screen and the board are buttons that open their menu, hung from the part itself. */
function Fact({ part, menu, onMenu }: { part: Part; menu?: PcPartMenu; onMenu: (menu: PcPartMenu) => void }) {
  const { label, icon: Icon, value, detail, share, note, tone } = part
  const words = (
    <>
      <Icon size={16} />
      <span className="exo-pc-fact-words">
        <span className="exo-pc-fact-label sr-only">{label}</span>
        <span className="exo-pc-fact-value">{value}</span>
        {detail ? <span className="exo-pc-fact-detail">{detail}</span> : null}
        {share != null ? <Meter share={share} tone={share >= 0.95 ? 'is-warn' : null} /> : null}
        {note ? <span className={cn('exo-pc-fact-detail exo-pc-fact-note', tone)}>{note}</span> : null}
      </span>
    </>
  )
  return (
    <li>
      {menu ? (
        <button type="button" className="exo-pc-fact is-menu" data-pc-menu={menu} aria-haspopup="dialog" onClick={() => onMenu(menu)}>{words}</button>
      ) : <div className="exo-pc-fact">{words}</div>}
    </li>
  )
}

/**
 * The board's menu: its BIOS with a restart into it, Windows' recovery menu, and what anti-cheat and Windows 11 ask of
 * the PC. Each restart asks in place, then Windows' own prompt shows.
 */
export function BoardRows({ about }: { about: PcAbout | null }) {
  const [error, setError] = useState<{ to: 'bios' | 'recovery'; text: string } | null>(null)
  const hints = biosVendorHints(about?.motherboard)
  const go = (to: 'bios' | 'recovery') => {
    setError(null)
    host.biosUpdate(to).then(answer => { if (!answer.ok) setError({ to, text: trimStop(answer.message ?? '') || 'Didn’t restart' }) },
      (e: unknown) => { report(e, 'BoardRows'); setError({ to, text: 'Didn’t restart' }) })
  }
  const restart = (to: 'bios' | 'recovery') => <ConfirmButton className="exo-set-btn" question="Restart now?" onConfirm={() => go(to)}>Restart</ConfirmButton>
  const on = (value: boolean) => <State on={value}>{value ? 'On' : 'Off'}</State>
  return (
    <>
      <Row label="BIOS" detail={about?.bios ? joined([about.bios, month(about.biosDate)], ', ') : undefined} error={error?.to === 'bios' ? error.text : null}>{restart('bios')}</Row>
      <Row label="Recovery" error={error?.to === 'recovery' ? error.text : null}>{restart('recovery')}</Row>
      {about?.secureBoot != null ? <Row label="Secure Boot" detail={about.secureBoot === false && hints ? `BIOS: ${hints.secureBoot}` : undefined}>{on(about.secureBoot)}</Row> : null}
      {about?.tpm ? <Row label="TPM" detail={about.tpm === 'None' && hints ? `BIOS: ${hints.tpm}` : undefined}><State on={about.tpm !== 'None'}>{about.tpm}</State></Row> : null}
      {about?.memoryIntegrity != null ? <Row label="Memory integrity">{on(about.memoryIntegrity)}</Row> : null}
    </>
  )
}
