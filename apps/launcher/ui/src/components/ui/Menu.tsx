import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode, type RefObject } from 'react'
import { cn } from '../../lib/cn'
import { useDismissOnAncestorScroll } from '../../lib/dismiss'
import { ConfirmLabel, useConfirmInPlace } from './ConfirmButton'

/** section: a heading over the items that share it (macOS menus); on: this item's own check, for a menu
    with one choice per section (in a menu at a point, a box ticked on its own); disabled: shown, not chosen; hint: its
    keys, at the row's end; instant: the menu goes at once, no fade, and is off the screen (three frames: drawn, handed
    to Windows, composed) before the choice runs, for one that captures the screen (Snip); confirm: the row asks it in place
    ("Delete?") on the first press and runs on the second, the menu staying open; danger: that question in Bad. */
export type MenuItem = { id: string; label: string; icon?: ReactNode; section?: string; on?: boolean; disabled?: boolean; hint?: string; instant?: boolean; confirm?: string; danger?: boolean }

/** A long section (a song's playlists) gets a field that narrows it, past this many items. */
const FILTER_FROM = 10

/** A menu at a point stays mounted through its fade as it closes (rooms.css › .exo-shelf-sort-menu, the quick time). */
const LEAVE_MS = 140

const rowsOf = (menu: RefObject<HTMLElement | null>) => Array.from(menu.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? [])

/** A menu's keys: the arrows, Home and End move between its rows (from its field, to the first or the last), Escape
    closes it and gives focus back, Tab closes it. */
function menuKeys(menu: RefObject<HTMLElement | null>, close: (returnFocus: boolean) => void) {
  return (event: KeyboardEvent<HTMLDivElement>) => {
    const list = rowsOf(menu)
    const at = list.indexOf(document.activeElement as HTMLElement)
    const typing = event.target instanceof HTMLInputElement
    const to = typing ? { ArrowDown: 0, ArrowUp: list.length - 1 }[event.key]
      : { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: list.length - 1 }[event.key]
    if (to !== undefined) {
      event.preventDefault()
      list[(to + list.length) % list.length]?.focus()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      close(true)
    } else if (event.key === 'Tab') close(false)
  }
}

/** A menu's rows: a heading where a section starts, then each item's icon and label; the checked one (or one that is
    on) takes the check (system.css). A row with `confirm` asks it in place first (ConfirmButton.tsx). */
function MenuRows<T extends MenuItem>({ items, checked, check = 'menuitemradio', onChoose }: {
  items: T[]
  checked?: string
  check?: 'menuitemradio' | 'menuitemcheckbox'
  onChoose: (item: T) => void
}) {
  return items.map((item, index) => [
    item.section && item.section !== items[index - 1]?.section
      ? <div key={'section:' + item.section} className="exo-menu-section" role="presentation">{item.section}</div>
      : null,
    <MenuRow key={item.id} item={item} checked={checked} check={check} onChoose={onChoose} />,
  ])
}

function MenuRow<T extends MenuItem>({ item, checked, check, onChoose }: {
  item: T
  checked?: string
  check: 'menuitemradio' | 'menuitemcheckbox'
  onChoose: (item: T) => void
}) {
  const ask = useConfirmInPlace<HTMLDivElement>(item.confirm ?? '', () => onChoose(item), item.danger)
  const choose = () => {
    if (item.disabled) return
    if (item.confirm) ask.press()
    else onChoose(item)
  }
  return (
    <div
      ref={ask.ref}
      role={checked === undefined && item.on === undefined ? 'menuitem' : check}
      aria-checked={item.on ?? (checked === undefined ? undefined : item.id === checked)}
      aria-disabled={item.disabled || undefined}
      tabIndex={-1}
      className={cn('exo-menu-item', item.confirm && 'exo-confirm', ask.className)}
      onClick={choose}
      onKeyDown={(event) => {
        if (event.key !== 'Enter' && event.key !== ' ') return
        event.preventDefault()
        choose()
      }}
    >
      {item.icon}
      {item.hint ? <><span className="exo-menu-label">{item.label}</span><kbd className="exo-menu-hint">{item.hint}</kbd></>
        : item.confirm ? <ConfirmLabel armed={ask.armed} question={item.confirm}>{item.label}</ConfirmLabel> : item.label}
    </div>
  )
}

