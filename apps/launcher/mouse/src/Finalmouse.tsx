import { useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { FINALMOUSE_INTENT_TRIGGER, finalmouseIsUltraclick } from '@openmouse/protocol/finalmouse'
import { can } from './mouse'
import type { Section } from './section'
import { ColorField, Panel, Row, Segmented, Slider, Switch } from './ui'
import {
  buildClickModeArgs,
  clickChoiceOf,
  CUSTOM_LOD_FIRMWARE,
  finalmouseIntentSupport,
  finalmouseSmartRtAlwaysOn,
  finalmouseUltraclickSupport,
  firmwarePartAtLeast,
  LATE_FIRMWARE,
  nextIntentForChoice,
  parseFirmware,
  ULTRACLICK_ACTUATION_STEPS,
  type ClickChoice,
} from './finalmouse-state'

/**
 * Finalmouse facts from xpanel (xpanel.finalmouse.com, read with REA):
 * actuation 0.01-0.40 mm (default 0.25), rapid trigger 150-250 µm (default
 * 220), release point Normal 0 / Early 1 / Late 2 per side, Late only on
 * firmware 1.1.1 or newer, custom lift-off 1.0-1.7 mm on firmware 1.1.0 or
 * newer, receiver light Off 0 / Battery 1 / Custom color 2 / TMR-DS 4. A
 * side whose actuation reads 0 is not calibrated and cannot go analog.
 */
const ACTUATION_DEFAULT = 25
const RT_DEFAULT = 220

// xpanel's own names: TMR Analog, Smart Rapid Trigger, Actuation point, Release point, the TMR-DS panel.
const CLICK_UNREAD = [{ value: 0, label: 'Mechanical' }, { value: 1, label: 'TMR Analog' }] as const
const CLICK_BASE = [{ value: 'mechanical' as ClickChoice, label: 'Mechanical' }, { value: 'analog' as ClickChoice, label: 'TMR Analog' }] as const
const CLICK_WITH_ULTRA = [...CLICK_BASE, { value: 'ultraclick' as ClickChoice, label: 'Ultraclick' }] as const
const RELEASE = [{ value: 0, label: 'Normal' }, { value: 1, label: 'Early' }, { value: 2, label: 'Late' }] as const

function firmwareAtLeast(status: MouseStatus, version: string): boolean | null {
  return firmwarePartAtLeast(status, 'mouse', version)
}

/** A Starlight X on its cable (361d:310, 311) keeps the profiles saved for it on its receiver (361d:300, 301). */
export function finalmouseProfileKey(id: string): string {
  return id === '361d:310' ? '361d:300' : id === '361d:311' ? '361d:301' : id
}

/**
 * Why a Starlight X setting shows no value: the mouse did not answer the read (asleep or out of
 * range: it reports its firmware when it does), or its firmware does not report that setting.
 */
export function finalmouseUnread(status: MouseStatus): string {
  return parseFirmware(status).has('mouse') ? 'Not reported by this firmware' : 'Move the mouse to read it'
}

/**
 * The Starlight X's custom lift-off, 1.0-1.7 mm: offered unless the mouse's firmware is known to be
 * older than it (then `needs` says which it needs), and always when it is already set.
 */
export function finalmouseCustomLod(status: MouseStatus): { offered: boolean; needs: string | null } {
  const offered = !!status.finalmousePawLodCustom || firmwareAtLeast(status, CUSTOM_LOD_FIRMWARE) !== false
  return { offered, needs: offered ? null : `Custom needs firmware ${CUSTOM_LOD_FIRMWARE}` }
}

/**
 * The Starlight X's own settings: its TMR analog main switches (mode,
 * actuation, rapid trigger and release point per side), onboard profiles,
 * and the receiver's light and Bhop mode.
 */
export function FinalmousePanels(section: Section) {
  if (!section.status.finalmouseIsSlx) return null
  return (
    <>
      <Switches {...section} />
      <Receiver {...section} />
    </>
  )
}

const TOGETHER = [{ value: 'both', label: 'Together' }, { value: 'apart', label: 'Separate' }] as const

type Side = {
  mode: number
  calibrated: boolean
  thr: number
  rt: number
  rel: number | null | undefined
  intent: number | null | undefined
  choice: ClickChoice
  ultraclick: boolean
  intentOn: boolean
}
type SideSetters = {
  click: (v: ClickChoice) => void
  thr: (v: number) => void
  rt: (v: number) => void
  rel: (v: number) => void
  intent: (on: boolean) => void
}

/**
 * The main switches. Both sides set alike show as one set of rows (Together,
 * as Exo's magnetic switches do), written to both sides at once; Separate
 * gives each side its own. A side that is not calibrated stays mechanical, so
 * the sides can only go together when both are.
 */
function Switches({ status, client, apply, busy }: Section) {
  const [pick, setPick] = useState<'both' | 'apart' | null>(null)
  if (!can(client, 'setClickMode')) return null
  const modeL = status.finalmouseClickModeL
  const modeR = status.finalmouseClickModeR
  const mask = status.finalmouseProfileEnabledMask
  const profiles = Array.from({ length: status.finalmouseProfileCount ?? 0 }, (_, i) => i)
    .filter(i => mask == null || (mask & (1 << i)) !== 0 || i === status.finalmouseProfileActive)
  const profileRow = profiles.length > 1 && can(client, 'setActiveProfile') ? (
    <Row label="Onboard profile">
      <Segmented
        label="Onboard profile"
        options={profiles.map(i => ({ value: i, label: status.finalmouseProfileNames?.[i] || String(i + 1) }))}
        value={status.finalmouseProfileActive}
        onChange={v => void apply('setActiveProfile', v)}
        disabled={busy}
      />
    </Row>
  ) : null
  const unread = finalmouseUnread(status)
  // Modes not read (the mouse asleep at the read, or a firmware that does not report them): both sides
  // can still be set together, and the read after the write shows the rest.
  if (modeL == null || modeR == null) {
    return (
      <Panel title="TMR-DS">
        <Row label="Click" value={unread}>
          <Segmented label="Click" options={CLICK_UNREAD} value={null} onChange={v => void apply('setClickMode', v, v)} disabled={busy} />
        </Row>
        {profileRow}
      </Panel>
    )
  }
  const relL = status.finalmouseClickReleaseL
  const relR = status.finalmouseClickReleaseR
  const intL = status.finalmouseClickIntentL
  const intR = status.finalmouseClickIntentR
  const intentSupport = finalmouseIntentSupport(status)
  const ultraclickSupport = finalmouseUltraclickSupport(status)
  const clickOptions = ultraclickSupport.supported ? CLICK_WITH_ULTRA : CLICK_BASE
  const makeSide = (mode: number, thr: number | null | undefined, hyst: number | null | undefined, rel: number | null | undefined, intent: number | null | undefined): Side => {
    const ultraclick = mode === 1 && finalmouseIsUltraclick(intent)
    return {
      mode,
      calibrated: thr !== 0,
      thr: thr || ACTUATION_DEFAULT,
      rt: hyst ?? RT_DEFAULT,
      rel,
      intent,
      choice: clickChoiceOf(mode, intent),
      ultraclick,
      intentOn: ultraclick ? true : intent === FINALMOUSE_INTENT_TRIGGER.on,
    }
  }
  const left = makeSide(modeL, status.finalmouseTmrThrL, status.finalmouseTmrHystL, relL, intL)
  const right = makeSide(modeR, status.finalmouseTmrThrR, status.finalmouseTmrHystR, relR, intR)
  const releases = relL != null && relR != null
  // Late: on firmware 1.1.1 or newer, or when the mouse's firmware is not known; dimmed, with why, on older.
  const lateNeeds = relL !== 2 && relR !== 2 && firmwareAtLeast(status, LATE_FIRMWARE) === false ? `Late needs firmware ${LATE_FIRMWARE}` : null
  const smartRtAlwaysOn = finalmouseSmartRtAlwaysOn(status)
  const release = lateNeeds ? RELEASE.map(o => ({ ...o, disabled: o.value === 2 })) : RELEASE
  const actuation = can(client, 'setTmrActuation')
  const tmr = (l: number, r: number, hl?: number, hr?: number) => (
    hl != null && hr != null
      ? void apply('setTmrActuation', l / 100, r / 100, hl, hr)
      : void apply('setTmrActuation', l / 100, r / 100)
  )
  // The mouse takes both sides in one command. Release points ride along once it reported them or one
  // is picked (the other side Normal); a firmware without them reads back none, and the row says so.
  const setModes = (l: number, r: number, rl = relL, rr = relR, il = intL, ir = intR) =>
    void apply('setClickMode', ...buildClickModeArgs(l, r, rl, rr, il, ir))
  const modeNum = (c: ClickChoice) => (c === 'mechanical' ? 0 : 1)
  const intentBit = (on: boolean) => (on ? FINALMOUSE_INTENT_TRIGGER.on : FINALMOUSE_INTENT_TRIGGER.off)
  const pairable = left.calibrated && right.calibrated
  const alike = left.choice === right.choice
    && (left.choice === 'mechanical' || (
      left.rel === right.rel
      && left.rt === right.rt
      && (left.choice === 'ultraclick' || (left.thr === right.thr && left.intentOn === right.intentOn))
    ))
  const together = pairable && (pick ?? (alike ? 'both' : 'apart')) === 'both'

  const rows = (name: string, side: Side, set: SideSetters) => {
    const label = (what: string) => (name ? `${name} ${what.toLowerCase()}` : what)
    const controlled = side.ultraclick ? 'Controlled by Ultraclick' : undefined
    return (
      <>
        <Row label={label('Click')}>
          {side.calibrated
            ? <Segmented label={label('Click')} options={clickOptions} value={side.choice} onChange={set.click} disabled={busy} />
            : <span className="m-state">Mechanical</span>}
        </Row>
        {side.mode === 1 && actuation ? (
          <>
            <Row label={label('Actuation point')} value={controlled}>
              <Slider label={label('Actuation point')} min={1} max={40} step={1} value={side.ultraclick ? ULTRACLICK_ACTUATION_STEPS : side.thr}
                format={v => `${(v / 100).toFixed(2)} mm`} onCommit={set.thr} disabled={busy || side.ultraclick} />
            </Row>
            {intentSupport.supported ? (
              <Row label={label('Intent Trigger')} value={controlled ?? (side.intent == null ? unread : undefined)}>
                {side.ultraclick
                  ? <span className="m-state">On</span>
                  : <Switch label={label('Intent Trigger')} checked={side.intentOn} onChange={set.intent} disabled={busy || side.intent == null} />}
              </Row>
            ) : null}
            <Row label={label('Smart Rapid Trigger')} value={!smartRtAlwaysOn && status.finalmouseTmrHystL == null ? unread : undefined}>
              {smartRtAlwaysOn
                ? <span className="m-state">Always on</span>
                : <Slider label={label('Smart Rapid Trigger')} min={150} max={250} step={10} value={side.rt} format={v => `${v} µm`}
                    onCommit={set.rt} disabled={busy} />}
            </Row>
            <Row label={label('Release point')} value={releases ? lateNeeds ?? undefined : unread}>
              <Segmented label={label('Release point')} options={release} value={side.rel} onChange={set.rel} disabled={busy} />
            </Row>
          </>
        ) : null}
      </>
    )
  }

  return (
    <Panel title="TMR-DS">
      {pairable ? (
        <Row label="Sides">
          <Segmented label="Sides" options={TOGETHER} value={together ? 'both' : 'apart'} onChange={setPick} />
        </Row>
      ) : null}
      {together ? rows('', left, {
        // Together writes the shown (left) values to both sides.
        click: v => {
          const nextInt = nextIntentForChoice(v, left.intent)
          setModes(modeNum(v), modeNum(v), relL, relL, nextInt, nextInt)
        },
        thr: v => smartRtAlwaysOn ? tmr(v, v) : tmr(v, v, left.rt, left.rt),
        rt: v => tmr(left.thr, left.thr, v, v),
        rel: v => setModes(left.mode, left.mode, v, v, left.intent, left.intent),
        intent: on => setModes(left.mode, left.mode, relL, relL, intentBit(on), intentBit(on)),
      }) : (
        <>
          {rows('Left', left, {
            click: v => setModes(modeNum(v), modeR, relL, relR, nextIntentForChoice(v, left.intent), right.intent),
            thr: v => smartRtAlwaysOn ? tmr(v, right.thr) : tmr(v, right.thr, left.rt, right.rt),
            rt: v => tmr(left.thr, right.thr, v, right.rt),
            rel: v => setModes(modeL, modeR, v, relR, intL, intR),
            intent: on => setModes(modeL, modeR, relL, relR, intentBit(on), right.intent),
          })}
          {rows('Right', right, {
            click: v => setModes(modeL, modeNum(v), relL, relR, left.intent, nextIntentForChoice(v, right.intent)),
            thr: v => smartRtAlwaysOn ? tmr(left.thr, v) : tmr(left.thr, v, left.rt, right.rt),
            rt: v => tmr(left.thr, right.thr, left.rt, v),
            rel: v => setModes(modeL, modeR, relL, v, intL, intR),
            intent: on => setModes(modeL, modeR, relL, relR, left.intent, intentBit(on)),
          })}
        </>
      )}
      {!ultraclickSupport.supported && ultraclickSupport.needs ? (
        <Row label="Ultraclick" value={ultraclickSupport.needs}>
          <span className="m-state">Update firmware</span>
        </Row>
      ) : null}
      {profileRow}
    </Panel>
  )
}

const LIGHT = [{ value: 0, label: 'Off' }, { value: 1, label: 'Battery level' }, { value: 2, label: 'Custom color' }, { value: 4, label: 'TMR-DS' }] as const
const BHOP = [{ value: 0, label: 'Off' }, { value: 1, label: 'Scroll up' }, { value: 2, label: 'Scroll down' }, { value: 3, label: 'Both' }] as const
const PASS = [{ value: 100, label: '100 ms' }, { value: 500, label: '500 ms' }, { value: 1000, label: '1 s' }, { value: 1500, label: '1.5 s' }] as const

type Rgb = { r: number; g: number; b: number }
/** The receiver's custom color (command 54), which the patched driver reads; see scripts/patch-protocol.mjs. */
export const indicatorColor = (status: MouseStatus) => (status as unknown as { finalmouseIndicatorColor?: Rgb | null }).finalmouseIndicatorColor ?? null
const hex = ({ r, g, b }: Rgb) => `#${[r, g, b].map(v => v.toString(16).padStart(2, '0')).join('')}`
const rgb = (value: string): [number, number, number] => [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16)) as [number, number, number]

