// A tab's note is one record that changes in place (its words, what the host has, its file): React Compiler would keep
// what it drew from the record's first state, so this file is drawn as plain React.
'use no memo'

import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type ClipboardEvent as ReactClipboardEvent, type DragEvent as ReactDragEvent, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent, type RefObject } from 'react'
import { ArrowDown, ArrowUp, Bold, BulletList, Checklist, Close, CodeIcon, FileText, FolderOpen, Gallery, Heading, ImageUpload, Italic, LinkIcon, More, Pencil, Pin, Plus, ReplaceIcon, SaveFile, Search, Strike, Trash, Unassign } from '../../brand/icons'
import { closeCaptures } from '../../lib/capture'
import { host, type Game } from '../../lib/host'
import { noteWhen, type NoteInfo, type NotesRequest } from '../../lib/notes'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { morph } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { Menu, PointMenu, useChoiceMenu, type MenuItem } from '../ui/Menu'
import { Wait, sample, times } from '../ui/Wait'
import { NoteMarkdown } from './NoteMarkdown'
import { errorText } from '../../lib/errorWords'

/** A tab's note: its words as typed, what the host has of them, and whether they are still being read. */
type Doc = {
  /** Its text box's key, the same once a new note's file is made (the box keeps focus and caret). */
  uid: number
  /** Its tab's key: the note's id, or FRESH and a number until its first words make its file. */
  key: string
  info: NoteInfo
  text: string
  /** The words the host has (the file, or a file from elsewhere's kept edits). */
  sent: string
  loaded: boolean
  reading: boolean
  problem: string | null
  timer?: number
  /** Saves one after another, so an older one never lands over a newer. */
  chain: Promise<void>
  fresh: boolean
  /** A Markdown note shown as Preview. */
  preview: boolean
}

const FRESH = 'new:'
/** Typing settles this long before it is written. */
const SAVE_MS = 450
let freshCount = 0
let uidCount = 0
/** Every note as last listed: Notes shows them at once the next time it opens, while the folder is read again. */
let lastNotes: NoteInfo[] | null = null
/** Where Preview loads a note's Photos from: notes.list's address for this run. */
let notePhotos: string | null = null
const NO_NOTES: NoteInfo[] = []

const freshDoc = (): Doc => {
  const key = `${FRESH}${++freshCount}`
  return {
    uid: ++uidCount, key, text: '', sent: '', loaded: true, reading: false, problem: null, chain: Promise.resolve(), fresh: true, preview: false,
    info: { id: key, title: 'New note', snippet: '', kind: 'text', name: '', edited: new Date().toISOString(), size: 0, pinned: false, external: false, unsaved: false, gameId: null, encoding: 'utf-8', eol: 'crlf', spelling: true },
  }
}

const docFor = (info: NoteInfo): Doc => ({ uid: ++uidCount, key: info.id, info, text: '', sent: '', loaded: false, reading: false, problem: null, chain: Promise.resolve(), fresh: false, preview: false })

const byPinThenLatest = (list: NoteInfo[]) => [...list].sort((a, b) => Number(b.pinned) - Number(a.pinned) || Date.parse(b.edited) - Date.parse(a.edited))