/** Menu button over a native popover: top layer, light dismiss, anchored under its trigger. */
export function Menu({ label, anchor, offset, triggerClassName, trigger, className, items, checked, filter, disabled = false, onWarm, onAction }: {
  label: string
  anchor: `--${string}`
  offset: number
  triggerClassName: string
  /** The menu's own class, beside the shared one. */
  className?: string
  trigger: ReactNode
  items: MenuItem[]
  /** Set for a single-choice menu: the items become menuitemradio. */
  checked?: string
  /** section: the items a field above it narrows (shown once it is long); label: the field's name. */
  filter?: { section: string; label: string }
  /** A trigger that can't open its menu, by pointer or keyboard. */
  disabled?: boolean
  /** The trigger under the pointer or focused: what its items open is built out of sight before the press. */
  onWarm?: () => void
  onAction: (id: string) => void
}) {
  const id = useId()
  const [open, setOpen] = useState(false)
  // A closed menu's rows aren't in the DOM: ~34 of these sit mounted page-wide, each a small tree.
  const [opened, setOpened] = useState(false)
  const [find, setFind] = useState('')
  const filtering = !!filter && items.filter(item => item.section === filter.section).length > FILTER_FROM
  const needle = find.trim().toLowerCase()
  const shown = filtering && needle ? items.filter(item => item.section !== filter.section || item.label.toLowerCase().includes(needle)) : items
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const focusCurrent = () => (rowsOf(menuRef).find((item) => item.ariaChecked === 'true') ?? rowsOf(menuRef)[0])?.focus()
  // The rows mount the frame the popover opens, so the first open focuses after that render.
  useEffect(() => {
    if (!opened) return
    const list = rowsOf(menuRef)
    ;(list.find(item => item.ariaChecked === 'true') ?? list[0])?.focus()
  }, [opened])
  // The name carries the current choice too, as the trigger shows it ("Sort library, Recent"): the checked id, or a
  // menu of ticked items' first one (the Library's sort leads its Show section).
  const current = checked === undefined ? items.find(item => item.on)?.label : items.find(item => item.id === checked)?.label
  function hide(returnFocus: boolean) {
    if (menuRef.current?.matches(':popover-open')) menuRef.current.hidePopover()
    if (returnFocus) triggerRef.current?.focus()
  }
  useDismissOnAncestorScroll(open, triggerRef, menuRef, () => hide(false))
  async function choose(item: MenuItem) {
    const menu = menuRef.current
    if (item.instant && menu) menu.style.transition = 'none'
    hide(true)
    if (item.instant && menu) {
      for (let i = 0; i < 3; i++) await new Promise(requestAnimationFrame)
      menu.style.transition = ''
    }
    onAction(item.id)
  }
  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className={triggerClassName}
        style={{ anchorName: anchor }}
        aria-label={current ? `${label}, ${current}` : label}
        disabled={disabled}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={id}
        popoverTarget={id}
        onPointerEnter={onWarm}
        onFocus={onWarm}
      >
        {trigger}
      </button>
      <div
        ref={menuRef}
        id={id}
        popover="auto"
        role="menu"
        aria-label={label}
        className={className ? `exo-shelf-sort-menu ${className}` : 'exo-shelf-sort-menu'}
        style={{ positionAnchor: anchor, marginTop: offset }}
        onToggle={(event) => {
          setOpen(event.newState === 'open')
          if (event.newState !== 'open') setFind('')
          else if (opened) focusCurrent()
          else setOpened(true)
        }}
        onKeyDown={menuKeys(menuRef, hide)}
      >
        {opened ? (<>
        {filtering ? (
          <input type="search" className="exo-menu-find" value={find} placeholder={filter.label} aria-label={filter.label}
            onChange={(event) => setFind(event.target.value)} />
        ) : null}
        <MenuRows items={shown} checked={checked} onChoose={item => void choose(item)} />
        </>) : null}
      </div>
    </>
  )
}

