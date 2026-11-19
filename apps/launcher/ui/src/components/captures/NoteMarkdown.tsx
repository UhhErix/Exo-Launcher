import { createContext, useContext, type ReactNode } from 'react'
import { Check } from '../../brand/icons'
import { openGuarded } from '../../lib/linkGuard'
import { ConfirmLabel } from '../ui/ConfirmButton'
import { useLinkGuard } from '../ui/useLinkGuard'
import { cn } from '../../lib/cn'

/**
 * A Markdown note's Preview: headings, paragraphs, lists and checklists (a press ticks one, in the words too), quotes,
 * code, rules and tables; inline bold, italic, strikethrough, code, links (opened in your browser, http, https and
 * mail only) and pictures (an Exo note's own, from its Photos folder; a picture on the web shows as its link, so a note
 * never fetches from another site). Drawn as React elements, never as HTML from the words, so a note can't put markup
 * on Exo's page.
 */
type Item = { text: string; task: boolean | null; line: number; depth: number }
type Block =
  | { kind: 'heading'; level: number; text: string }
  | { kind: 'paragraph'; lines: string[] }
  | { kind: 'list'; ordered: boolean; items: Item[] }
  | { kind: 'quote'; lines: string[] }
  | { kind: 'code'; text: string }
  | { kind: 'rule' }
  | { kind: 'table'; head: string[]; rows: string[][] }

