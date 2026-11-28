import { useCallback, useEffect, useRef, useState, type ComponentType, type PointerEvent as ReactPointerEvent } from 'react'
import { ArrowUpRight, Blur, Crop, Highlighter, Pencil, Redact, ScanText, TextIcon, type IconProps } from '../../brand/icons'
import { host } from '../../lib/host'
import type { CaptureItem } from '../../lib/capture'
import { blurCell, centeredCrop, CROP_SHAPES, shapedCrop, type CropShape, type Rect } from '../../lib/gallery'
import { ErrorNote } from '../ui/ErrorNote'
import { EditorShell, PresetTile, type EditorTab } from './EditorShell'
import { errorText } from '../../lib/errorWords'

type Tab = 'crop' | 'markup' | 'blur' | 'text'
type Markup = 'arrow' | 'box' | 'highlight' | 'note'
type Tool = 'crop' | 'blur' | 'read' | Markup
type Mark =
  | { tool: 'arrow'; from: [number, number]; to: [number, number] }
  | { tool: 'highlight' | 'blur' | 'box'; rect: Rect }
  | { tool: 'note'; at: [number, number]; text: string }
  | { tool: 'crop'; rect: Rect }

const TABS: readonly EditorTab<Tab>[] = [
  { id: 'crop', label: 'Crop', icon: Crop },
  { id: 'markup', label: 'Mark up', icon: Pencil },
  { id: 'blur', label: 'Blur', icon: Blur },
  { id: 'text', label: 'Copy text', icon: ScanText },
]

const MARKUPS: { tool: Markup; label: string; icon: ComponentType<IconProps> }[] = [
  { tool: 'arrow', label: 'Arrow', icon: ArrowUpRight },
  { tool: 'box', label: 'Box', icon: Redact },
  { tool: 'highlight', label: 'Highlight', icon: Highlighter },
  { tool: 'note', label: 'Text', icon: TextIcon },
]

const ANNOTATE = '#ff5c5c'

function rectOf(a: [number, number], b: [number, number]): Rect {
  return { x: Math.min(a[0], b[0]), y: Math.min(a[1], b[1]), w: Math.abs(a[0] - b[0]), h: Math.abs(a[1] - b[1]) }
}

/**
 * The canvas as PNG, in base64 for the host, encoded in the background (toBlob, then a FileReader for the base64):
 * toDataURL encoded a full-size snip in one task on the press, a long freeze for one across several screens.
 */
async function pngOf(canvas: HTMLCanvasElement): Promise<string> {
  const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'))
  if (!blob) throw new Error('Exo could not make the picture. Try again.')
  const url = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(reader.error ?? new Error('Exo could not read the picture. Try again.'))
    reader.readAsDataURL(blob)
  })
  return url.slice(url.indexOf(',') + 1)
}

/**
 * Blur: the box drawn small (cells as tall as the line under it, lib/gallery.ts › blurCell), then large again with
 * smoothing, opaque over the box. Each cell is the average of several letters, so a name or a chat line can't be matched
 * back from it (as a fine pixelation can: Depix); what it hid reads as a soft smear, not a black hole in the picture.
 */
function blurInto(ctx: CanvasRenderingContext2D, image: HTMLImageElement, rect: Rect, unit: number) {
  const cell = blurCell(rect, unit)
  const w = Math.max(1, Math.round(rect.w / cell)), h = Math.max(1, Math.round(rect.h / cell))
  const small = document.createElement('canvas')
  small.width = w
  small.height = h
  const sctx = small.getContext('2d')!
  sctx.imageSmoothingQuality = 'high'
  sctx.drawImage(image, rect.x, rect.y, rect.w, rect.h, 0, 0, w, h)
  ctx.save()
  ctx.beginPath()
  ctx.rect(rect.x, rect.y, rect.w, rect.h)
  ctx.clip()
  ctx.imageSmoothingEnabled = true
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(small, 0, 0, w, h, rect.x, rect.y, rect.w, rect.h)
  ctx.restore()
  small.width = small.height = 0
}

