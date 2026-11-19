import type { DiscordChannel } from '../../lib/discord'
import { Plus, Apps, ArrowRight, At, Award, BadgeCheck, Bell, BellOff, Bolt, Book, Bookmark, Boost, Burst, Calendar, Car, Category, Check, ChevronDown, ChevronLeft, ChevronRight, ChevronUp, Clock, Close, Copy, Cover, Crown, Decline, Download, Encrypted, EyeOff, ExitFullScreen, ExternalLink, Failed, FileIcon, Flag, FlagsCategory, Folder, Forum, Forward, FriendAccepted, FullScreen, Controller, Grip, Hash, HashLock, Headphones, HeadphonesOff, Hourglass, Id, Inbox, Leaf, Lightbulb, LinkIcon, ListSearch, Live, Lock, Mail, Megaphone, Members, MessageCircle, Mic, MicOff, More, Movie, MusicNotes, Nitro, OnlyYou, Partnered, Pencil, Pending, Person, Phone, PhoneDown, Pin, Pizza, Poll, Radio, Remove, Reply, Rules, SaveImage, Search, Shop, Smile, SmilePlus, Soundboard, Volume, Speaker, Star, StopWatching, Symbols, Threads, Trash, Trophy, Tv, Unread, Upload, UserPlus, Video, ViewReactions, VolumeLock, VolumeOff, ZoomIn, ZoomOut, Signal, Stop } from '../../brand/icons'

/** The Discord room's glyphs, in the places Discord's own client puts them, drawn in Exo's set: one glyph per meaning. */
export const Icons = {
  Add: Plus,
  Reply,
  At,
  HangUp: PhoneDown,
  Call: Phone,
  Delete: Trash,
  Edit: Pencil,
  Hash,
  Deafened: HeadphonesOff,
  Headphones,
  Announcement: Megaphone,
  Mic,
  Muted: MicOff,
  More,
  Pin,
  Search,
  React: SmilePlus,
  Emoji: Smile,
  Soundboard,
  Threads,
  Poll,
  Upload,
  CopyId: Id,
  ViewReactions,
  FriendAccepted,
  StopWatching,
  Person,
  Nitro,
  AddFriend: UserPlus,
  Members,
  Shop,
  Guide: Book,
  Rules,
  Playing: Controller,
  Listening: MusicNotes,
  Watching: Tv,
  Streaming: Live,
  VoiceSignal: Signal,
  Competing: Award,
  Profile: Person,
  Events: Calendar,
  Browse: ListSearch,
  // A voice channel is Discord's speaker; a stage channel is its broadcast radio icon.
  Voice: Volume,
  VoiceLock: VolumeLock,
  Stage: Radio,
  HashLock,
  Lock,
  Chat: MessageCircle,
  Forum,
  Copy,
  Bell,
  Inbox,
  Folder,
  Plus,
  BellOff,
  Star,
  Recent: Clock,
  // Now, the list's first row.
  Now: Bolt,
  Camera: Video,
  Check,
  Grip,
  // A stream's sound: on and muted.
  Sound: Volume,
  SoundOff: VolumeOff,
  // The emoji picker's categories, the thread breadcrumb, and Mark as spoiler.
  People: Smile,
  Nature: Leaf,
  Food: Pizza,
  Activities: Trophy,
  Travel: Car,
  Objects: Lightbulb,
  Symbols,
  Flags: FlagsCategory,
  Report: Flag,
  Crumb: ChevronRight,
  Spoiler: EyeOff,
  // A profile's verified connection; a link (a connection without its own mark, Copy Message Link); the message menu's Forward and Mark Unread; the composer's slowmode.
  Verified: BadgeCheck,
  Link: LinkIcon,
  Forward,
  Unread,
  Slowmode: Hourglass,
  // A person's or a message's Apps menu.
  Apps,
  // A list's ×, super reactions, and a file card's download and its preview's Expand and Collapse.
  Close,
  Failed,
  Remove,
  Decline,
  Partnered,
  Category,
  Encrypted,
  OnlyYou,
  Pending,
  ArrowRight,
  Super: Burst,
  Download,
  File: FileIcon,
  // A picture, GIF or video that would not load, in a file's one-line card.
  Picture: Cover,
  Movie,
  Expand: ChevronDown,
  Collapse: ChevronUp,
  // A watched stream's Full Screen and Exit Full Screen.
  FullScreen,
  ExitFullScreen,
  // A picture's menu: Save Image, Open Link (Copy Image is Copy); the image viewer's Zoom, Previous and Next.
  SaveImage,
  Open: ExternalLink,
  ZoomIn,
  ZoomOut,
  // A message's Speak Message, and Bookmark Message (saved for later, as the Store's wishlist keeps a game).
  Speak: Speaker,
  // Stop speaking it: Stop, as everywhere.
  StopSpeaking: Stop,
  Bookmark,
  Previous: ChevronLeft,
  Next: ChevronRight,
  // The member list's server owner and server booster.
  Owner: Crown,
  Boosting: Boost,
  // Message Requests in the DM list.
  Requests: Mail,
}

/** A channel's mark, as Discord draws it: with a lock when @everyone can't see it (a voice one: can't join it). */
export function channelGlyph(channel: Pick<DiscordChannel, 'kind' | 'locked' | 'rules' | 'type'>) {
  if (channel.kind === 'voice') {
    if (channel.type === 13) return channel.locked ? Icons.VoiceLock : Icons.Stage
    return channel.locked ? Icons.VoiceLock : Icons.Voice
  }
  if (channel.rules) return Icons.Rules
  if (channel.kind === 'forum') return Icons.Forum
  if (channel.type === 5) return Icons.Announcement
  return channel.locked ? Icons.HashLock : Icons.Hash
}
