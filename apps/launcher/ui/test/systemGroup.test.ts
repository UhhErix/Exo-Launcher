// Best settings › System row actions (src/components/gpu/systemGroupModel.ts, SystemGroup.tsx) and first-run tuning wiring. `npm test`.
//
// How it could fail: a partially applied item with on=false and canUndo=true hides its Put back control so the user can
// only retry applying instead of reverting; sending Put back calls 'on' instead of 'off'; OnboardingPanel omits tuning.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { systemItemCanPutBack, systemTuningNeedsRestart } from '../src/components/gpu/systemGroupModel.ts'

const groupSource = readFileSync(new URL('../src/components/gpu/SystemGroup.tsx', import.meta.url), 'utf8')
const autopilotSource = readFileSync(new URL('../src/components/gpu/Autopilot.tsx', import.meta.url), 'utf8')
const bestSetupSource = readFileSync(new URL('../src/components/gpu/BestSetup.tsx', import.meta.url), 'utf8')
const onboardingSource = readFileSync(new URL('../src/components/OnboardingPanel.tsx', import.meta.url), 'utf8')
const eqPanelsSource = readFileSync(new URL('../src/components/devices/EqPanels.tsx', import.meta.url), 'utf8')
const audioPageSource = readFileSync(new URL('../src/components/devices/AudioPage.tsx', import.meta.url), 'utf8')

test('an off item that keeps an undo record offers Put back', () => {
  assert.equal(systemItemCanPutBack({ on: false, canUndo: true }), true)
  assert.equal(systemItemCanPutBack({ on: true, canUndo: true }), false)
  assert.equal(systemItemCanPutBack({ on: false, canUndo: false }), false)
})

test('SystemGroup renders Put back via systemItemCanPutBack, calls host.systemTuning directly, and OnboardingPanel passes tuning to bestSteps', () => {
  assert.match(groupSource, /systemItemCanPutBack\(item\)/)
  assert.match(groupSource, /await host\.systemTuning\(on \? 'on' : 'off', item\.key\)/)
  assert.match(groupSource, /onClick=\{\(\) => void run\(item, false\)\}>Put back</)
  assert.match(onboardingSource, /host\.systemTuning\(\)/)
  assert.match(onboardingSource, /bestSteps\(gpu, health, screen, settings, saveSettings, drives, tuning\)/)
})

test('systemTuningNeedsRestart and BestSetup omit restart wording when items have needsRestart false', () => {
  assert.equal(systemTuningNeedsRestart([{ on: false, restart: false, needsRestart: false }]), false)
  assert.equal(systemTuningNeedsRestart([{ on: false, restart: false, needsRestart: true }]), true)
  assert.equal(systemTuningNeedsRestart([{ on: true, restart: true, needsRestart: true }]), true)
  assert.equal(systemTuningNeedsRestart([{ on: false, restart: false }]), true)
  assert.match(bestSetupSource, /systemTuningNeedsRestart\(items\)/)
})

test('EqPanels gates its 4s waiting poll on visible and pageIsActive()', () => {
  assert.match(eqPanelsSource, /if \(!waiting \|\| !visible \|\| !active \|\| !pageIsActive\(\)\) return/)
})

test('SystemGroup omits integrity, mouseAccel, and unapplied vrr', () => {
  assert.match(groupSource, /item\.key !== 'integrity' && item\.key !== 'mouseAccel' && \(item\.key !== 'vrr' \|\| item\.canUndo\)/)
})

test('Autopilot has no on/off switch or manual priority picker and renders memoryIntegrity', () => {
  assert.doesNotMatch(autopilotSource, /id="autopilot-on"/)
  assert.doesNotMatch(autopilotSource, /id="autopilot-priority"/)
  assert.match(autopilotSource, /state\.memoryIntegrity/)
})

test('Exo EQ removes GamesPanel and manual sound/custom controls for zero-config game profiles', () => {
  assert.doesNotMatch(eqPanelsSource, /export function GamesPanel/)
  assert.doesNotMatch(eqPanelsSource, /<SliderRow\b/)
  assert.doesNotMatch(eqPanelsSource, /<ChoiceRow\b/)
  assert.match(eqPanelsSource, /<State on>Auto<\/State>/)
  assert.doesNotMatch(audioPageSource, /<GamesPanel\b/)
})
