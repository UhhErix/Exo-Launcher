import layoutData from '../../../contracts/keyboard-layouts.json' with { type: 'json' }
import type { KeyboardFormFactor } from '../../lib/host'

export type KeyKind = 'alpha' | 'mod' | 'wasd' | 'space' | 'gap' | 'led-bar' | 'analog'

export type KeyCap = {
  id: string
  label: string
  u: number
  kind?: KeyKind
  note?: string
}

export type LayoutCode = 'ansi' | 'iso' | 'jis' | 'ansi-split' | 'iso-split' | 'keypad'

export type KeyboardLayoutSpec = {
  form: KeyboardFormFactor
  layoutCode: LayoutCode
  mainRows: KeyCap[][]
  navRows: KeyCap[][]
  numRows: KeyCap[][]
  hasLedBar: boolean
}

type LayoutData = {
  fnRow: KeyCap[]
  mainRow1: { standard: KeyCap[]; jis: KeyCap[] }
  mainRow2: { ansi: KeyCap[]; iso: KeyCap[]; jis: KeyCap[] }
  mainRow3: { ansi: KeyCap[]; iso: KeyCap[]; jis: KeyCap[] }
  mainRow4: { ansi: KeyCap[]; iso: KeyCap[]; jis: KeyCap[] }
  bottomRows: { standard: KeyCap[]; split: KeyCap[]; jis: KeyCap[] }
  tklNavRows: KeyCap[][]
  w80NavRows: KeyCap[][]
  fullNumRows: KeyCap[][]
  uwuRows: KeyCap[][]
}

const DATA = layoutData as LayoutData

export function resolveFormFactor(formFactor?: KeyboardFormFactor | string | null, model?: string | null): KeyboardFormFactor {
  if (formFactor === '60' || formFactor === '80' || formFactor === 'tkl' || formFactor === 'full' || formFactor === 'keypad3') {
    return formFactor
  }
  const m = (model ?? '').toLowerCase()
  if (m.includes('uwu')) return 'keypad3'
  if (m.includes('two') || m.includes('full') || m.includes('100')) return 'full'
  if (m.includes('80')) return '80'
  if (m.includes('one') || m.includes('tkl')) return 'tkl'
  return '60'
}

export function resolveLayoutCode(
  layoutCode?: string | null,
  layoutLabel?: string | null,
  form?: KeyboardFormFactor,
): LayoutCode {
  if (form === 'keypad3') return 'keypad'
  const raw = (layoutCode ?? '').toLowerCase().trim()
  if (raw === 'ansi' || raw === 'iso' || raw === 'jis' || raw === 'ansi-split' || raw === 'iso-split' || raw === 'keypad') {
    return raw
  }
  const text = (layoutLabel ?? '').toLowerCase()
  const split = text.includes('split')
  if (text.includes('jis')) return 'jis'
  if (text.includes('iso')) return split ? 'iso-split' : 'iso'
  return split ? 'ansi-split' : 'ansi'
}

function buildMainRows(form: KeyboardFormFactor, layout: LayoutCode): KeyCap[][] {
  const isIso = layout === 'iso' || layout === 'iso-split'
  const isJis = layout === 'jis'
  const isSplit = layout === 'ansi-split' || layout === 'iso-split'

  const rawRow1 = isJis ? DATA.mainRow1.jis : DATA.mainRow1.standard
  const row1 = form === '60'
    ? rawRow1
    : rawRow1.map((k, idx) => (idx === 0 ? { ...k, label: '~' } : k))

  const variant = isJis ? 'jis' : isIso ? 'iso' : 'ansi'
  const row2 = DATA.mainRow2[variant]
  const row3 = DATA.mainRow3[variant]
  const row4 = DATA.mainRow4[variant]
  const row5 = isJis ? DATA.bottomRows.jis : isSplit ? DATA.bottomRows.split : DATA.bottomRows.standard

  return [row1, row2, row3, row4, row5]
}

export function buildKeyboardLayout(
  formFactor?: KeyboardFormFactor | string | null,
  layoutCode?: string | null,
  model?: string | null,
  layoutLabel?: string | null,
): KeyboardLayoutSpec {
  const form = resolveFormFactor(formFactor, model)
  const resolvedLayout = resolveLayoutCode(layoutCode, layoutLabel, form)

  if (form === 'keypad3') {
    return {
      form,
      layoutCode: 'keypad',
      mainRows: DATA.uwuRows,
      navRows: [],
      numRows: [],
      hasLedBar: false,
    }
  }

  const baseMain = buildMainRows(form, resolvedLayout)
  const mainRows = form === '60' ? baseMain : [DATA.fnRow, ...baseMain]
  const navRows = form === '60' ? [] : form === '80' ? DATA.w80NavRows : DATA.tklNavRows
  const numRows = form === 'full' ? DATA.fullNumRows : []

  return {
    form,
    layoutCode: resolvedLayout,
    mainRows,
    navRows,
    numRows,
    hasLedBar: form === '80',
  }
}