/** A line's words without Markdown's marks, as the host titles a note (Notes.cs Plain). */
function plain(line: string): string {
  let s = line.trim().replace(/^[#>]+/, '').trim()
  s = s.replace(/^- \[[ xX]\] /, '').replace(/^[-*+] /, '')
  return s.replace(/\*\*|__|~~|`/g, '').trim()
}

/**
 * The tab's name: a note's first line as it is typed, a file from elsewhere's name. Read line by line up to the first
 * with words, not the whole note, since it is drawn on every key typed.
 */
function tabName(doc: Doc): string {
  if (doc.info.external) return doc.info.name
  if (!doc.loaded) return doc.info.title
  const text = doc.text
  for (let from = 0; from <= text.length;) {
    const end = text.indexOf('\n', from)
    const first = plain(text.slice(from, end < 0 ? text.length : end))
    if (first) return first.slice(0, 40)
    if (end < 0) break
    from = end + 1
  }
  return 'New note'
}

/** JavaScript's \s, by character code. */
const isSpace = (code: number) => code === 32 || (code >= 9 && code <= 13) || code === 0xa0 || code === 0x1680 || (code >= 0x2000 && code <= 0x200a)
  || code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000 || code === 0xfeff

/** How many words a note has, counted without splitting it into a list (it runs on every key typed). */
function wordCount(text: string): number {
  let count = 0
  let inWord = false
  for (let i = 0; i < text.length; i++) {
    const word = !isSpace(text.charCodeAt(i))
    if (word && !inWord) count++
    inWord = word
  }
  return count
}

/** The caret's line and column, 1-based, counted without splitting the note into lines. */
function caretAt(text: string, caret: number): { line: number; column: number } {
  let line = 1
  for (let at = text.indexOf('\n'); at >= 0 && at < caret; at = text.indexOf('\n', at + 1)) line++
  const start = caret > 0 ? text.lastIndexOf('\n', caret - 1) + 1 : 0
  return { line, column: caret - start + 1 }
}

const ENCODINGS: Record<NoteInfo['encoding'], string> = { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 with BOM', 'utf-16le': 'UTF-16 LE', 'utf-16be': 'UTF-16 BE', ansi: 'ANSI' }

/** The word around a caret: letters, marks, digits and apostrophes. */
function wordAt(text: string, at: number): [number, number] {
  const part = /[\p{L}\p{M}\p{N}'’]/u
  let start = at, end = at
  while (start > 0 && part.test(text[start - 1])) start--
  while (end < text.length && part.test(text[end])) end++
  while (start < end && /['’]/.test(text[start])) start++
  while (end > start && /['’]/.test(text[end - 1])) end--
  return [start, end]
}

/** Words go in the way typing does, so Undo takes them back. */
function put(area: HTMLTextAreaElement, start: number, end: number, text: string, select?: [number, number]) {
  area.focus({ preventScroll: true })
  area.setSelectionRange(start, end)
  if (!document.execCommand('insertText', false, text)) {
    area.setRangeText(text, start, end, 'end')
    area.dispatchEvent(new Event('input', { bubbles: true }))
  }
  if (select) area.setSelectionRange(select[0], select[1])
}

type Format = 'bold' | 'italic' | 'strike' | 'code' | 'heading' | 'list' | 'checklist' | 'link'

const WRAPS: Partial<Record<Format, string>> = { bold: '**', italic: '*', strike: '~~', code: '`' }
const LINE_MARKS: Partial<Record<Format, string>> = { heading: '# ', list: '- ', checklist: '- [ ] ' }

/** Markdown's marks around the selection, or before each line it touches (pressed again on marked lines, taken off). */
function format(area: HTMLTextAreaElement, kind: Format) {
  const { value, selectionStart: start, selectionEnd: end } = area
  const wrap = WRAPS[kind]
  if (wrap) {
    const inner = value.slice(start, end)
    if (value.slice(start - wrap.length, start) === wrap && value.slice(end, end + wrap.length) === wrap) {
      put(area, start - wrap.length, end + wrap.length, inner, [start - wrap.length, end - wrap.length])
    } else put(area, start, end, wrap + inner + wrap, [start + wrap.length, end + wrap.length])
    return
  }
  if (kind === 'link') {
    const label = value.slice(start, end) || 'link'
    put(area, start, end, `[${label}](https://)`, [start + label.length + 3, start + label.length + 11])
    return
  }
  const from = value.lastIndexOf('\n', start - 1) + 1
  const to = value.indexOf('\n', end) < 0 ? value.length : value.indexOf('\n', end)
  const lines = value.slice(from, to).split('\n')
  const mark = LINE_MARKS[kind] ?? ''
  const marked = /^(- \[[ xX]\] |[-*+] |#{1,6} )/
  const all = lines.every(line => kind === 'heading' ? /^#{1,6} /.test(line) : kind === 'checklist' ? /^- \[[ xX]\] /.test(line) : /^[-*+] (?!\[[ xX]\] )/.test(line))
  const next = lines.map(line => all ? line.replace(marked, '') : mark + line.replace(marked, '')).join('\n')
  put(area, from, to, next, [from, from + next.length])
}

/** A line of its own at the caret (a picture's), the caret after it. */
function putLine(area: HTMLTextAreaElement, line: string) {
  const { value, selectionStart: start, selectionEnd: end } = area
  const text = (start > 0 && value[start - 1] !== '\n' ? '\n' : '') + line + (end < value.length && value[end] !== '\n' ? '\n' : '')
  put(area, start, end, text, [start + text.length, start + text.length])
}

/** notes.photo takes up to this much (Notes.cs MaxPhotoBytes). */
const MAX_PHOTO = 25 * 1024 * 1024

const pictureIn = (data: DataTransfer) => [...data.files].find(file => file.type.startsWith('image/')) ?? null

/** A picture's bytes as base64, for notes.photo. */
const base64 = (file: Blob) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''))
  reader.onerror = () => reject(reader.error ?? new Error('The picture could not be read.'))
  reader.readAsDataURL(file)
})

type Find = { replace: boolean; query: string; with: string; matchCase: boolean; word: boolean; regex: boolean; current: number }

/** Every match of the find bar's words in a note, at most 5,000; bad: a regular expression that doesn't read. */
function findAll(text: string, find: Find): { ranges: Array<[number, number]>; pattern: RegExp | null; bad: boolean } {
  if (!find.query) return { ranges: [], pattern: null, bad: false }
  let pattern: RegExp
  try {
    const source = find.regex ? find.query : find.query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    pattern = new RegExp(find.word ? `\\b(?:${source})\\b` : source, find.matchCase ? 'gu' : 'giu')
  } catch { return { ranges: [], pattern: null, bad: true } }
  const ranges: Array<[number, number]> = []
  for (const match of text.matchAll(pattern)) {
    if (match[0].length === 0) continue
    ranges.push([match.index, match.index + match[0].length])
    if (ranges.length >= 5000) break
  }
  return { ranges, pattern, bad: false }
}

const LAST_LINE = '\n' + String.fromCharCode(0x200b)

/** The find bar's matches in the note, and the one in front (kept in range as the words change). */
function useMatches(text: string, find: Find | null) {
  const matches = useMemo(() => find ? findAll(text, find) : null, [find, text])
  const count = matches?.ranges.length ?? 0
  return { matches, count, current: find && count ? Math.min(find.current, count - 1) : 0 }
}

/** The matches lit behind the words: a copy of the note laid out as the text box is, its words clear. */
function Marks({ text, ranges, current, refTo }: { text: string; ranges: Array<[number, number]>; current: number; refTo: RefObject<HTMLDivElement | null> }) {
  const parts: Array<string | { text: string; on: boolean; key: number }> = []
  let at = 0
  ranges.forEach(([start, end], index) => {
    parts.push(text.slice(at, start))
    parts.push({ text: text.slice(start, end), on: index === current, key: index })
    at = end
  })
  parts.push(text.slice(at))
  return (
    <div ref={refTo} className="exo-note-marks" aria-hidden="true">
      {parts.map(part => typeof part === 'string' ? part
        : <mark key={`m${part.key}`} className={part.on ? 'is-current' : undefined}>{part.text}</mark>)}
      {/* A last empty line takes its height, as the text box gives it. */}
      {LAST_LINE}
    </div>
  )
}

function FindBar({ find, count, bad, onChange, onStep, onReplace, onReplaceAll, onClose }: {
  find: Find
  count: number
  bad: boolean
  onChange: (next: Partial<Find>) => void
  onStep: (by: -1 | 1) => void
  onReplace: () => void
  onReplaceAll: () => void
  onClose: () => void
}) {
  const field = useRef<HTMLInputElement>(null)
  useEffect(() => { field.current?.focus(); field.current?.select() }, [find.replace])
  const keys = (event: ReactKeyboardEvent) => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose() }
  }
  const toggle = (key: 'matchCase' | 'word' | 'regex', label: string, text: string) => (
    <button type="button" className="exo-shelf-chip" aria-pressed={find[key]} aria-label={label} onClick={() => onChange({ [key]: !find[key], current: 0 })}>{text}</button>
  )
  return (
    <div className="exo-note-find" role="search" onKeyDown={keys}>
      <div className="exo-note-find-row">
        <label className="exo-music-search exo-note-find-field">
          <Search size={14} />
          <input ref={field} type="text" value={find.query} placeholder="Find" aria-label="Find" spellCheck={false}
            onChange={event => onChange({ query: event.target.value, current: 0 })}
            onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); onStep(event.shiftKey ? -1 : 1) } }} />
          <span className="exo-note-find-count" aria-live="polite">{find.query ? count ? `${find.current + 1} of ${count}` : 'No matches' : ''}</span>
        </label>
        <span className="exo-shelf-chips" role="group" aria-label="Match">
          {toggle('matchCase', 'Match case', 'Aa')}
          {toggle('word', 'Whole word', 'Word')}
          {toggle('regex', 'Regular expression', '.*')}
        </span>
        <button type="button" className="exo-set-btn is-compact" aria-label="Previous match" disabled={!count} onClick={() => onStep(-1)}><ArrowUp size={14} /></button>
        <button type="button" className="exo-set-btn is-compact" aria-label="Next match" disabled={!count} onClick={() => onStep(1)}><ArrowDown size={14} /></button>
        <button type="button" className="exo-set-btn is-compact" aria-label="Replace" aria-pressed={find.replace} onClick={() => onChange({ replace: !find.replace })}><ReplaceIcon size={14} /></button>
        <button type="button" className="exo-set-btn is-compact" aria-label="Close find" onClick={onClose}><Close size={14} /></button>
      </div>
      {find.replace ? (
        <div className="exo-note-find-row">
          <label className="exo-music-search exo-note-find-field">
            <ReplaceIcon size={14} />
            <input type="text" value={find.with} placeholder="Replace with" aria-label="Replace with" spellCheck={false}
              onChange={event => onChange({ with: event.target.value })}
              onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); onReplace() } }} />
          </label>
          <button type="button" className="exo-set-btn is-compact" disabled={!count} onClick={onReplace}>Replace</button>
          <button type="button" className="exo-set-btn is-compact" disabled={!count} onClick={onReplaceAll}>Replace all</button>
        </div>
      ) : null}
      {bad ? <ErrorNote className="exo-capture-note is-bad" message="That isn't a regular expression." /> : null}
    </div>
  )
}

