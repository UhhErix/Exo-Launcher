import type { CSSProperties } from 'react'
import { CompanyLogo } from './CompanyLogo'
import { GLYPHS, lineAt } from './glyphs'

export { CHECK_PATH } from './glyphs'

/** `filled`: the glyph solid, for a toggle that is on (a starred capture, a liked song). `current`: the glyph solid
    too, for the place you are (the current room). */
export type IconProps = { size?: number; className?: string; filled?: boolean; current?: boolean }

type Glyph = keyof typeof GLYPHS

/*
 * Lucide (lucide.dev, ISC), one family everywhere: Exo, Exo Mouse, setup and first run, the drawn-in checks too. Brand
 * marks (stores, Discord, Spotify, Apple Music, YouTube Music) stay their own. scripts/lucide-map.mjs names the Lucide icon
 * for each name here; scripts/icons.mjs writes them into glyphs.ts.
 * - Lucide's outline icons on the 24 grid with round ends and joins, a line that follows its size (lineAt).
 * - A toggle that is on (liked, starred, pinned) and the transport are the outline filled; the current room keeps its outline.
 * - One glyph per meaning: two things that mean different things never share an icon, anywhere in Exo (scripts/icons.mjs
 *   stops on two that do).
 */

const classes = (name: string, filled: boolean | undefined, className: string | undefined, current?: boolean) =>
  ['exo-glyph', 'is-' + name, filled && 'is-filled', current && 'is-current', className].filter(Boolean).join(' ')

const block: CSSProperties = { display: 'block', flexShrink: 0 }

/** The glyph's solid form, where it has one. */
const solid = (glyph: Glyph) => (`${glyph}-on` in GLYPHS ? `${glyph}-on` : glyph) as Glyph

function Svg({ glyph, size, line = lineAt(size), className, style = block }: { glyph: Glyph; size: number; line?: number; className?: string; style?: CSSProperties }) {
  const [markup, solid] = GLYPHS[glyph]
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill={solid ? 'currentColor' : 'none'} stroke="currentColor"
      strokeWidth={line} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" className={className} style={style}
      dangerouslySetInnerHTML={{ __html: markup }} />
  )
}

function icon(name: Glyph) {
  function ExoIcon({ size = 16, className, filled, current }: IconProps) {
    return <Svg glyph={filled || current ? solid(name) : name} size={size} className={classes(name, filled, className, current)} />
  }
  ExoIcon.displayName = name
  return ExoIcon
}

// The glyph cleared in a circle at its corner, where the badge sits.
const corner = `url("data:image/svg+xml,${encodeURIComponent("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' fill='white'/><circle cx='19.5' cy='4.5' r='6.5' fill='black'/></svg>")}")`

/** A glyph with a small solid badge at the top right, where every badge sits (a private channel's lock), the glyph cleared under it. */
function marked(name: string, glyph: Glyph, badge: Glyph) {
  function ExoIcon({ size = 16, className }: IconProps) {
    return (
      <span aria-hidden="true" className={classes(name, false, className)} style={{ ...block, position: 'relative', width: size, height: size }}>
        <Svg glyph={glyph} size={size} style={{ position: 'absolute', inset: 0, maskImage: corner, maskSize: '100% 100%' }} />
        {/* At half the size, twice the line on its grid: the badge's shackle draws as heavy as the glyph beside it. */}
        <Svg glyph={badge} size={size * 0.5} line={lineAt(size) * 2} style={{ position: 'absolute', right: -size * 0.06, top: -size * 0.06 }} />
      </span>
    )
  }
  ExoIcon.displayName = name
  return ExoIcon
}

function brand(name: string) {
  return function Glyph({ size = 16, className }: IconProps) {
    return <CompanyLogo company={name} size={size} className={['exo-glyph', `is-${name}`, className].filter(Boolean).join(' ')} />
  }
}

