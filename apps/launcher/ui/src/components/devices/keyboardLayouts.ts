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

function buildBottomRow(layout: LayoutCode): KeyCap[] {
  if (layout === 'jis') {
    return [
      { id: 'lctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
      { id: 'win', label: 'Win', u: 1.25, kind: 'mod' },
      { id: 'lalt', label: 'Alt', u: 1.25, kind: 'mod' },
      { id: 'muhenkan', label: 'Muhen', u: 1.25, kind: 'mod' },
      { id: 'space', label: 'Space', u: 3.75, kind: 'space' },
      { id: 'henkan', label: 'Henk', u: 1.25, kind: 'mod' },
      { id: 'kana', label: 'Kana', u: 1.25, kind: 'mod' },
      { id: 'ralt', label: 'Alt', u: 1.25, kind: 'mod' },
      { id: 'fn', label: 'Fn', u: 1.25, kind: 'mod' },
      { id: 'rctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
    ]
  }

  const splitSpace = layout === 'ansi-split' || layout === 'iso-split'
  const spaceKeys: KeyCap[] = splitSpace
    ? [
        { id: 'space-l', label: 'Space L', u: 2.75, kind: 'space', note: 'Left split spacebar (2.75u)' },
        { id: 'fn-mid', label: 'Fn', u: 1.25, kind: 'mod', note: 'Center split key (1.25u)' },
        { id: 'space-r', label: 'Space R', u: 2.25, kind: 'space', note: 'Right split spacebar (2.25u)' },
      ]
    : [{ id: 'space', label: 'Space', u: 6.25, kind: 'space' }]

  return [
    { id: 'lctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
    { id: 'win', label: 'Win', u: 1.25, kind: 'mod' },
    { id: 'lalt', label: 'Alt', u: 1.25, kind: 'mod' },
    ...spaceKeys,
    { id: 'ralt', label: 'Alt', u: 1.25, kind: 'mod' },
    { id: 'fn', label: 'Fn', u: 1.25, kind: 'mod' },
    { id: 'menu', label: 'Menu', u: 1.25, kind: 'mod' },
    { id: 'rctrl', label: 'Ctrl', u: 1.25, kind: 'mod' },
  ]
}

function buildMainRows(form: KeyboardFormFactor, layout: LayoutCode): KeyCap[][] {
  const isIso = layout === 'iso' || layout === 'iso-split'
  const isJis = layout === 'jis'
  const topLeftLabel = form === '60' ? 'Esc' : '~'

  const row1: KeyCap[] = isJis
    ? [
        { id: 'esc', label: topLeftLabel, u: 1, kind: 'mod' },
        ...['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '^'].map(k => ({ id: k, label: k, u: 1 })),
        { id: 'yen', label: '¥', u: 1 },
        { id: 'bksp', label: 'Bksp', u: 1, kind: 'mod' },
      ]
    : [
        { id: 'esc', label: topLeftLabel, u: 1, kind: 'mod' },
        ...['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '='].map(k => ({ id: k, label: k, u: 1 })),
        { id: 'bksp', label: 'Bksp', u: 2, kind: 'mod' },
      ]

  const row2: KeyCap[] = isIso || isJis
    ? [
        { id: 'tab', label: 'Tab', u: 1.5, kind: 'mod' },
        ...['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P'].map(k => ({
          id: k,
          label: k,
          u: 1,
          kind: k === 'W' ? 'wasd' as const : undefined,
        })),
        { id: '[', label: isJis ? '@' : '[', u: 1 },
        { id: ']', label: isJis ? '[' : ']', u: 1 },
        { id: 'enter-top', label: 'Enter', u: 1.5, kind: 'mod', note: 'ISO Enter key' },
      ]
    : [
        { id: 'tab', label: 'Tab', u: 1.5, kind: 'mod' },
        ...['Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P'].map(k => ({
          id: k,
          label: k,
          u: 1,
          kind: k === 'W' ? 'wasd' as const : undefined,
        })),
        { id: '[', label: '[', u: 1 },
        { id: ']', label: ']', u: 1 },
        { id: '\\', label: '\\', u: 1.5, kind: 'mod' },
      ]

  const row3: KeyCap[] = isIso || isJis
    ? [
        { id: 'caps', label: 'Caps', u: 1.75, kind: 'mod' },
        ...['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L'].map(k => ({
          id: k,
          label: k,
          u: 1,
          kind: k === 'A' || k === 'S' || k === 'D' ? 'wasd' as const : undefined,
        })),
        { id: ';', label: ';', u: 1 },
        { id: "'", label: isJis ? ':' : "'", u: 1 },
        { id: 'iso-hash', label: isJis ? ']' : '#', u: 1 },
        { id: 'enter', label: '↵', u: 1.25, kind: 'mod', note: 'ISO Enter key' },
      ]
    : [
        { id: 'caps', label: 'Caps', u: 1.75, kind: 'mod' },
        ...['A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L'].map(k => ({
          id: k,
          label: k,
          u: 1,
          kind: k === 'A' || k === 'S' || k === 'D' ? 'wasd' as const : undefined,
        })),
        { id: ';', label: ';', u: 1 },
        { id: "'", label: "'", u: 1 },
        { id: 'enter', label: 'Enter', u: 2.25, kind: 'mod' },
      ]

  const row4: KeyCap[] = isIso
    ? [
        { id: 'lshift', label: 'Shift', u: 1.25, kind: 'mod' },
        { id: 'iso-lt', label: '<>', u: 1 },
        ...['Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/'].map(k => ({ id: k, label: k, u: 1 })),
        { id: 'rshift', label: 'Shift', u: 2.75, kind: 'mod' },
      ]
    : isJis
      ? [
          { id: 'lshift', label: 'Shift', u: 2.25, kind: 'mod' },
          ...['Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/'].map(k => ({ id: k, label: k, u: 1 })),
          { id: 'jis-ro', label: '_', u: 1 },
          { id: 'rshift', label: 'Shift', u: 1.75, kind: 'mod' },
        ]
      : [
          { id: 'lshift', label: 'Shift', u: 2.25, kind: 'mod' },
          ...['Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/'].map(k => ({ id: k, label: k, u: 1 })),
          { id: 'rshift', label: 'Shift', u: 2.75, kind: 'mod' },
        ]

  return [row1, row2, row3, row4, buildBottomRow(layout)]
}

const FN_ROW: KeyCap[] = [
  { id: 'f-esc', label: 'Esc', u: 1, kind: 'mod' },
  { id: 'gap-1', label: '', u: 1, kind: 'gap' },
  ...['F1', 'F2', 'F3', 'F4'].map(k => ({ id: k, label: k, u: 1 })),
  { id: 'gap-2', label: '', u: 0.5, kind: 'gap' },
  ...['F5', 'F6', 'F7', 'F8'].map(k => ({ id: k, label: k, u: 1, kind: 'mod' as const })),
  { id: 'gap-3', label: '', u: 0.5, kind: 'gap' },
  ...['F9', 'F10', 'F11', 'F12'].map(k => ({ id: k, label: k, u: 1 })),
]

const TKL_NAV_ROWS: KeyCap[][] = [
  [
    { id: 'prtsc', label: 'Prt', u: 1, kind: 'mod' },
    { id: 'scrlk', label: 'Scr', u: 1, kind: 'mod' },
    { id: 'pause', label: 'Pau', u: 1, kind: 'mod' },
  ],
  [
    { id: 'ins', label: 'Ins', u: 1, kind: 'mod' },
    { id: 'home', label: 'Hm', u: 1, kind: 'mod' },
    { id: 'pgup', label: 'PU', u: 1, kind: 'mod' },
  ],
  [
    { id: 'del', label: 'Del', u: 1, kind: 'mod' },
    { id: 'end', label: 'End', u: 1, kind: 'mod' },
    { id: 'pgdn', label: 'PD', u: 1, kind: 'mod' },
  ],
  [
    { id: 'nav-gap-1', label: '', u: 3, kind: 'gap' },
  ],
  [
    { id: 'nav-gap-2', label: '', u: 1, kind: 'gap' },
    { id: 'up', label: '^', u: 1, kind: 'wasd' },
    { id: 'nav-gap-3', label: '', u: 1, kind: 'gap' },
  ],
  [
    { id: 'left', label: '<', u: 1, kind: 'wasd' },
    { id: 'down', label: 'v', u: 1, kind: 'wasd' },
    { id: 'right', label: '>', u: 1, kind: 'wasd' },
  ],
]

const W80_NAV_ROWS: KeyCap[][] = [
  [
    { id: 'prtsc', label: 'Prt', u: 1, kind: 'mod' },
    { id: 'pause', label: 'Pau', u: 1, kind: 'mod' },
    { id: 'mode', label: 'Mode', u: 1, kind: 'mod', note: 'Profile mode key' },
  ],
  [
    { id: 'ins', label: 'Ins', u: 1, kind: 'mod' },
    { id: 'home', label: 'Hm', u: 1, kind: 'mod' },
    { id: 'pgup', label: 'PU', u: 1, kind: 'mod' },
  ],
  [
    { id: 'del', label: 'Del', u: 1, kind: 'mod' },
    { id: 'end', label: 'End', u: 1, kind: 'mod' },
    { id: 'pgdn', label: 'PD', u: 1, kind: 'mod' },
  ],
  [
    { id: 'led-bar', label: 'LED bar', u: 3, kind: 'led-bar', note: 'Wooting 80HE 10-segment status LED bar' },
  ],
  [
    { id: 'nav-gap-2', label: '', u: 1, kind: 'gap' },
    { id: 'up', label: '^', u: 1, kind: 'wasd' },
    { id: 'nav-gap-3', label: '', u: 1, kind: 'gap' },
  ],
  [
    { id: 'left', label: '<', u: 1, kind: 'wasd' },
    { id: 'down', label: 'v', u: 1, kind: 'wasd' },
    { id: 'right', label: '>', u: 1, kind: 'wasd' },
  ],
]

const FULL_NUM_ROWS: KeyCap[][] = [
  [
    { id: 'a1', label: 'A1', u: 1, kind: 'mod', note: 'Wooting Two analog profile key' },
    { id: 'a2', label: 'A2', u: 1, kind: 'mod', note: 'Wooting Two analog profile key' },
    { id: 'a3', label: 'A3', u: 1, kind: 'mod', note: 'Wooting Two analog profile key' },
    { id: 'mode', label: 'Mode', u: 1, kind: 'mod', note: 'Profile mode key' },
  ],
  [
    { id: 'numlk', label: 'Num', u: 1, kind: 'mod' },
    { id: 'kp/', label: '/', u: 1, kind: 'mod' },
    { id: 'kp*', label: '*', u: 1, kind: 'mod' },
    { id: 'kp-', label: '-', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp7', label: '7', u: 1 },
    { id: 'kp8', label: '8', u: 1 },
    { id: 'kp9', label: '9', u: 1 },
    { id: 'kp+', label: '+', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp4', label: '4', u: 1 },
    { id: 'kp5', label: '5', u: 1 },
    { id: 'kp6', label: '6', u: 1 },
    { id: 'kpeq', label: '+', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp1', label: '1', u: 1 },
    { id: 'kp2', label: '2', u: 1 },
    { id: 'kp3', label: '3', u: 1 },
    { id: 'kpent', label: 'Ent', u: 1, kind: 'mod' },
  ],
  [
    { id: 'kp0', label: '0', u: 2 },
    { id: 'kp.', label: '.', u: 1 },
    { id: 'kpret', label: '↵', u: 1, kind: 'mod' },
  ],
]

const UWU_ROWS: KeyCap[][] = [
  [
    { id: 'uwu-m1', label: 'Menu 1', u: 1, kind: 'mod', note: 'UwU top utility button' },
    { id: 'uwu-m2', label: 'Menu 2', u: 1, kind: 'mod', note: 'UwU top utility button' },
    { id: 'uwu-m3', label: 'Menu 3', u: 1, kind: 'mod', note: 'UwU top utility button' },
    { id: 'uwu-mode', label: 'Mode', u: 1, kind: 'mod', note: 'Profile switch button' },
  ],
  [
    { id: 'uwu-k1', label: 'Key 1', u: 1.33, kind: 'analog', note: 'Primary Hall-effect key 1' },
    { id: 'uwu-k2', label: 'Key 2', u: 1.34, kind: 'analog', note: 'Primary Hall-effect key 2' },
    { id: 'uwu-k3', label: 'Key 3', u: 1.33, kind: 'analog', note: 'Primary Hall-effect key 3' },
  ],
]

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
      mainRows: UWU_ROWS,
      navRows: [],
      numRows: [],
      hasLedBar: false,
    }
  }

  const baseMain = buildMainRows(form, resolvedLayout)
  const mainRows = form === '60' ? baseMain : [FN_ROW, ...baseMain]
  const navRows = form === '60' ? [] : form === '80' ? W80_NAV_ROWS : TKL_NAV_ROWS
  const numRows = form === 'full' ? FULL_NUM_ROWS : []

  return {
    form,
    layoutCode: resolvedLayout,
    mainRows,
    navRows,
    numRows,
    hasLedBar: form === '80',
  }
}