type Spell = { x: number; y: number; word: string; start: number; end: number; suggestions: string[] }

/** Windows' suggestions for a misspelled word, at the pointer; Add to dictionary puts it in Windows' own word list. */
function SpellMenu({ spell, onPick, onAdd, onClose }: { spell: Spell; onPick: (word: string) => void; onAdd: () => void; onClose: () => void }) {
  const items = [...spell.suggestions.map(word => ({ id: `w:${word}`, label: word, run: () => onPick(word) })), { id: 'add', label: 'Add to dictionary', run: onAdd }]
  return <PointMenu label={`Spelling of ${spell.word}`} at={spell} items={items} note={spell.suggestions.length === 0 ? 'No suggestions' : undefined} onClose={onClose} />
}

/** A file's name without its extension, as Rename shows it. */
const baseName = (name: string) => name.replace(/\.[^.]+$/, '')

/**
 * Rename, in the tab: its name becomes a field holding the file's name. Enter or leaving it renames (`byKey`: from the
 * keyboard, so the note takes the focus back), Escape keeps the old one; an empty name keeps it too.
 */
function RenameField({ name, onDone }: { name: string; onDone: (name: string | null, byKey: boolean) => void }) {
  const done = useRef(false)
  const finish = (value: string | null, byKey: boolean) => {
    if (done.current) return
    done.current = true
    onDone(value?.trim() ? value.trim() : null, byKey)
  }
  return (
    <input className="exo-field exo-note-tab-field" aria-label="Note name" defaultValue={baseName(name)} maxLength={200} autoFocus spellCheck={false}
      onFocus={event => event.currentTarget.select()}
      onBlur={event => finish(event.currentTarget.value, false)}
      onKeyDown={(event) => {
        if (event.key === 'Enter') { event.preventDefault(); finish(event.currentTarget.value, true) }
        else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); finish(null, true) }
      }} />
  )
}

/** Why a note's menu action failed, when the host didn't say. */
const NOTE_ACT_FAILED = {
  pin: 'Exo could not pin the note. Try again.',
  unpin: 'Exo could not unpin the note. Try again.',
  markdown: 'Exo could not make it Markdown. Try again.',
  text: 'Exo could not make it plain text. Try again.',
  reveal: 'Exo could not open its folder. Try again.',
  delete: 'Exo could not move the note to the Recycle Bin. Try again.',
} as const

const FORMATS: Array<{ kind: Format; label: string; keys?: string; Icon: typeof Bold }> = [
  { kind: 'bold', label: 'Bold', keys: 'Control+B', Icon: Bold },
  { kind: 'italic', label: 'Italic', keys: 'Control+I', Icon: Italic },
  { kind: 'strike', label: 'Strikethrough', Icon: Strike },
  { kind: 'heading', label: 'Heading', Icon: Heading },
  { kind: 'list', label: 'List', Icon: BulletList },
  { kind: 'checklist', label: 'Checklist', Icon: Checklist },
  { kind: 'code', label: 'Code', Icon: CodeIcon },
  { kind: 'link', label: 'Link', Icon: LinkIcon },
]

/**
 * Captures › Notes: every note on the left (search reads their words), the open ones as tabs, and the one in front:
 * plain text, or Markdown with its formatting bar and Edit or Preview. Exo's notes are written as you type; a file from
 * elsewhere keeps its edits on this PC (and after a restart) until Save. Find and replace, Windows' spell check on a
 * right-click, and Ctrl+N, Ctrl+S, Ctrl+Shift+S, Ctrl+O, Ctrl+W, Ctrl+F, Ctrl+H and Ctrl+Tab.
 */