/**
 * The receiver's light, Bhop mode and its pass-through window; on the cable there is no receiver,
 * so Bhop mode alone. A value the mouse did not report shows with nothing picked and why, and can
 * still be set.
 */
function Receiver({ status, client, apply, busy }: Section) {
  const color = indicatorColor(status)
  const unread = finalmouseUnread(status)
  const cabled = status.connectionType === 'Wired'
  const rows = [
    !cabled && can(client, 'setDongleLedMode') && (
      // The custom color sits in the light's own row, beside the pill, while Custom is on.
      <Row key="led" label="Receiver light" value={status.finalmouseDongleLedMode == null ? unread : undefined}>
        <span className="m-inline">
          {status.finalmouseDongleLedMode === 2 && color && can(client, 'setIndicatorColor') ? (
            <ColorField label="Light color" value={hex(color)} disabled={busy} onCommit={v => void apply('setIndicatorColor', ...rgb(v))} />
          ) : null}
          <Segmented label="Receiver light" options={LIGHT} value={status.finalmouseDongleLedMode} onChange={v => void apply('setDongleLedMode', v)} disabled={busy} />
        </span>
      </Row>
    ),
    can(client, 'setTournamentScrollMode') && (
      <Row key="scroll" label="Bhop mode" value={status.finalmouseTournamentScrollMode == null ? unread : undefined}>
        <Segmented label="Bhop mode" options={BHOP} value={status.finalmouseTournamentScrollMode} onChange={v => void apply('setTournamentScrollMode', v)} disabled={busy} />
      </Row>
    ),
    !!status.finalmouseTournamentScrollMode && can(client, 'setTournamentScrollTimeout') && (
      <Row key="pass" label="Pass-through window" value={status.finalmouseTournamentScrollTimeoutMs == null ? unread : undefined}>
        <Segmented label="Pass-through window" options={PASS} value={status.finalmouseTournamentScrollTimeoutMs} onChange={v => void apply('setTournamentScrollTimeout', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title={cabled ? 'Scroll wheel' : 'Receiver'}>{rows}</Panel> : null
}
