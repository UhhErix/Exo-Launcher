import assert from 'node:assert/strict'
import { test } from 'node:test'
import { firstScreen, nextFlow, resetEmail, setupSkipsAccount, type AccountScreen, type FlowEvent, type FlowState } from '../src/lib/accountFlow.ts'

// The account step's screen table (lib/accountFlow.ts). Ways it could fail: anything but typing the code back leaves the
// code screen (a refresh, Back, Escape, a second submit), so a code the player never saved is lost; a form sends twice;
// an answer lands on the wrong screen; a created account with no code in the answer goes back to the create form; the
// new-code step lets the player go back and make a second account; setup asks a signed-in PC again.

const SCREENS: AccountScreen[] = ['choose', 'create', 'signin', 'reset', 'newCode', 'code', 'offline', 'signedIn']
const EVENTS: FlowEvent[] = [
  ...(['choose', 'create', 'signin', 'reset', 'newCode'] as const).map(to => ({ type: 'open', to }) as FlowEvent),
  { type: 'submit' },
  ...(['code', 'noCode', 'signedIn', 'offline', 'refused'] as const).map(result => ({ type: 'answer', result }) as FlowEvent),
  { type: 'confirm' },
  { type: 'signedOut' },
]
const FORMS: AccountScreen[] = ['create', 'signin', 'reset', 'newCode']
const OPENS_FROM: AccountScreen[] = ['choose', 'create', 'signin', 'reset', 'offline']
const ANSWERS: Record<string, AccountScreen | 'same'> = { code: 'code', noCode: 'newCode', signedIn: 'signedIn', offline: 'offline', refused: 'same' }

/** The table written out row by row, independently of the code under test. */
function expected(state: FlowState, event: FlowEvent): FlowState {
  if (state.screen === 'code') return event.type === 'confirm' ? { screen: 'signedIn', busy: false } : state
  if (state.screen === 'signedIn') return state
  if (state.busy) {
    if (event.type !== 'answer') return state
    const to = ANSWERS[event.result]
    return { screen: to === 'same' ? state.screen : to, busy: false }
  }
  if (event.type === 'submit') return FORMS.includes(state.screen) ? { ...state, busy: true } : state
  if (event.type === 'signedOut') return ['newCode', 'offline'].includes(state.screen) ? { screen: 'choose', busy: false } : state
  if (event.type === 'open') {
    if (state.screen === 'newCode') return event.to === 'reset' ? { screen: 'reset', busy: false } : state
    return OPENS_FROM.includes(state.screen) ? { screen: event.to, busy: false } : state
  }
  return state
}

test('every screen, busy or not, against every event, matches the table', () => {
  let rows = 0
  for (const screen of SCREENS) for (const busy of [false, true]) for (const event of EVENTS) {
    const state = { screen, busy }
    assert.deepEqual(nextFlow(state, event), expected(state, event), `${screen}${busy ? ' (busy)' : ''} + ${JSON.stringify(event)}`)
    rows++
  }
  assert.equal(rows, SCREENS.length * 2 * EVENTS.length)
})

test('the code screen has one way out: confirm', () => {
  const code: FlowState = { screen: 'code', busy: false }
  for (const event of EVENTS) {
    const next = nextFlow(code, event)
    if (event.type === 'confirm') assert.deepEqual(next, { screen: 'signedIn', busy: false })
    else assert.equal(next, code, JSON.stringify(event))
  }
})

test('a sign-up walks choose, create, busy, code, signed in; a sign-up answer without a code goes to a new code', () => {
  let state: FlowState = { screen: firstScreen(false), busy: false }
  state = nextFlow(state, { type: 'open', to: 'create' })
  state = nextFlow(state, { type: 'submit' })
  assert.deepEqual(state, { screen: 'create', busy: true })
  assert.deepEqual(nextFlow(state, { type: 'submit' }), state, 'a second press sends nothing')
  assert.deepEqual(nextFlow(state, { type: 'answer', result: 'noCode' }), { screen: 'newCode', busy: false })
  state = nextFlow(state, { type: 'answer', result: 'code' })
  assert.equal(state.screen, 'code')
  assert.equal(nextFlow(state, { type: 'confirm' }).screen, 'signedIn')
})