export function NotesView({ request, games, onCaptures }: {
  request: NotesRequest | null
  games: readonly Game[]
  /** Captures, in the sheet's place. */
  onCaptures: () => void
}) {
  // The notes last listed show at once (stale while the folder is read again).
  const [notes, setNotes] = useState<NoteInfo[] | null>(lastNotes)
  useEffect(() => { if (notes) lastNotes = notes }, [notes])
  const [failed, setFailed] = useState<string | null>(null)
  const [tabs, setTabs] = useState<string[]>([])
  const [active, setActive] = useState<string | null>(null)
  const docs = useRef(new Map<string, Doc>())
  const tabsRef = useRef(tabs)
  tabsRef.current = tabs
  const [, redraw] = useReducer((n: number) => n + 1, 0)
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<NoteInfo[] | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const closing = useChoiceMenu()
  const [renaming, setRenaming] = useState<Doc | null>(null)
  const [find, setFind] = useState<Find | null>(null)
  const [spell, setSpell] = useState<Spell | null>(null)
  const [caret, setCaret] = useState(0)
  const [textFiles, setTextFiles] = useState<{ offered: boolean; isDefault: boolean } | null>(null)
  const area = useRef<HTMLTextAreaElement>(null)
  const marks = useRef<HTMLDivElement>(null)
  const ready = useRef(false)
  const doc = active ? docs.current.get(active) ?? null : null

  const upsert = useCallback((info: NoteInfo, was?: string) => {
    setNotes(list => byPinThenLatest([info, ...(list ?? []).filter(note => note.id !== info.id && note.id !== was)]))
    setFound(list => list?.map(note => note.id === info.id ? info : note) ?? list)
  }, [])

  const load = useCallback(async () => {
    try {
      const result = await host.notesList()
      if (!result.ok) { setFailed(errorText(result.message, 'Exo couldn’t read the Notes folder.')); return null }
      setFailed(null)
      notePhotos = result.photos ?? null
      setNotes(result.notes ?? [])
      return result
    } catch (error) {
      report(error, 'notes.list', { level: 'warn' })
      setFailed('Exo couldn’t read the Notes folder.')
      return null
    }
  }, [])
  const retry = useCallback(() => void load(), [load])

  /** In front, in its tab (opened if it wasn't). */
  const show = useCallback((info: NoteInfo) => {
    if (!docs.current.has(info.id)) docs.current.set(info.id, docFor(info))
    setTabs(list => list.includes(info.id) ? list : [...list, info.id])
    setActive(info.id)
  }, [])

  const newNote = useCallback(() => {
    const fresh = freshDoc()
    docs.current.set(fresh.key, fresh)
    setTabs(list => [...list, fresh.key])
    setActive(fresh.key)
    requestAnimationFrame(() => area.current?.focus())
  }, [])

  // The tabs as they were left, then what opened Notes: a note in front, or a new one.
  useEffect(() => {
    let live = true
    void load().then(result => {
      if (!live) return
      const list = result?.notes ?? []
      let open = (result?.tabs ?? []).filter(id => list.some(note => note.id === id))
      let front = result?.active && open.includes(result.active) ? result.active : open[0] ?? null
      for (const id of open) docs.current.set(id, docFor(list.find(note => note.id === id)!))
      const asked = request?.id ? list.find(note => note.id === request.id) : undefined
      if (asked) {
        if (!open.includes(asked.id)) { open = [...open, asked.id]; docs.current.set(asked.id, docFor(asked)) }
        front = asked.id
      }
      if (request?.fresh) {
        const fresh = freshDoc()
        docs.current.set(fresh.key, fresh)
        open = [...open, fresh.key]
        front = fresh.key
        requestAnimationFrame(() => area.current?.focus())
      }
      setTabs(open)
      setActive(front)
      ready.current = true
    })
    void host.notesTextFiles('state').then(result => setTextFiles({ offered: result.offered, isDefault: result.isDefault })).catch(() => { })
    return () => { live = false }
  }, [load, request])

  // The note in front is read once.
  useEffect(() => {
    const shown = active ? docs.current.get(active) : undefined
    if (!shown || shown.loaded || shown.reading) return
    shown.reading = true
    host.notesRead(shown.info.id).then(result => {
      shown.reading = false
      if (result.ok && result.text !== undefined && result.note) {
        shown.text = result.text
        shown.sent = result.text
        shown.info = result.note
        shown.loaded = true
        shown.problem = null
      } else shown.problem = errorText(result.message, 'Exo couldn’t open this note.')
      redraw()
    }).catch((error: unknown) => {
      shown.reading = false
      report(error, 'notes.read', { level: 'warn' })
      shown.problem = 'Exo couldn’t open this note.'
      redraw()
    })
  }, [active])

  // The tabs and the one in front, for the next time.
  useEffect(() => {
    if (!ready.current) return
    const kept = tabs.filter(key => !key.startsWith(FRESH))
    const timer = window.setTimeout(() => {
      void host.notesSession(kept, active && !active.startsWith(FRESH) ? active : null).catch((error: unknown) => report(error, 'notes.session', { level: 'warn' }))
    }, 250)
    return () => window.clearTimeout(timer)
  }, [tabs, active])

  // Search reads every note's words.
  useEffect(() => {
    const needle = query.trim()
    if (!needle) { setFound(null); return }
    let live = true
    const timer = window.setTimeout(() => {
      host.notesList(needle).then(result => { if (live) setFound(result.notes ?? []) }).catch(() => { if (live) setFound([]) })
    }, 180)
    return () => { live = false; window.clearTimeout(timer) }
  }, [query])

  const adopt = useCallback((fresh: Doc, info: NoteInfo) => {
    const was = fresh.key
    Object.assign(fresh, { key: info.id, info, fresh: false, loaded: true, problem: null })
    docs.current.delete(was)
    docs.current.set(info.id, fresh)
    setTabs(list => list.map(key => key === was ? info.id : key))
    setActive(front => front === was ? info.id : front)
    upsert(info)
  }, [upsert])

  /** Writes what's typed (toFile: Save, for a file from elsewhere). A new note's first words make its file. */
  const flush = useCallback((target: Doc, toFile = false): Promise<void> => {
    window.clearTimeout(target.timer)
    target.timer = undefined
    target.chain = target.chain.then(async () => {
      const text = target.text
      if (!target.loaded || !toFile && text === target.sent) return
      if (target.fresh) {
        if (!text.trim()) return
        const result = await host.notesSave(null, text, { markdown: target.info.kind === 'markdown' })
        if (!result.ok || !result.note) { target.problem = errorText(result.message, 'Exo couldn’t save the note.'); redraw(); return }
        target.sent = text
        adopt(target, result.note)
        return
      }
      const result = await host.notesSave(target.info.id, text, { toFile })
      if (result.ok && result.note) {
        target.sent = text
        target.info = result.note
        target.problem = null
        upsert(result.note)
      } else target.problem = errorText(result.message, 'Exo couldn’t save the note.')
      redraw()
    }).catch((error: unknown) => {
      report(error, 'notes.save', { level: 'warn' })
      target.problem = 'Exo couldn’t save the note.'
      redraw()
    })
    return target.chain
  }, [adopt, upsert])

  // Nothing typed is left behind: the sheet closing, or the page going.
  useEffect(() => {
    const all = docs.current
    const flushAll = () => { for (const each of all.values()) if (each.text !== each.sent) void flush(each) }
    window.addEventListener('pagehide', flushAll)
    return () => { window.removeEventListener('pagehide', flushAll); flushAll() }
  }, [flush])

  const edit = (target: Doc, text: string) => {
    target.text = text
    redraw()
    window.clearTimeout(target.timer)
    target.timer = window.setTimeout(() => void flush(target), SAVE_MS)
  }

  /** The tab goes; its neighbor comes to the front. */
  const drop = (key: string) => {
    const list = tabsRef.current
    const at = list.indexOf(key)
    const next = list.filter(other => other !== key)
    docs.current.delete(key)
    setTabs(next)
    setActive(front => front === key ? next[Math.min(at, next.length - 1)] ?? null : front)
  }

  /**
   * Closing a tab: a file from elsewhere with unsaved edits asks first; one of Exo's notes left empty goes to the
   * Recycle Bin, since nothing in it is worth a file.
   */
  const closeTab = async (key: string) => {
    const target = docs.current.get(key)
    if (target) {
      await flush(target)
      if (target.info.external && target.info.unsaved) {
        // Its edits aren't in the file: Save or Don't save, as Notepad asks, as a menu from the tab's close button; Don't
        // save asks in place, since the edits go. Escape or a press elsewhere keeps the tab.
        const answer = await closing.ask(`Save changes to ${target.info.name}?`, [
          { id: 'save', label: 'Save and close' },
          { id: 'discard', label: 'Close without saving', danger: true, confirm: 'Lose your edits?' },
        ], tabOf(key)?.querySelector('.exo-note-tab-close'))
        if (answer === 'save') void flush(target, true).then(() => { if (!target.info.unsaved) drop(key) })
        else if (answer === 'discard') void host.notesAct(target.info.id, 'discard').finally(() => drop(key))
        return
      }
      if (!target.fresh && !target.info.external && target.loaded && !target.text.trim()) {
        void host.notesAct(target.info.id, 'delete').then(result => {
          if (result.ok) setNotes(list => list?.filter(note => note.id !== target.info.id) ?? list)
        }).catch(() => { })
      }
    }
    drop(key)
  }

  const save = async (target: Doc) => {
    await flush(target, target.info.external)
    if (!target.problem) setStatus(target.info.external ? `Saved ${target.info.name}.` : null)
  }

  const saveAs = async (target: Doc) => {
    await flush(target)
    if (target.fresh) return
    try {
      const result = await host.notesSaveAs(target.info.id, target.text)
      if (result.cancelled) return
      if (!result.ok || !result.id) { setStatus(errorText(result.message, 'Exo couldn’t save the file.')); return }
      const listed = await load()
      const info = listed?.notes?.find(note => note.id === result.id)
      if (!info) return
      if (target.info.external) {
        // The file from elsewhere is now the one you saved, in its tab.
        docs.current.delete(target.key)
        docs.current.set(info.id, docFor(info))
        setTabs(list => list.map(key => key === target.key ? info.id : key))
        setActive(info.id)
      } else show(info)
      setStatus(`Saved ${info.name}.`)
    } catch (error) {
      report(error, 'notes.saveAs', { level: 'warn' })
      setStatus('Exo couldn’t save the file.')
    }
  }

  const openFile = async () => {
    try {
      const result = await host.notesOpen()
      if (result.cancelled) return
      if (!result.ok || !result.id) { setStatus(errorText(result.message, 'Exo couldn’t open the file.')); return }
      const listed = await load()
      const info = listed?.notes?.find(note => note.id === result.id)
      if (info) show(info)
    } catch (error) {
      report(error, 'notes.open', { level: 'warn' })
      setStatus('Exo couldn’t open the file.')
    }
  }

  const act = async (target: Doc, action: 'pin' | 'unpin' | 'markdown' | 'text' | 'reveal' | 'delete') => {
    setStatus(null)
    if (target.fresh) {
      if (action === 'markdown' || action === 'text') { target.info = { ...target.info, kind: action }; redraw() }
      return
    }
    await flush(target)
    try {
      const result = await host.notesAct(target.info.id, action)
      if (!result.ok) { setStatus(errorText(result.message, NOTE_ACT_FAILED[action])); return }
      if (action === 'delete') {
        setNotes(list => list?.filter(note => note.id !== target.info.id) ?? list)
        drop(target.key)
      } else if (result.note) {
        target.info = result.note
        upsert(result.note)
        redraw()
      }
    } catch (error) {
      report(error, 'notes.act', { level: 'warn' })
      setStatus(NOTE_ACT_FAILED[action])
    }
  }

  // Rename happens in the tab: its name becomes a field and back, the tab's width springing between them (DESIGN.md › Feel).
  const renameFrom = useRef<{ key: string; width: number } | null>(null)
  const tabOf = (key: string) => document.querySelector<HTMLElement>(`.exo-note-tabs [data-note-tab="${CSS.escape(key)}"]`)
  const renameTo = (target: Doc | null) => {
    const key = target?.key ?? renaming?.key
    const width = key ? tabOf(key)?.getBoundingClientRect().width : undefined
    renameFrom.current = key && width ? { key, width } : null
    setRenaming(target)
  }
  useLayoutEffect(() => {
    const from = renameFrom.current
    renameFrom.current = null
    const tab = from && tabOf(from.key)
    if (tab) morph(tab, from.width)
  }, [renaming])

  const rename = async (target: Doc, name: string | null) => {
    renameTo(null)
    if (name === null || target.fresh || name === baseName(target.info.name)) return
    try {
      const result = await host.notesAct(target.info.id, 'rename', name)
      if (!result.ok || !result.note) { setStatus(errorText(result.message, 'Exo couldn’t rename it.')); return }
      target.info = result.note
      upsert(result.note)
      redraw()
    } catch (error) {
      report(error, 'notes.rename', { level: 'warn' })
      setStatus('Exo couldn’t rename it.')
    }
  }

  const textFilesAct = async (action: 'offer' | 'settings' | 'withdraw') => {
    try {
      const result = await host.notesTextFiles(action)
      setTextFiles({ offered: result.offered, isDefault: result.isDefault })
      if (result.message) setStatus(result.message)
    } catch (error) {
      report(error, 'notes.textFiles', { level: 'warn' })
      setStatus('Exo couldn’t change how text files open. Try again.')
    }
  }

  // ── Find and replace ──
  const text = doc?.loaded ? doc.text : ''
  const { matches, count, current } = useMatches(text, find)
  const shownMatches = useRef(matches)
  shownMatches.current = matches
  // The match in front shows in the text box, and is selected there for when find closes; not while you type in it.
  useLayoutEffect(() => {
    const box = area.current
    const range = shownMatches.current?.ranges[current]
    if (!box || !range || document.activeElement === box) return
    box.setSelectionRange(range[0], range[1])
    const mark = marks.current?.querySelector<HTMLElement>('mark.is-current')
    if (mark && (mark.offsetTop < box.scrollTop || mark.offsetTop + mark.offsetHeight > box.scrollTop + box.clientHeight)) box.scrollTop = Math.max(0, mark.offsetTop - box.clientHeight / 3)
    if (marks.current) marks.current.scrollTop = box.scrollTop
  }, [find?.query, find?.matchCase, find?.word, find?.regex, current, count])

  const openFind = (replace: boolean) => {
    const box = area.current
    const picked = box && box.selectionEnd > box.selectionStart && box.selectionEnd - box.selectionStart < 200 ? box.value.slice(box.selectionStart, box.selectionEnd) : null
    setFind(was => ({ replace, query: picked ?? was?.query ?? '', with: was?.with ?? '', matchCase: was?.matchCase ?? false, word: was?.word ?? false, regex: was?.regex ?? false, current: 0 }))
  }
  const closeFind = () => { setFind(null); requestAnimationFrame(() => area.current?.focus({ preventScroll: true })) }
  const step = (by: -1 | 1) => setFind(was => was && count ? { ...was, current: (current + by + count) % count } : was)
  const replaceOne = () => {
    const box = area.current
    const range = matches?.ranges[current]
    if (!box || !range || !find || !matches?.pattern) return
    const found = box.value.slice(range[0], range[1])
    const next = find.regex ? found.replace(new RegExp(matches.pattern.source, matches.pattern.flags.replace('g', '')), find.with) : find.with
    put(box, range[0], range[1], next)
  }
  const replaceAll = () => {
    const box = area.current
    if (!box || !find || !matches?.pattern || !count) return
    // A regular expression's $1 and the rest are its groups; plain words go in as they are.
    const next = find.regex ? box.value.replace(matches.pattern, find.with) : box.value.replace(matches.pattern, () => find.with)
    put(box, 0, box.value.length, next, [0, 0])
    setStatus(`Replaced ${count}.`)
  }

  // ── Keys ──
  const formatKey = (kind: Format) => {
    if (doc?.info.kind !== 'markdown' || doc.preview || document.activeElement !== area.current || !area.current) return false
    format(area.current, kind)
  }
  const stepTab = (by: -1 | 1) => {
    if (tabs.length < 2) return false
    setActive(tabs[(tabs.indexOf(active ?? '') + by + tabs.length) % tabs.length])
  }
  const KEYS: Record<string, () => void | false> = {
    s: () => { if (doc) void save(doc) },
    'shift+s': () => { if (doc) void saveAs(doc) },
    n: newNote,
    o: () => void openFile(),
    w: () => { if (doc) void closeTab(doc.key) },
    f: () => openFind(false),
    h: () => openFind(true),
    tab: () => stepTab(1),
    'shift+tab': () => stepTab(-1),
    b: () => formatKey('bold'),
    i: () => formatKey('italic'),
  }
  const onKeyDown = (event: ReactKeyboardEvent) => {
    if (!event.ctrlKey || event.altKey || event.metaKey) return
    const run = KEYS[(event.shiftKey ? 'shift+' : '') + event.key.toLowerCase()]
    if (!run || run() === false) return
    event.preventDefault()
    event.stopPropagation()
  }

  // ── Photos and links: Markdown's, so a text note becomes a Markdown one as the first goes in ──
  const asMarkdown = async (target: Doc) => {
    if (target.info.kind !== 'markdown') await act(target, 'markdown')
    return target.info.kind === 'markdown'
  }
  /** The picture pasted or dropped, or one picked in Windows' picker, into Photos and onto its own line at the caret. */
  const addPhoto = async (target: Doc, picture?: Blob) => {
    if (target.info.external) return
    setStatus(null)
    if (picture && picture.size > MAX_PHOTO) { setStatus('That picture is over 25 MB.'); return }
    try {
      const result = await host.notesPhoto(picture ? await base64(picture) : undefined)
      if (!result.ok || !result.src) { if (!result.cancelled) setStatus(errorText(result.message, 'Exo couldn’t add the picture.')); return }
      if (await asMarkdown(target) && area.current) putLine(area.current, `![](${result.src})`)
    } catch (error) {
      report(error, 'notes.photo', { level: 'warn' })
      setStatus('Exo couldn’t add the picture.')
    }
  }
  const addLink = async (target: Doc) => {
    if (await asMarkdown(target) && area.current) format(area.current, 'link')
  }
  const onPaste = (event: ReactClipboardEvent<HTMLTextAreaElement>) => {
    if (!doc) return
    const picture = pictureIn(event.clipboardData)
    if (picture) {
      if (doc.info.external) return
      event.preventDefault()
      void addPhoto(doc, picture)
      return
    }
    // A web address pasted over words in a Markdown note links them.
    const box = event.currentTarget
    const url = event.clipboardData.getData('text/plain').trim()
    if (doc.info.kind !== 'markdown' || box.selectionEnd <= box.selectionStart || !/^https?:\/\/\S+$/i.test(url)) return
    event.preventDefault()
    put(box, box.selectionStart, box.selectionEnd, `[${box.value.slice(box.selectionStart, box.selectionEnd)}](${url})`)
  }
  const onDrop = (event: ReactDragEvent<HTMLTextAreaElement>) => {
    const picture = pictureIn(event.dataTransfer)
    if (!doc || !picture || doc.info.external) return
    event.preventDefault()
    void addPhoto(doc, picture)
  }

  const tick = (line: number) => {
    if (!doc) return
    const lines = doc.text.split('\n')
    lines[line] = lines[line].replace(/\[([ xX])\]/, (_, mark: string) => mark === ' ' ? '[x]' : '[ ]')
    edit(doc, lines.join('\n'))
  }
  // One function for Preview's whole life, so its Markdown (compiled, kept by its words and this) isn't read again on
  // every redraw of Notes (a key in search, a status).
  const latestTick = useRef(tick)
  latestTick.current = tick
  const onTick = useCallback((line: number) => latestTick.current(line), [])

  const onContextMenu = (event: ReactMouseEvent<HTMLTextAreaElement>) => {
    const box = event.currentTarget
    if (!doc?.info.spelling) return
    const [start, end] = wordAt(box.value, box.selectionStart)
    if (end <= start) return
    event.preventDefault()
    const word = box.value.slice(start, end)
    const { clientX: x, clientY: y } = event
    host.notesSpell(word).then(result => { if (result.wrong) setSpell({ x, y, word, start, end, suggestions: result.suggestions ?? [] }) })
      .catch((error: unknown) => report(error, 'notes.spell', { level: 'warn' }))
  }

  const addWord = (word: string) => void host.notesSpell(word, true).then(() => {
    // Windows' word list has it: the text box reads its words again.
    const box = area.current
    if (box) { box.spellcheck = false; requestAnimationFrame(() => { box.spellcheck = true }) }
  })

  const onMore = (id: string) => {
    if (id === 'folder') void host.notesAct('', 'folder').then(result => { if (!result.ok) setStatus(errorText(result.message, 'Exo couldn’t open the Notes folder.')) })
    else void textFilesAct(id as 'offer' | 'settings' | 'withdraw')
  }
  const onNoteAction = (target: Doc, id: string) => {
    if (id === 'rename') renameTo(target)
    else if (id === 'save') void save(target)
    else if (id === 'saveAs') void saveAs(target)
    else void act(target, id as 'pin' | 'unpin' | 'reveal' | 'delete')
  }
  const view = (target: Doc, preview: boolean) => {
    if (preview) { void flush(target); setFind(null) }
    target.preview = preview
    redraw()
  }

  const words = useMemo(() => wordCount(text), [text])

  return (
    <div className="exo-notes" onKeyDown={onKeyDown}>
      <NotesHead count={notes?.length ?? 0} more={moreMenu(textFiles)} onNew={newNote} onOpen={() => void openFile()} onMore={onMore} onCaptures={onCaptures} />
      <div className="exo-notes-body">
        <NotesList listed={found ?? notes ?? NO_NOTES} reading={notes === null && !failed} query={query} searched={found !== null} failed={failed}
          active={active} games={games} onQuery={setQuery} onRetry={retry} onShow={show} />
        <div className="exo-notes-main">
          <NoteTabs tabs={tabs} docs={docs.current} active={active} renaming={renaming?.key ?? null} onPick={setActive} onClose={key => void closeTab(key)}
            onRename={(name, byKey) => { if (renaming) void rename(renaming, name); if (byKey) area.current?.focus({ preventScroll: true }) }} />
          {doc ? front(doc) : <NotesEmpty any={!!notes?.length} onNew={newNote} onOpen={() => void openFile()} />}
        </div>
      </div>
      {dialogs()}
    </div>
  )
  // The note in front, with its tools, find bar and readings.
  function front(doc: Doc) {
    return (
      <>
        <NoteTools doc={doc} area={area} finding={!!find} onFind={() => find ? closeFind() : openFind(false)} onView={preview => view(doc, preview)}
          onKind={kind => void act(doc, kind)} onPhoto={() => void addPhoto(doc)} onLink={() => void addLink(doc)} onAction={id => onNoteAction(doc, id)} />
        {find && !doc.preview ? (
          <FindBar find={{ ...find, current }} count={count} bad={!!matches?.bad} onChange={next => setFind(was => was && { ...was, ...next })}
            onStep={step} onReplace={replaceOne} onReplaceAll={replaceAll} onClose={closeFind} />
        ) : null}
        <NoteBody doc={doc} area={area} marks={marks} ranges={find && count ? matches!.ranges : null} current={current} onTick={onTick}
          onEdit={next => edit(doc, next)} onCaret={setCaret} onBlur={() => { if (doc.text !== doc.sent) void flush(doc) }} onContextMenu={onContextMenu}
          onPaste={onPaste} onDrop={onDrop} />
        <NoteStatus doc={doc} status={status} caret={caret} words={words} />
      </>
    )
  }
  // Spelling's menu, and Save changes' from the tab closing.
  function dialogs() {
    return (
      <>
        {spell && area.current ? (
          <SpellMenu spell={spell} onClose={() => setSpell(null)} onAdd={() => addWord(spell.word)}
            onPick={word => { if (area.current) put(area.current, spell.start, spell.end, word) }} />
        ) : null}
        {closing.menu}
      </>
    )
  }
}