/**
 * Draws the snip and every mark, in the snip's own pixels, at `scale` of its size (the editor draws at the size
 * it is shown; Save, at full size); the last crop decides what is kept.
 */
function paint(canvas: HTMLCanvasElement, image: HTMLImageElement, marks: Mark[], draft: Mark | null, forExport: boolean, scale = 1) {
  const all = draft ? [...marks, draft] : marks
  const crop = [...all].reverse().find(mark => mark.tool === 'crop') as Extract<Mark, { tool: 'crop' }> | undefined
  const area = forExport && crop ? crop.rect : { x: 0, y: 0, w: image.naturalWidth, h: image.naturalHeight }
  const width = Math.max(1, Math.round(area.w * scale)), height = Math.max(1, Math.round(area.h * scale))
  // A new size reallocates the canvas: only when it changes, not on every stroke.
  if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height }
  const ctx = canvas.getContext('2d')!
  ctx.setTransform(1, 0, 0, 1, 0, 0)
  ctx.clearRect(0, 0, width, height)
  ctx.setTransform(scale, 0, 0, scale, -area.x * scale, -area.y * scale)
  ctx.drawImage(image, 0, 0)
  const unit = Math.max(2, Math.round(Math.max(image.naturalWidth, image.naturalHeight) / 400))
  // Blur first, over the picture itself: a mark drawn later stays sharp on top of it.
  for (const mark of all) if (mark.tool === 'blur') blurInto(ctx, image, mark.rect, unit)
  for (const mark of all) {
    if (mark.tool === 'highlight') {
      ctx.save()
      ctx.globalCompositeOperation = 'multiply'
      ctx.fillStyle = 'rgba(255, 214, 10, 0.55)'
      ctx.fillRect(mark.rect.x, mark.rect.y, mark.rect.w, mark.rect.h)
      ctx.restore()
    } else if (mark.tool === 'box') {
      ctx.strokeStyle = ANNOTATE
      ctx.lineWidth = unit * 2
      ctx.lineJoin = 'round'
      ctx.strokeRect(mark.rect.x, mark.rect.y, mark.rect.w, mark.rect.h)
    } else if (mark.tool === 'arrow') {
      const [x1, y1] = mark.from, [x2, y2] = mark.to
      const angle = Math.atan2(y2 - y1, x2 - x1)
      const head = unit * 7
      ctx.strokeStyle = ANNOTATE
      ctx.fillStyle = ANNOTATE
      ctx.lineWidth = unit * 2
      ctx.lineCap = 'round'
      ctx.beginPath()
      ctx.moveTo(x1, y1)
      ctx.lineTo(x2 - Math.cos(angle) * head * 0.8, y2 - Math.sin(angle) * head * 0.8)
      ctx.stroke()
      ctx.beginPath()
      ctx.moveTo(x2, y2)
      ctx.lineTo(x2 - head * Math.cos(angle - 0.45), y2 - head * Math.sin(angle - 0.45))
      ctx.lineTo(x2 - head * Math.cos(angle + 0.45), y2 - head * Math.sin(angle + 0.45))
      ctx.closePath()
      ctx.fill()
    } else if (mark.tool === 'note') {
      ctx.font = `600 ${unit * 11}px "Geist", "Segoe UI Variable", sans-serif`
      ctx.textBaseline = 'top'
      ctx.lineWidth = unit * 1.5
      ctx.strokeStyle = '#000'
      ctx.strokeText(mark.text, mark.at[0], mark.at[1])
      ctx.fillStyle = '#eee'
      ctx.fillText(mark.text, mark.at[0], mark.at[1])
    } else if (mark.tool === 'crop' && !forExport) {
      // While editing, what the crop leaves out is dimmed.
      const { x, y, w, h } = mark.rect
      ctx.fillStyle = 'rgba(0, 0, 0, 0.6)'
      ctx.fillRect(0, 0, image.naturalWidth, y)
      ctx.fillRect(0, y + h, image.naturalWidth, image.naturalHeight - y - h)
      ctx.fillRect(0, y, x, h)
      ctx.fillRect(x + w, y, image.naturalWidth - x - w, h)
      ctx.strokeStyle = '#eee'
      ctx.lineWidth = unit / 2
      ctx.strokeRect(x, y, w, h)
    }
  }
}