// ── The window and the Bar ──────────────────────────────────────────────────────────────
export const Minus = icon('minus')
/** Make the window fill the screen: arrows out. */
export const Maximize = icon('maximize')
/** Make the window smaller again: arrows in. */
export const Restore = icon('restore')
export const Close = icon('close')
export const Search = icon('search')
export const Settings = icon('settings')
export const Library = icon('library')
export const StoreBag = icon('store')
/** The Discord room. */
export const Chats = icon('chats')
/** The Music room, and music without its art. */
export const MusicNotes = icon('music')
/** Your graphics card. */
export const GraphicsCard = icon('graphics-card')
/** A processor. */
export const Cpu = icon('cpu')
/** The PC's memory. */
export const Memory = icon('memory')
/** The PC's motherboard. */
export const Board = icon('board')
/** A drive: an SSD or a hard drive. */
export const Drive = icon('drive')
/** A wired connection: the Ethernet port. */
export const Network = icon('network')
/** A Wi-Fi connection. */
export const Wifi = icon('wifi')
/** Exo's own update (a game's install is Download). */
export const AppUpdate = icon('app-update')
/** The Devices room: your mouse. */
export const Mouse = icon('mouse')
export const Home = icon('home')
/** A gift waiting on a store, in the Library. */
export const Gift = icon('gift')

// ── Actions ─────────────────────────────────────────────────────────────────────────────
export const Plus = icon('plus')
export const Check = icon('check')
export const ChevronDown = icon('chevron-down')
export const ChevronUp = icon('chevron-up')
export const ChevronLeft = icon('chevron-left')
export const ChevronRight = icon('chevron-right')
export const More = icon('more')
export const Download = icon('download')
export const Refresh = icon('refresh')
export const Undo = icon('undo')
export const Trash = icon('trash')
export const Copy = icon('copy')
export const ExternalLink = icon('external')
export const ArrowUpRight = icon('arrow-up-right')
export const LinkIcon = icon('link')
export const Pin = icon('pin')
export const Star = icon('star')
export const Heart = icon('heart')
export const ThumbsDown = icon('thumbs-down')
/** A Store game on your wishlist. */
export const Bookmark = icon('bookmark')
export const Key = icon('key')
export const Folder = icon('folder')
export const FolderOpen = icon('folder-open')
export const FolderPlus = icon('folder-plus')
export const ImageUpload = icon('image-upload')
export const SaveImage = icon('save-image')
export const FileIcon = icon('file')
export const Upload = icon('upload')
/** A game's Properties. */
export const Sliders = icon('sliders')
/** Music's Explore page. */
export const Explore = icon('explore')
/** Music's New page: new releases. */
export const New = icon('new')
export const Radio = icon('radio')
/** A camera in a call. */
export const Video = icon('video')
/** A song's music video. */
export const Movie = icon('movie')
/** A video, a stream or Music's Immersive filling the screen: the corners out (the window's own Maximize is the arrows). */
export const FullScreen = icon('full-screen')
export const ExitFullScreen = icon('exit-full-screen')
/** A game's cover art. */
export const Cover = icon('cover')
/** Exo Shaders: a game's shaders compiled ahead (the cube they draw). */
export const Shaders = icon('shaders')
/** Frame generation: the frame you see, with the frames made behind it. */
export const FrameGen = icon('frame-gen')
/** Compress games: arrows pressing onto a fold. */
export const Compress = icon('compress')
/** A game's anti-cheat, which Exo leaves alone: the shield (an encrypted call is the shield with its lock). */
export const AntiCheat = icon('anti-cheat')

// ── Music ───────────────────────────────────────────────────────────────────────────────
export const Play = icon('play')
export const Pause = icon('pause')
/** Stop: the solid square (Record is the solid circle). */
export const Stop = icon('stop')
export const Next = icon('next')
export const Previous = icon('previous')
export const Shuffle = icon('shuffle')
export const Repeat = icon('repeat')
export const RepeatOne = icon('repeat-one')
export const Queue = icon('queue')
/** Your saved songs: a list of them. */
export const Songs = icon('songs')
/** The song playing now: its record. */
export const NowPlaying = icon('now-playing')
export const Headphones = icon('headphones')
export const HeadphonesOff = icon('headphones-off')
/** Sound on, full: the speaker with its waves. */
export const Volume = icon('volume')
export const VolumeLow = icon('volume-low')
export const VolumeOff = icon('volume-off')
export const Mic = icon('mic')
export const MicOff = icon('mic-off')
export const Keyboard = icon('keyboard')