/** The note's menu: pin, rename, Save (a file from elsewhere), Save as, Show in folder, Delete (Exo's own notes). */
function noteMenu(doc: Doc): MenuItem[] {
  const saved = !doc.fresh
  return [
    { id: doc.info.pinned ? 'unpin' : 'pin', label: doc.info.pinned ? 'Unpin' : 'Pin', icon: <Pin size={16} /> },
    ...(saved ? [{ id: 'rename', label: 'Rename', icon: <Pencil size={16} /> }] : []),
    ...(doc.info.external ? [{ id: 'save', label: 'Save', icon: <SaveFile size={16} /> }] : []),
    ...(saved ? [
      { id: 'saveAs', label: 'Save as', icon: <SaveFile size={16} /> },
      { id: 'reveal', label: 'Show in folder', icon: <FolderOpen size={16} /> },
    ] : []),
    ...(saved && !doc.info.external ? [{ id: 'delete', label: 'Delete', icon: <Trash size={16} /> }] : []),
  ]
}

/** Notes' own menu: its folder, and Exo for text files (offered only when you ask; Windows' Default apps chooses). */
function moreMenu(textFiles: { offered: boolean; isDefault: boolean } | null): MenuItem[] {
  const folder = { id: 'folder', label: 'Show the Notes folder', icon: <FolderOpen size={16} /> }
  if (!textFiles?.offered) return [folder, { id: 'offer', label: 'Open text files with Exo', icon: <FileText size={16} /> }]
  return [
    folder,
    ...(textFiles.isDefault ? [] : [{ id: 'settings', label: 'Choose Exo in Default apps', icon: <FileText size={16} /> }]),
    { id: 'withdraw', label: 'Stop opening text files with Exo', icon: <Unassign size={16} /> },
  ]
}