/** The marks, and what Undo took (Redo puts it back); a new mark forgets what Undo took. */
function useMarks() {
  const [state, setState] = useState<{ marks: Mark[]; undone: Mark[] }>({ marks: [], undone: [] })
  const add = useCallback((mark: Mark) => setState(s => ({ marks: [...s.marks, mark], undone: [] })), [])
  const undo = useCallback(() => setState(s => s.marks.length ? { marks: s.marks.slice(0, -1), undone: [...s.undone, s.marks.at(-1)!] } : s), [])
  const redo = useCallback(() => setState(s => s.undone.length ? { marks: [...s.marks, s.undone.at(-1)!], undone: s.undone.slice(0, -1) } : s), [])
  const revert = useCallback(() => setState({ marks: [], undone: [] }), [])
  return { ...state, add, undo, redo, revert }
}

/**
 * The snip editor (Captures › Edit): the snip on the left, the inspector on the right by intent: Crop (free or a
 * shape: 16:9, 9:16, Square), Mark up (arrow, box, highlight, text), Blur (over names and chat), Copy text (drag over
 * words, or all of them: Windows reads them on this PC). Save writes the result over the snip and copies it.
 */
export function SnipEditor({ item, onDone }: { item: CaptureItem; onDone: (saved: CaptureItem | null) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [image, setImage] = useState<HTMLImageElement | null>(null)
  const [tab, setTab] = useState<Tab>('markup')
  const [markup, setMarkup] = useState<Markup>('arrow')
  const [shape, setShape] = useState<CropShape>('free')
  const { marks, undone, add, undo, redo, revert } = useMarks()
  const [draft, setDraft] = useState<Mark | null>(null)
  const [typing, setTyping] = useState<{ at: [number, number]; left: number; top: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [reading, setReading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [said, setSaid] = useState<string | null>(null)
  const start = useRef<[number, number] | null>(null)
  const tool: Tool = tab === 'crop' ? 'crop' : tab === 'blur' ? 'blur' : tab === 'text' ? 'read' : markup
  const ratio = CROP_SHAPES.find(entry => entry.id === shape)?.ratio ?? null
  // The text field that already ended: Enter, then the blur as it leaves, adds its text once; Escape adds none.
  const finished = useRef<typeof typing>(null)
  const finish = (session: typeof typing, value: string) => {
    if (!session || finished.current === session) return
    finished.current = session
    const text = value.trim()
    if (text) add({ tool: 'note', at: session.at, text })
    setTyping(current => current === session ? null : current)
  }

  useEffect(() => {
    const next = new Image()
    next.crossOrigin = 'anonymous'
    next.onload = () => setImage(next)
    next.onerror = () => setError('Exo couldn’t open the snip. Cancel and open it again.')
    next.src = item.url
    // Leaving the editor lets go of the decoded snip and the canvas's pixels at once, not whenever memory runs short.
    const drawn = canvas.current
    return () => {
      next.onload = next.onerror = null
      next.src = ''
      if (drawn) { drawn.width = 0; drawn.height = 0 }
    }
  }, [item.url])

  // The editor draws at the size it is shown on this screen, never larger than the snip: a snip across three 4K
  // screens is a 100 MB picture, its editor a few MB. Marks stay in the snip's own pixels. Null until measured,
  // so the first frame isn't drawn at full size.
  const [scale, setScale] = useState<number | null>(null)
  useEffect(() => {
    const frame = canvas.current?.parentElement
    if (!image || !frame) return
    const fit = () => {
      const dpr = window.devicePixelRatio || 1
      // Not laid out yet: the size of a large screen until it is.
      const width = frame.clientWidth || 2560 / dpr, height = frame.clientHeight || 1440 / dpr
      setScale(Math.min(1, width * dpr / image.naturalWidth, height * dpr / image.naturalHeight))
    }
    fit()
    const sizes = new ResizeObserver(fit)
    sizes.observe(frame)
    return () => sizes.disconnect()
  }, [image])

  useEffect(() => {
    if (image && scale && canvas.current) paint(canvas.current, image, marks, draft, false, scale)
  }, [image, marks, draft, scale])

  // Where the pointer is, in the snip's own pixels (the canvas shows the whole snip, at whatever size).
  const point = (event: ReactPointerEvent<HTMLCanvasElement>): [number, number] => {
    const box = event.currentTarget.getBoundingClientRect()
    const width = image?.naturalWidth ?? event.currentTarget.width, height = image?.naturalHeight ?? event.currentTarget.height
    return [(event.clientX - box.left) * (width / box.width), (event.clientY - box.top) * (height / box.height)]
  }

  function draftFor(from: [number, number], to: [number, number]): Mark {
    if (tool === 'arrow') return { tool, from, to }
    if (tool === 'crop') return { tool, rect: shapedCrop(from, to, ratio, image?.naturalWidth ?? 0, image?.naturalHeight ?? 0) }
    return { tool: tool === 'blur' ? 'blur' : tool === 'box' ? 'box' : 'highlight', rect: rectOf(from, to) }
  }

  /** A shape tile: with nothing cropped yet, the largest of it in the middle; Free leaves the crop to the drag. */
  function pickShape(next: CropShape) {
    setShape(next)
    const r = CROP_SHAPES.find(entry => entry.id === next)?.ratio
    if (r && image) add({ tool: 'crop', rect: centeredCrop(r, image.naturalWidth, image.naturalHeight) })
  }

  async function readText(area?: Rect) {
    if (reading) return
    setReading(true)
    setError(null)
    setSaid(null)
    try {
      const result = await host.captureText(item.id, area && { x: area.x, y: area.y, width: area.w, height: area.h })
      if (!result.ok || !result.text) { setError(errorText(result.message, 'No text found there.')); return }
      await navigator.clipboard.writeText(result.text)
      setSaid(`Copied: ${result.text.length > 80 ? result.text.slice(0, 80) + '…' : result.text}`)
    } catch (e) { setError(errorText(e, 'Exo couldn’t read the text. Try again.')) }
    finally { setReading(false) }
  }

  async function save() {
    if (!image || busy) return
    setBusy(true)
    setError(null)
    const failed = 'Exo couldn’t save the snip. Try again.'
    const out = document.createElement('canvas')
    try {
      paint(out, image, marks, null, true)
      const png = await pngOf(out)
      // Let go of the full-size pixels before the host's round trip.
      out.width = out.height = 0
      const result = await host.captureSaveImage(item.id, png)
      if (!result.ok) { setError(errorText(result.message, failed)); return }
      onDone(result.item ?? null)
    } catch (e) { setError(errorText(e, failed)) }
    finally { out.width = out.height = 0; setBusy(false) }
  }

  const cropped = marks.some(mark => mark.tool === 'crop')
  const panel = tab === 'crop' ? (
    <>
      <div className="exo-editor-tiles">
        {CROP_SHAPES.map(entry => (
          <PresetTile key={entry.id} label={entry.label} shape={entry.ratio} pressed={shape === entry.id} onPress={() => pickShape(entry.id)} />
        ))}
      </div>
      <p className="exo-editor-hint">{ratio ? 'Drag to place it, or keep the middle.' : 'Drag over what to keep.'}</p>
      {cropped ? <button type="button" className="exo-set-btn" onClick={() => add({ tool: 'crop', rect: { x: 0, y: 0, w: image?.naturalWidth ?? 0, h: image?.naturalHeight ?? 0 } })}>Whole picture</button> : null}
    </>
  ) : tab === 'markup' ? (
    <>
      <div className="exo-editor-tiles">
        {MARKUPS.map(entry => (
          <button key={entry.tool} type="button" className="exo-editor-tile" aria-pressed={markup === entry.tool} onClick={() => { setMarkup(entry.tool); setTyping(null) }}>
            <span className="exo-editor-tile-shape" aria-hidden="true"><entry.icon size={18} /></span>
            <strong>{entry.label}</strong>
          </button>
        ))}
      </div>
      <p className="exo-editor-hint">{markup === 'note' ? 'Click where the text goes, then type.' : markup === 'arrow' ? 'Drag from where it starts to what it points at.' : 'Drag over it.'}</p>
    </>
  ) : tab === 'blur' ? (
    <p className="exo-editor-hint">Drag over names, chat or anything else to hide. What&apos;s under it can&apos;t be read back.</p>
  ) : (
    <>
      <p className="exo-editor-hint">Drag over words to copy them. Windows reads them on this PC.</p>
      <button type="button" className="exo-set-btn" disabled={reading} onClick={() => void readText()}>Copy all text</button>
    </>
  )

  return (
    <EditorShell
      label="Edit snip"
      tabs={TABS}
      tab={tab}
      onTab={next => { setTab(next); setTyping(null); setSaid(null) }}
      typing={!!typing}
      history={{ revert: marks.length ? revert : null, undo: marks.length ? undo : null, redo: undone.length ? redo : null }}
      save={{ label: 'Save', busy, busyLabel: 'Saving the snip', disabled: !image || !marks.length, run: () => void save() }}
      onCancel={() => onDone(null)}
      media={
        <div className="exo-snip-canvas">
          <canvas
            ref={canvas}
            className={`is-${tool}`}
            onPointerDown={event => {
              if (!image) return
              const at = point(event)
              if (tool === 'note') {
                // The field is placed in the canvas's frame, which centers the canvas: measure from the frame.
                const box = (event.currentTarget.parentElement ?? event.currentTarget).getBoundingClientRect()
                setTyping({ at, left: event.clientX - box.left, top: event.clientY - box.top })
                return
              }
              start.current = at
              event.currentTarget.setPointerCapture(event.pointerId)
            }}
            onPointerMove={event => {
              if (!start.current) return
              const to = point(event)
              setDraft(tool === 'read' ? { tool: 'box', rect: rectOf(start.current, to) } : draftFor(start.current, to))
            }}
            onPointerUp={event => {
              if (!start.current) return
              const from = start.current, to = point(event)
              start.current = null
              setDraft(null)
              if (tool === 'read') {
                const rect = rectOf(from, to)
                if (rect.w > 4 && rect.h > 4) void readText(rect)
                return
              }
              const mark = draftFor(from, to)
              const big = mark.tool === 'arrow' ? Math.hypot(mark.to[0] - mark.from[0], mark.to[1] - mark.from[1]) > 6 : mark.tool !== 'note' && mark.rect.w > 4 && mark.rect.h > 4
              if (big) add(mark)
            }}
          />
          {typing ? (
            <input
              className="exo-snip-text"
              style={{ left: typing.left, top: typing.top }}
              autoFocus
              aria-label="Text"
              onKeyDown={event => {
                if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); finish(typing, '') }
                else if (event.key === 'Enter') finish(typing, event.currentTarget.value)
              }}
              onBlur={event => finish(typing, event.currentTarget.value)}
            />
          ) : null}
        </div>
      }
    >
      {panel}
      {said ? <p className="exo-capture-note" role="status">{said}</p> : null}
      {error ? <ErrorNote className="exo-capture-note is-bad" message={error} /> : null}
    </EditorShell>
  )
}