// ── People and messages ─────────────────────────────────────────────────────────────────
export const UserPlus = icon('user-plus')
/** A server's members. */
export const Members = icon('members')
/** One person, with no picture. */
export const Person = icon('person')
export const FriendAccepted = icon('friend-accepted')
export const MessageCircle = icon('message')
export const Unread = icon('unread')
export const Forum = icon('forum')
/** A thread: a message off a line, as a channel's threads hang from it. */
export const Threads = icon('threads')
export const Reply = icon('reply')
export const Forward = icon('forward')
export const At = icon('at')
export const Hash = icon('hash')
export const Phone = icon('phone')
export const PhoneDown = icon('phone-down')
export const Pencil = icon('pencil')
export const Smile = icon('smile')
export const SmilePlus = icon('smile-plus')
export const ViewReactions = icon('view-reactions')
export const Megaphone = icon('megaphone')
export const Soundboard = icon('soundboard')
export const Bell = icon('bell')
export const BellOff = icon('bell-off')
export const Inbox = icon('inbox')
export const Grip = icon('grip')
export const Eye = icon('eye')
export const EyeOff = icon('eye-off')
/** Copy an ID (developer mode). */
export const Id = icon('id')
export const Lock = icon('lock')
export const Clock = icon('clock')
/** Discord's Now: what is happening with your friends at the moment (a lightning bolt). */
export const Bolt = icon('now')
/** Something went wrong. */
export const Alert = icon('alert')
/** Exo Capture: the Bar's button, and the add-on. */
export const Capture = icon('capture')
/** Exo Capture: the replay it keeps. */
export const History = icon('history')
/** Your captures. */
export const Gallery = icon('gallery')
export const Hourglass = icon('hourglass')
export const Calendar = icon('calendar')
export const Book = icon('book')
export const Rules = icon('rules')
/** Browse Channels. */
export const ListSearch = icon('list-search')
export const Apps = icon('apps')
/** Discord's Shop. */
export const Shop = icon('shop')
/** A Nitro price. */
export const Nitro = icon('nitro')
// A server's owner (the crown beside their name) and someone boosting the server (a rocket: the lightning is Now's).
export const Crown = icon('crown')
// Message requests: DMs from people you aren't friends with, waiting for Accept.
export const Mail = icon('mail')
export const Boost = icon('boost')
export const Poll = icon('poll')
export const Award = icon('award')
export const Trophy = icon('trophy')
export const Tv = icon('tv')
export const Live = icon('live')
export const Signal = icon('signal')
export const Monitor = icon('monitor')
export const AppWindow = icon('app-window')
export const Flag = icon('flag')
export const Leaf = icon('leaf')
export const Pizza = icon('pizza')
export const Car = icon('car')
export const Lightbulb = icon('lightbulb')
export const Symbols = icon('symbols')
export const BadgeCheck = icon('badge-check')
/** A super reaction. */
export const Burst = icon('burst')
export const HashLock = marked('hash-lock', 'hash', 'lock-on')
export const VolumeLock = marked('volume-lock', 'volume-low', 'lock-on')
export const ZoomIn = icon('zoom-in')
/** Read a message aloud. */
export const Speaker = icon('speaker')
export const ZoomOut = icon('zoom-out')