/** With no note open: the two ways in and their keys, so the blank never looks broken. */
function NotesEmpty({ any, onNew, onOpen }: { any: boolean; onNew: () => void; onOpen: () => void }) {
  return (
    <div className="exo-captures-empty">
      <strong>{any ? 'No note open' : 'No notes yet'}</strong>
      <div className="exo-captures-empty-ways">
        <button type="button" className="exo-captures-way" onClick={onNew}><Plus size={16} />New note<kbd className="exo-capture-keys">Ctrl + N</kbd></button>
        <button type="button" className="exo-captures-way" onClick={onOpen}><FileText size={16} />Open a file<kbd className="exo-capture-keys">Ctrl + O</kbd></button>
      </div>
    </div>
  )
}

function NotesHead({ count, more, onNew, onOpen, onMore, onCaptures }: {
  count: number
  more: MenuItem[]
  onNew: () => void
  onOpen: () => void
  onMore: (id: string) => void
  onCaptures: () => void
}) {
  return (
    <header className="exo-hub-head">
      <div className="min-w-0">
        <h2 id="exo-captures-title" className="exo-hub-title">Notes</h2>
        {count ? <p className="exo-hub-sub tabular-nums">{count === 1 ? '1 note' : `${count} notes`}</p> : null}
      </div>
      <div className="exo-hub-head-actions">
        <button type="button" className="exo-set-btn is-primary" aria-keyshortcuts="Control+N" onClick={onNew}><Plus size={14} />New note</button>
        <button type="button" className="exo-set-btn" aria-keyshortcuts="Control+O" onClick={onOpen}><FileText size={14} />Open</button>
        <Menu label="More" anchor="--exo-notes-more" offset={6} triggerClassName="exo-set-btn" trigger={<More size={16} />} items={more} onAction={onMore} />
        <button type="button" className="exo-set-btn" onClick={onCaptures}><Gallery size={14} />Captures</button>
        <button type="button" className="exo-game-close" aria-label="Close Captures" onClick={closeCaptures}><Close size={16} /></button>
      </div>
    </header>
  )
}