test('a code issued and never typed back starts on a new code, which cannot go back to make another account', () => {
  assert.equal(firstScreen(true), 'newCode')
  assert.equal(firstScreen(undefined), 'choose')
  const fresh: FlowState = { screen: 'newCode', busy: false }
  for (const to of ['choose', 'create', 'signin'] as const) assert.equal(nextFlow(fresh, { type: 'open', to }), fresh)
  // Offline from the new code: Try again goes back to it.
  assert.deepEqual(nextFlow({ screen: 'offline', busy: false }, { type: 'open', to: 'newCode' }), fresh)
})

test('the new-code step is never a dead end: Forgot your password? resets, Sign out of this PC goes back to the choice', () => {
  const fresh: FlowState = { screen: 'newCode', busy: false }
  assert.deepEqual(nextFlow(fresh, { type: 'open', to: 'reset' }), { screen: 'reset', busy: false })
  assert.deepEqual(nextFlow(fresh, { type: 'signedOut' }), { screen: 'choose', busy: false })
  // Back from that reset goes back to the new code; a reset there shows its new code, held like any other.
  assert.deepEqual(nextFlow({ screen: 'reset', busy: false }, { type: 'open', to: 'newCode' }), fresh)
  let state = nextFlow({ screen: 'reset', busy: false }, { type: 'submit' })
  state = nextFlow(state, { type: 'answer', result: 'code' })
  assert.equal(state.screen, 'code')
  // Offline from the new code can sign out too.
  assert.deepEqual(nextFlow({ screen: 'offline', busy: false }, { type: 'signedOut' }), { screen: 'choose', busy: false })
  // Nowhere else does a sign-out event move the step.
  for (const screen of ['choose', 'create', 'signin', 'reset', 'code', 'signedIn'] as const) {
    assert.deepEqual(nextFlow({ screen, busy: false }, { type: 'signedOut' }), { screen, busy: false })
  }
})

test('every screen has a way on: from each, some event reaches the end of the step (signed in) or the choice', () => {
  const reaches = (start: FlowState) => {
    const seen = new Set<string>()
    const queue = [start]
    while (queue.length) {
      const state = queue.shift()!
      if (state.screen === 'signedIn' || (state.screen === 'choose' && start.screen !== 'choose')) return true
      const key = `${state.screen}:${state.busy}`
      if (seen.has(key)) continue
      seen.add(key)
      for (const event of EVENTS) queue.push(nextFlow(state, event))
    }
    return false
  }
  for (const screen of SCREENS) for (const busy of [false, true]) {
    if (screen === 'signedIn') continue
    assert.ok(reaches({ screen, busy }), `${screen}${busy ? ' (busy)' : ''} is a dead end`)
  }
})
test('setup skips the account step only on the choice, signed in, with no code to confirm', () => {
  assert.equal(setupSkipsAccount({ signedIn: true, codePending: false }, 'choose'), true)
  assert.equal(setupSkipsAccount({ signedIn: true, codePending: true }, 'choose'), false)
  assert.equal(setupSkipsAccount({ signedIn: false, codePending: false }, 'choose'), false)
  assert.equal(setupSkipsAccount({ signedIn: true, codePending: false }, 'code'), false)
  assert.equal(setupSkipsAccount(null, 'choose'), false)
})

test('the reset form opened from the new-code step starts with the account email, and never overwrites what was typed', () => {
  assert.equal(resetEmail('', 'newCode', 'me@example.com'), 'me@example.com', 'signed in and nothing typed: the account email')
  assert.equal(resetEmail('  ', 'newCode', ' me@example.com '), 'me@example.com', 'blanks are not an email')
  assert.equal(resetEmail('typed@example.com', 'newCode', 'me@example.com'), 'typed@example.com', 'what the player typed stays')
  assert.equal(resetEmail('', 'newCode', null), '', 'no email known: an empty field')
  assert.equal(resetEmail('', 'newCode', undefined), '')
  assert.equal(resetEmail('', 'signin', 'me@example.com'), '', 'from the sign-in form the field is only what was typed there')
  assert.equal(resetEmail('typed@example.com', 'signin', 'other@example.com'), 'typed@example.com')
})