// ── Meanings that read apart from a close, an eye, a lock, a download ───────────────────
/** Something that failed or isn't met. */
export const Failed = icon('failed')
/** A permission that's denied. */
export const Deny = icon('deny')
/** A permission left to what it inherits: Discord's gray slash. */
export const Neutral = icon('neutral')
/** Remove an item from a list. */
export const Remove = icon('remove')
/** Turn down a request from someone. */
export const Decline = icon('decline')
/** Revoke an invite. */
export const Revoke = icon('revoke')
/** The emoji picker's Flags. */
export const FlagsCategory = icon('flags-category')
/** A message only you can see. */
export const OnlyYou = icon('only-you')
/** Stop watching someone's stream. */
export const StopWatching = icon('stop-watching')
/** The snip editor's box over what you hide. */
export const Redact = icon('redact')
/** Captures' editor: Blur over names and chat, Redo; the viewer's Info, Share and Copy text. */
export const Blur = icon('blur')
export const Redo = icon('redo')
export const Info = icon('info')
export const Share = icon('share')
export const ScanText = icon('scan-text')
/** An end-to-end encrypted call. */
export const Encrypted = icon('encrypted')
/** A check still running. */
export const Pending = icon('pending')
/** A Discord server. */
export const Server = icon('server')
/** The PC room, and this PC as a whole. */
export const ThisPc = icon('this-pc')
/** Put something back to how it started. */
export const ResetDefault = icon('reset-default')
/** Stop opening a kind of file with Exo: the file, crossed out. */
export const Unassign = icon('unassign')
/** A partnered server. */
export const Partnered = icon('partnered')
/** A channel category. */
export const Category = icon('category')
/** Change what you're sharing. */
export const ChangeShare = icon('change-share')
/** How your PC performs. */
export const Performance = icon('performance')
/** Calibrate a controller's sticks: find their center. */
export const Calibrate = icon('calibrate')
/** The controller in your hands. */
export const Controller = icon('controller')
/** Select several at once. */
export const SelectMany = icon('select-many')
/** A link's preview that plays. */
export const LinkPreview = icon('link-preview')
/** Exo asleep while you play: the moon. */
export const Suspend = icon('suspend')
/** Clearing out what's left over: the broom. */
export const CleanUp = icon('clean-up')
/** A message forwarded or joined: the arrow in its gutter. */
export const ArrowRight = icon('arrow-right')

// ── Capture and the snip editor ─────────────────────────────────────────────────────────
export const Record = icon('record')
export const Scissors = icon('scissors')
export const Crop = icon('crop')
export const Highlighter = icon('highlighter')
export const TextIcon = icon('text')

// ── Exo Notes ───────────────────────────────────────────────────────────────────────────
export const Notes = icon('notes')
export const FileText = icon('file-text')
export const SaveFile = icon('save')
export const Bold = icon('bold')
export const Italic = icon('italic')
export const Strike = icon('strike')
export const Heading = icon('heading')
export const BulletList = icon('bullet-list')
export const Checklist = icon('checklist')
export const CodeIcon = icon('code')
export const ReplaceIcon = icon('replace')
export const ArrowUp = icon('arrow-up')
export const ArrowDown = icon('arrow-down')

// ── Brand marks ─────────────────────────────────────────────────────────────────────────
export const Spotify = brand('spotify')
export const AppleMusic = brand('apple-music')
/** Official YouTube Music mark, from music.youtube.com/img/on_platform_logo_dark.svg. */
export function YouTube({ size = 16, className }: IconProps) {
  return <svg width={size} height={size} viewBox="0 0 26 26" fill="none" aria-hidden="true" className={className} style={block}>
    <path d="m13 26c7.176 0 13-5.824 13-13s-5.824-13-13-13-13 5.824-13 13 5.824 13 13 13z" fill="#f03" />
    <path d="m20.5 13c0 4.1439-3.3561 7.5-7.5 7.5-4.14386 0-7.5-3.3561-7.5-7.5 0-4.14386 3.35614-7.5 7.5-7.5 4.1439 0 7.5 3.35614 7.5 7.5z" stroke="#fff" />
    <path d="m17.75 13-7.5-4.25v8.5z" fill="#fff" />
  </svg>
}