/**
 * Every note, pinned first then the latest; with words in search, the notes that hold them. Kept (memo) while a note is
 * typed in: Notes redraws on every key, and the list doesn't change with it.
 */
const NotesList = memo(function NotesList({ listed, reading, query, searched, failed, active, games, onQuery, onRetry, onShow }: {
  listed: NoteInfo[]
  /** The folder is being read for the first time: the rows shine. */
  reading: boolean
  query: string
  searched: boolean
  failed: string | null
  active: string | null
  games: readonly Game[]
  onQuery: (query: string) => void
  onRetry: () => void
  onShow: (note: NoteInfo) => void
}) {
  const titles = new Map(games.map(game => [game.id, game.title]))
  return (
    <aside className="exo-notes-list" aria-label="All notes">
      <label className="exo-music-search exo-notes-search">
        <Search size={14} />
        <input type="search" value={query} placeholder="Search notes" aria-label="Search notes" spellCheck={false} onChange={event => onQuery(event.target.value)} />
      </label>
      {failed ? <ErrorNote className="exo-capture-note is-bad" message={failed} /> : null}
      {failed ? <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button> : null}
      {searched && listed.length === 0 ? <p className="exo-hub-sub">No notes match</p> : null}
      {reading ? (
        <Wait label="Reading your notes">
          <ul className="exo-notes-rows">
            {times(6, i => <li><span className="exo-notes-row"><span className="exo-notes-row-title"><span>{sample(i)}</span></span><span className="exo-notes-row-sub"><span>Yesterday</span><span className="exo-notes-row-snippet">{sample(i + 1)}</span></span></span></li>)}
          </ul>
        </Wait>
      ) : null}
      <ul className="exo-notes-rows">
        {listed.map(note => <NoteRow key={note.id} note={note} on={note.id === active} game={note.gameId ? titles.get(note.gameId) : undefined} onShow={onShow} />)}
      </ul>
    </aside>
  )
})

/** One note's row; kept while its note, its game and whether it is in front stay the same. */
const NoteRow = memo(function NoteRow({ note, on, game, onShow }: { note: NoteInfo; on: boolean; game?: string; onShow: (note: NoteInfo) => void }) {
  return (
    <li>
      <button type="button" className={cn('exo-notes-row', on && 'is-on')} aria-current={on || undefined} onClick={() => onShow(note)}>
        <span className="exo-notes-row-title">
          {note.pinned ? <Pin size={12} filled /> : null}
          <span>{note.external ? note.name : note.title}</span>
        </span>
        <span className="exo-notes-row-sub">
          <span>{noteWhen(note.edited)}</span>
          {game ? <span>{game}</span> : null}
          {note.snippet ? <span className="exo-notes-row-snippet">{note.snippet}</span> : null}
        </span>
      </button>
    </li>
  )
})