const ITEM = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/
const TASK = /^\[([ xX])\]\s+(.*)$/
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/
const DIVIDER = /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/
const FENCE = /^\s*(```|~~~)/

const cells = (line: string) => line.trim().replace(/^\||\|$/g, '').split('|').map(cell => cell.trim())

/** One kind of block read from line i: it and the line after it, or null when line i doesn't start one. */
type Reader = (lines: string[], i: number) => [Block, number] | null

const QUOTE = /^\s*>/

const readFence: Reader = (lines, i) => {
  const fence = lines[i].match(FENCE)
  if (!fence) return null
  const body: string[] = []
  let at = i + 1
  for (; at < lines.length && !lines[at].trimStart().startsWith(fence[1]); at++) body.push(lines[at])
  return [{ kind: 'code', text: body.join('\n') }, at + 1]
}

const readHeading: Reader = (lines, i) => {
  const heading = lines[i].match(HEADING)
  return heading ? [{ kind: 'heading', level: heading[1].length, text: heading[2] }, i + 1] : null
}

const readRule: Reader = (lines, i) => RULE.test(lines[i]) ? [{ kind: 'rule' }, i + 1] : null

const readQuote: Reader = (lines, i) => {
  if (!QUOTE.test(lines[i])) return null
  const body: string[] = []
  let at = i
  for (; at < lines.length && QUOTE.test(lines[at]); at++) body.push(lines[at].replace(/^\s*>\s?/, ''))
  return [{ kind: 'quote', lines: body }, at]
}

const readList: Reader = (lines, i) => {
  const first = lines[i].match(ITEM)
  if (!first) return null
  const items: Item[] = []
  let at = i
  for (; at < lines.length; at++) {
    const item = lines[at].match(ITEM)
    if (!item) break
    const task = item[3].match(TASK)
    items.push({ text: task ? task[2] : item[3], task: task ? task[1] !== ' ' : null, line: at, depth: Math.min(4, Math.floor(item[1].replace(/\t/g, '  ').length / 2)) })
  }
  return [{ kind: 'list', ordered: /\d/.test(first[2]), items }, at]
}

const readTable: Reader = (lines, i) => {
  if (!lines[i].includes('|') || i + 1 >= lines.length || !DIVIDER.test(lines[i + 1])) return null
  const rows: string[][] = []
  let at = i + 2
  for (; at < lines.length && lines[at].includes('|') && lines[at].trim(); at++) rows.push(cells(lines[at]))
  return [{ kind: 'table', head: cells(lines[i]), rows }, at]
}

const READERS = [readFence, readHeading, readRule, readQuote, readList, readTable]

/** Lines up to a blank one or another block's start, as one paragraph; each line keeps its own line, as notes are written. */
const readParagraph = (lines: string[], i: number): [Block, number] => {
  const body = [lines[i].trim()]
  let at = i + 1
  for (; at < lines.length && lines[at].trim() && !READERS.some(read => read(lines, at)); at++) body.push(lines[at].trim())
  return [{ kind: 'paragraph', lines: body }, at]
}

function parse(text: string): Block[] {
  const lines = text.split('\n')
  const blocks: Block[] = []
  let i = 0
  while (i < lines.length) {
    if (!lines[i].trim()) { i++; continue }
    let read: [Block, number] | null = null
    for (const reader of READERS) if ((read = reader(lines, i))) break
    const [block, next] = read ?? readParagraph(lines, i)
    blocks.push(block)
    i = next
  }
  return blocks
}

const INLINE = /(`+)(.+?)\1|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|\*(?=\S)(.+?)\*|(?<![\p{L}\p{N}])_(?=\S)(.+?)_(?![\p{L}\p{N}])|!\[([^\]]*)\]\(([^)\s]+)\)|\[([^\]]+)\]\(([^)\s]+)\)|(https?:\/\/[^\s<>()]*[^\s<>().,;:!?'"])/gu

const safeLink = (url: string) => /^(https?:|mailto:)/i.test(url)

function Link({ url, children }: { url: string; children: ReactNode }) {
  const guard = useLinkGuard(url, onLookalike => openGuarded(url, onLookalike))
  if (!safeLink(url)) return <>{children}</>
  return (
    <a ref={guard.ref} className={cn(guard.className)} href={url} onClick={event => { event.preventDefault(); guard.click() }}>
      <ConfirmLabel armed={guard.armed} question={guard.question}>{children}</ConfirmLabel>
    </a>
  )
}

/** Where "Photos/…" loads from (notes.list's photos): Exo's notes only, not a file opened from elsewhere. */
const OwnPhotos = createContext<string | null>(null)
/** A picture Exo put in Photos (notes.photo names them so): nothing else is read from the Notes folder. */
const PHOTO = /^Photos\/([\w.-]+\.(?:png|jpe?g|gif|webp|bmp))$/i

function Photo({ src, alt }: { src: string; alt: string }) {
  const own = useContext(OwnPhotos)
  const name = own ? src.match(PHOTO)?.[1] : undefined
  if (own && name && !name.startsWith('.')) return <img className="exo-note-photo" src={own + encodeURIComponent(name)} alt={alt} loading="lazy" draggable={false} />
  return <Link url={src}>{alt || src}</Link>
}

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  let at = 0
  for (const match of text.matchAll(INLINE)) {
    const start = match.index
    if (start > at) out.push(text.slice(at, start))
    const key = start
    const [, , code, bold, bold2, strike, italic, italic2, alt, src, label, url, bare] = match
    if (code !== undefined) out.push(<code key={key}>{code}</code>)
    else if (bold !== undefined || bold2 !== undefined) out.push(<strong key={key}>{inline(bold ?? bold2)}</strong>)
    else if (strike !== undefined) out.push(<s key={key}>{inline(strike)}</s>)
    else if (italic !== undefined || italic2 !== undefined) out.push(<em key={key}>{inline(italic ?? italic2)}</em>)
    else if (src !== undefined) out.push(<Photo key={key} src={src} alt={alt} />)
    else if (label !== undefined) out.push(<Link key={key} url={url}>{inline(label)}</Link>)
    else if (bare !== undefined) out.push(<Link key={key} url={bare}>{bare}</Link>)
    at = start + match[0].length
  }
  if (at < text.length) out.push(text.slice(at))
  return out
}

const HEADINGS = ['h1', 'h2', 'h3', 'h4', 'h4', 'h4'] as const

const withBreaks = (lines: string[]) => lines.flatMap((line, at) => at ? [<br key={`br${at}`} />, ...inline(line)] : inline(line))

export function NoteMarkdown({ text, photos, onTick }: {
  text: string
  /** An Exo note's Photos address (notes.list), whose "Photos/…" pictures show; null for a file from elsewhere. */
  photos: string | null
  /** A checklist item pressed: its line in the words. */
  onTick: (line: number) => void
}) {
  return (
    <OwnPhotos.Provider value={photos}>
      <div className="exo-note-preview">{parse(text).map((block, index) => drawBlock(block, index, onTick))}</div>
    </OwnPhotos.Provider>
  )
}

function drawBlock(block: Block, index: number, onTick: (line: number) => void): ReactNode {
  switch (block.kind) {
    case 'heading': {
      const Tag = HEADINGS[block.level - 1]
      return <Tag key={index}>{inline(block.text)}</Tag>
    }
    case 'paragraph': return <p key={index}>{withBreaks(block.lines)}</p>
    case 'quote': return <blockquote key={index}>{withBreaks(block.lines)}</blockquote>
    case 'code': return <pre key={index}><code>{block.text}</code></pre>
    case 'rule': return <hr key={index} />
    case 'table':
      return (
        <table key={index}>
          <thead><tr>{block.head.map((cell, c) => <th key={c}>{inline(cell)}</th>)}</tr></thead>
          <tbody>{block.rows.map((row, r) => <tr key={r}>{block.head.map((_, c) => <td key={c}>{inline(row[c] ?? '')}</td>)}</tr>)}</tbody>
        </table>
      )
    case 'list': {
      const List = block.ordered ? 'ol' : 'ul'
      return (
        <List key={index} className={block.items.some(item => item.task !== null) ? 'is-tasks' : undefined}>
          {block.items.map(item => (
            <li key={item.line} className={item.task ? 'is-done' : undefined} style={item.depth ? { marginInlineStart: item.depth * 20 } : undefined}>
              {item.task !== null ? (
                <button type="button" className="exo-note-tick" aria-pressed={item.task} aria-label={item.task ? 'Done' : 'Not done'} onClick={() => onTick(item.line)}>
                  {item.task ? <Check size={12} /> : null}
                </button>
              ) : null}
              <span>{inline(item.text)}</span>
            </li>
          ))}
        </List>
      )
    }
  }
}