/**
 * The same menu at a point (a right-click, a card's More): open while mounted, at `at` (each new one opens it again), kept inside the window
 * (lib/menuFit.ts); it only fades in, as a context menu does (rooms.css › .exo-game-menu). Escape, a press outside
 * or a choice closes it; onClose comes once its fade is done, unless it opened again meanwhile. An item that is on
 * is a box ticked on its own. `from`: where focus goes back to.
 */
export function PointMenu<T extends MenuItem & { run: () => void }>({ label, at, items, from, note, onClose }: {
  label: string
  at: { x: number; y: number }
  items: T[]
  from?: HTMLElement | null
  /** A heading over the items (Notes' "No suggestions"). */
  note?: string
  onClose: () => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const menu = ref.current
    if (!menu) return
    menu.showPopover()
    menu.style.left = `${at.x}px`
    menu.style.top = `${at.y}px`
    // Never over the Bar: a menu that would rise above it (a short window) starts under it instead.
    const bar = document.querySelector('.exo-titlebar')?.getBoundingClientRect().bottom ?? 0
    if (menu.getBoundingClientRect().top < bar) menu.style.top = `${bar + 4}px`
    if (!menu.contains(document.activeElement)) rowsOf(ref)[0]?.focus()
  }, [at, items.length])
  const close = (returnFocus: boolean) => {
    if (ref.current?.matches(':popover-open')) ref.current.hidePopover()
    if (returnFocus && from?.isConnected) from.focus({ preventScroll: true })
  }
  return (
    <div
      ref={ref}
      popover="auto"
      role="menu"
      aria-label={label}
      className="exo-shelf-sort-menu exo-game-menu"
      onToggle={(event) => {
        if (event.newState !== 'closed') return
        window.setTimeout(() => { if (!ref.current?.matches(':popover-open')) onClose() }, LEAVE_MS)
      }}
      onKeyDown={menuKeys(ref, close)}
    >
      {note ? <div className="exo-menu-section" role="presentation">{note}</div> : null}
      <MenuRows items={items} check="menuitemcheckbox" onChoose={item => { close(true); item.run() }} />
    </div>
  )
}

type Asked = { label: string; at: { x: number; y: number }; from: HTMLElement | null; items: MenuItem[]; answer: (id: string | null) => void }

/**
 * A choice of more than yes or no, asked from the control pressed (DESIGN.md › Confirm): its options as a menu's rows
 * under the control, the one that destroys asking in place (`confirm`, `danger`). `ask` resolves to the chosen row's
 * id, or null when the menu closes with none (Escape, a press elsewhere); render `menu` anywhere in the component.
 * `from`: the control pressed, else the one focused (a press focuses it).
 */
export function useChoiceMenu() {
  const [asked, setAsked] = useState<Asked | null>(null)
  const current = useRef<Asked | null>(null)
  const ask = useCallback((label: string, items: MenuItem[], from: Element | null = document.activeElement) => new Promise<string | null>(resolve => {
    current.current?.answer(null)
    const element = from instanceof HTMLElement && from !== document.body ? from : null
    const box = element?.getBoundingClientRect()
    const next: Asked = {
      label, items, from: element,
      at: box ? { x: box.left, y: box.bottom + 4 } : { x: window.innerWidth / 2, y: window.innerHeight / 2 },
      answer: resolve,
    }
    current.current = next
    setAsked(next)
  }), [])
  const menu = asked ? (
    <PointMenu label={asked.label} at={asked.at} from={asked.from}
      items={asked.items.map(item => ({ ...item, run: () => asked.answer(item.id) }))}
      onClose={() => {
        asked.answer(null)
        if (current.current === asked) current.current = null
        setAsked(shown => shown === asked ? null : shown)
      }} />
  ) : null
  return { menu, ask }
}