function NoteTabs({ tabs, docs, active, renaming, onPick, onClose, onRename }: {
  tabs: string[]
  docs: ReadonlyMap<string, Doc>
  active: string | null
  /** The tab being renamed: its name is a field. */
  renaming: string | null
  onPick: (key: string) => void
  onClose: (key: string) => void
  onRename: (name: string | null, byKey: boolean) => void
}) {
  return (
    <div className="exo-note-tabs" role="tablist" aria-label="Open notes">
      {tabs.map(key => {
        const tab = docs.get(key)
        if (!tab) return null
        const name = tabName(tab)
        return (
          <div key={key} className={cn('exo-note-tab', key === active && 'is-on')} data-note-tab={key}>
            {renaming === key ? <RenameField name={tab.info.name} onDone={onRename} /> : (
              <button type="button" role="tab" aria-selected={key === active} className="exo-note-tab-name" onClick={() => onPick(key)}
                onAuxClick={event => { if (event.button === 1) onClose(key) }}>
                <span>{name}</span>
                {tab.info.unsaved ? <span className="exo-note-dot" aria-label="Unsaved" /> : null}
              </button>
            )}
            <button type="button" className="exo-note-tab-close" aria-label={`Close ${name}`} onClick={() => onClose(key)}><Close size={12} /></button>
          </div>
        )
      })}
    </div>
  )
}

/**
 * Over the note: Markdown's formatting (a text note of Exo's: Link and Photo, which make it Markdown) and Edit or
 * Preview, then Text or Markdown, Find and the note's menu.
 */
function NoteTools({ doc, area, finding, onFind, onView, onKind, onPhoto, onLink, onAction }: {
  doc: Doc
  area: RefObject<HTMLTextAreaElement | null>
  finding: boolean
  onFind: () => void
  onView: (preview: boolean) => void
  onKind: (kind: 'text' | 'markdown') => void
  onPhoto: () => void
  onLink: () => void
  onAction: (id: string) => void
}) {
  const markdown = doc.info.kind === 'markdown'
  const own = !doc.info.external
  return (
    <div className="exo-note-tools">
      {!doc.preview && (markdown || own) ? (
        <span className="exo-note-format" role="toolbar" aria-label="Formatting">
          {markdown ? FORMATS.map(({ kind, label, keys, Icon }) => (
            <button key={kind} type="button" className="exo-note-tool" aria-label={label} aria-keyshortcuts={keys}
              onPointerDown={event => event.preventDefault()} onClick={() => { if (area.current) format(area.current, kind) }}>
              <Icon size={16} />
            </button>
          )) : (
            <button type="button" className="exo-note-tool" aria-label="Link" onPointerDown={event => event.preventDefault()} onClick={onLink}>
              <LinkIcon size={16} />
            </button>
          )}
          {own ? (
            <button type="button" className="exo-note-tool" aria-label="Photo" onPointerDown={event => event.preventDefault()} onClick={onPhoto}>
              <ImageUpload size={16} />
            </button>
          ) : null}
        </span>
      ) : null}
      {markdown ? (
        <span className="exo-shelf-chips" role="group" aria-label="View">
          <button type="button" className="exo-shelf-chip" aria-pressed={!doc.preview} onClick={() => onView(false)}>Edit</button>
          <button type="button" className="exo-shelf-chip" aria-pressed={doc.preview} onClick={() => onView(true)}>Preview</button>
        </span>
      ) : null}
      <span className="exo-note-tools-gap" />
      {doc.info.external ? null : (
        <span className="exo-shelf-chips" role="group" aria-label="Kind">
          <button type="button" className="exo-shelf-chip" aria-pressed={!markdown} onClick={() => onKind('text')}>Text</button>
          <button type="button" className="exo-shelf-chip" aria-pressed={markdown} onClick={() => onKind('markdown')}>Markdown</button>
        </span>
      )}
      {doc.preview ? null : (
        <button type="button" className="exo-note-tool" aria-label="Find" aria-keyshortcuts="Control+F" aria-pressed={finding} onClick={onFind}><Search size={16} /></button>
      )}
      <Menu label="Note" anchor="--exo-note-more" offset={6} triggerClassName="exo-note-tool" trigger={<More size={16} />} items={noteMenu(doc)} onAction={onAction} />
    </div>
  )
}

/** The note in front: its words in the text box (the matches lit behind them), Markdown's Preview, or why it didn't open. */
function NoteBody({ doc, area, marks, ranges, current, onTick, onEdit, onCaret, onBlur, onContextMenu, onPaste, onDrop }: {
  doc: Doc
  area: RefObject<HTMLTextAreaElement | null>
  marks: RefObject<HTMLDivElement | null>
  ranges: Array<[number, number]> | null
  current: number
  onTick: (line: number) => void
  onEdit: (text: string) => void
  onCaret: (at: number) => void
  onBlur: () => void
  onContextMenu: (event: ReactMouseEvent<HTMLTextAreaElement>) => void
  /** A picture pasted or a link pasted over words. */
  onPaste: (event: ReactClipboardEvent<HTMLTextAreaElement>) => void
  /** A picture dropped from Explorer. */
  onDrop: (event: ReactDragEvent<HTMLTextAreaElement>) => void
}) {
  if (!doc.loaded) {
    return <div className="exo-note-empty" aria-busy={!doc.problem}>{doc.problem ? <ErrorNote className="exo-capture-note is-bad" message={doc.problem} /> : null}</div>
  }
  if (doc.preview) return <div className="exo-note-page"><NoteMarkdown text={doc.text} photos={doc.info.external ? null : notePhotos} onTick={onTick} /></div>
  return (
    <div className="exo-note-editor">
      {ranges ? <Marks text={doc.text} ranges={ranges} current={current} refTo={marks} /> : null}
      <textarea
        key={doc.uid}
        ref={area}
        className="exo-note-text"
        value={doc.text}
        aria-label={tabName(doc)}
        placeholder={doc.fresh ? 'New note' : undefined}
        spellCheck={doc.info.spelling}
        onChange={event => { onEdit(event.target.value); onCaret(event.target.selectionStart) }}
        onSelect={event => onCaret(event.currentTarget.selectionStart)}
        onScroll={event => { if (marks.current) { marks.current.scrollTop = event.currentTarget.scrollTop; marks.current.scrollLeft = event.currentTarget.scrollLeft } }}
        onBlur={onBlur}
        onContextMenu={onContextMenu}
        onPaste={onPaste}
        onDragOver={event => { if (event.dataTransfer.types.includes('Files')) event.preventDefault() }}
        onDrop={onDrop}
      />
    </div>
  )
}

/** Under the note: what happened, then where the caret is, how many words and characters, and how the file is written. */
function NoteStatus({ doc, status, caret, words }: { doc: Doc; status: string | null; caret: number; words: number }) {
  const at = caretAt(doc.text, caret)
  return (
    <div className="exo-note-status" role="status">
      {doc.problem && doc.loaded ? <span className="is-bad">{doc.problem}</span> : null}
      {status ? <span>{status}</span> : null}
      <span className="exo-note-tools-gap" />
      {doc.info.external && doc.info.unsaved ? <span>Unsaved</span> : null}
      {doc.loaded ? (
        <>
          {doc.preview ? null : <span>Ln {at.line}, Col {at.column}</span>}
          <span>{words === 1 ? '1 word' : `${words} words`}</span>
          <span>{doc.text.length === 1 ? '1 character' : `${doc.text.length} characters`}</span>
        </>
      ) : null}
      <span>{ENCODINGS[doc.info.encoding]}</span>
      <span>{doc.info.eol.toUpperCase()}</span>
    </div>
  )
}
