import assert from 'node:assert/strict'
import { test } from 'node:test'
import { accountGateDue, accountSheetOpen } from '../src/lib/accountPrompt.ts'
import { accountRequest, accountStore, FIRST_READ_RETRY_MS, HANDOVER_MS, watchAccount } from '../src/lib/accountStore.ts'

// The page's one account state (lib/accountStore.ts). Ways it could fail: the shell's gate keeps its own copy and asks a
// PC that setup just signed in; Continue without an account in setup is forgotten by the shell, so it asks twice in a run;
// a session that ended is never read again, so the gate stays shut; an older read answering late puts a stale state back;
// a failed read wipes what is known.

type Overview = { signedIn: boolean; codePending?: boolean }

/** A host whose answers the test hands out one by one, in any order. */
function scriptedHost() {
  const pending: { resolve: (value: Overview) => void; reject: (error: unknown) => void }[] = []
  const load = () => new Promise<Overview>((resolve, reject) => { pending.push({ resolve, reject }) })
  return { load, pending }
}

const gateOpen = (view: { overview: Overview | null; laterThisRun: boolean }) => accountGateDue(view.overview && { signedIn: view.overview.signedIn, codePending: view.overview.codePending ?? false }, view.laterThisRun)

test('start: nothing is read yet, so the gate stays shut instead of flashing over a signed-in PC', () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  assert.deepEqual(store.get(), { overview: null, laterThisRun: false, codesOnScreen: 0, requests: 0 })
  assert.equal(gateOpen(store.get()), false)
})

test('a sign-in in setup is seen by the shell: one refresh after it updates every reader, and the gate stays shut', async () => {
  const hostAnswers = scriptedHost()
  const store = accountStore(hostAnswers.load, () => undefined)
  // The shell is mounted while setup runs and reads the PC as signed out.
  const first = store.refresh()
  hostAnswers.pending[0].resolve({ signedIn: false })
  await first
  const shellSaw: boolean[] = []
  store.subscribe(() => shellSaw.push(gateOpen(store.get())))
  assert.equal(gateOpen(store.get()), true)

  // Setup signs in; the host's exoAccount.changed (or setup's own onDone) refreshes the one state.
  const after = store.refresh()
  hostAnswers.pending[1].resolve({ signedIn: true })
  await after
  assert.equal(gateOpen(store.get()), false)
  assert.deepEqual(shellSaw, [false])
})

test('Continue without an account in setup holds for the shell for the rest of the run, and is said once', () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  let told = 0
  store.subscribe(() => told++)
  store.continueOffline()
  store.continueOffline()
  assert.equal(store.get().laterThisRun, true)
  assert.equal(told, 1)
})

test('a session the service ended opens the gate when the host says the session changed', async () => {
  const hostAnswers = scriptedHost()
  const store = accountStore(hostAnswers.load, () => undefined)
  const signedIn = store.refresh()
  hostAnswers.pending[0].resolve({ signedIn: true })
  await signedIn
  assert.equal(gateOpen(store.get()), false)

  const ended = store.refresh()
  hostAnswers.pending[1].resolve({ signedIn: false })
  await ended
  assert.equal(gateOpen(store.get()), true)
})

test('an older read that answers late never replaces a newer one', async () => {
  const hostAnswers = scriptedHost()
  const store = accountStore(hostAnswers.load, () => undefined)
  const older = store.refresh()
  const newer = store.refresh()
  hostAnswers.pending[1].resolve({ signedIn: true })
  await newer
  hostAnswers.pending[0].resolve({ signedIn: false })
  await older
  assert.deepEqual(store.get().overview, { signedIn: true })
})

test('a failed read is reported and keeps what was known', async () => {
  const hostAnswers = scriptedHost()
  const errors: unknown[] = []
  const store = accountStore(hostAnswers.load, (error) => errors.push(error))
  const known = store.refresh()
  hostAnswers.pending[0].resolve({ signedIn: true })
  await known
  const failed = store.refresh()
  hostAnswers.pending[1].reject(new Error('host is busy'))
  await failed
  assert.equal(errors.length, 1)
  assert.deepEqual(store.get().overview, { signedIn: true })
})

test('a reader that left is not told any more', async () => {
  const hostAnswers = scriptedHost()
  const store = accountStore(hostAnswers.load, () => undefined)
  let told = 0
  const leave = store.subscribe(() => told++)
  leave()
  const read = store.refresh()
  hostAnswers.pending[0].resolve({ signedIn: true })
  await read
  assert.equal(told, 0)
})

/** A fake onHostEvent: the test fires exoAccount.changed itself. */
function fakeEvents() {
  const handlers = new Set<() => void>()
  return {
    on: (_event: 'exoAccount.changed', handler: () => void) => { handlers.add(handler); return () => { handlers.delete(handler) } },
    fire: () => { for (const handler of handlers) handler() },
    count: () => handlers.size,
  }
}

const flush = () => new Promise(resolve => setImmediate(resolve))

test('the regression, through the store alone: a sign-up saves the session, exoAccount.changed refreshes it, and the sheet stays up on the code until it is typed back', async () => {
  const hostAnswers = scriptedHost()
  const store = accountStore(hostAnswers.load, () => undefined)
  const events = fakeEvents()
  watchAccount(store, events.on)
  assert.equal(events.count(), 1, 'watchAccount listens to exoAccount.changed')
  hostAnswers.pending[0].resolve({ signedIn: false, codePending: false })
  await flush()

  // The shell: the sheet's state a moment ago, and what it is now (accountSheetOpen), as useAccountShell keeps it.
  let up = false
  const sheet = () => {
    const view = store.get()
    up = accountSheetOpen({
      wasOpen: up, whatsNewOpen: false, laterThisRun: view.laterThisRun, codesOnScreen: view.codesOnScreen, requests: view.requests,
      overview: view.overview && { signedIn: view.overview.signedIn, codePending: view.overview.codePending ?? false },
    })
    return up
  }
  assert.equal(sheet(), true)

  // The step sends the sign-up through accountRequest (as AccountStep does). The host saves the session and says so before the answer.
  let answer!: (value: { recoveryCode: string }) => void
  const sent = accountRequest(store, () => new Promise<{ recoveryCode: string }>(resolve => { answer = resolve }), result => result.recoveryCode)
  events.fire()
  hostAnswers.pending[1].resolve({ signedIn: true, codePending: true })
  await flush()
  assert.equal(sheet(), true, 'the request on its way holds it')
  answer({ recoveryCode: 'ABCD-EFGH-JKMN-PQRS-TVWX-YZ01' })
  await sent
  assert.equal(store.get().requests, 0)
  assert.equal(store.get().codesOnScreen, 1, 'the code in the answer counts as on screen before the code screen mounts')
  assert.equal(sheet(), true)
  // The code screen mounts and registers the same code: still one.
  const hide = store.codeShown('ABCD-EFGH-JKMN-PQRS-TVWX-YZ01')
  assert.equal(store.get().codesOnScreen, 1)
  events.fire()
  hostAnswers.pending[2].resolve({ signedIn: true, codePending: true })
  await flush()
  assert.equal(sheet(), true, 'any later refresh while the code shows')
  // Typed back: the host clears the flag and says so; the code screen goes.
  events.fire()
  hostAnswers.pending[3].resolve({ signedIn: true, codePending: false })
  await flush()
  hide()
  assert.equal(sheet(), false)
})

test('restart after quitting on the code screen: the gate opens on the pending code; a code Settings shows, or its request, keeps it shut', async () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  const pending = { signedIn: true, codePending: true }
  const open = () => accountSheetOpen({ wasOpen: false, whatsNewOpen: false, overview: pending, laterThisRun: false, codesOnScreen: store.get().codesOnScreen, requests: store.get().requests })
  assert.equal(open(), true)
  const done = store.requestStarted()
  assert.equal(open(), false, 'Settings asked for a new code: its request keeps the sheet shut before the code arrives')
  const hide = store.codeShown('Q4LM-7PXR-2KTB-9VHC-3WDN-8FGJ')
  done()
  done()
  assert.equal(store.get().requests, 0, 'a request that ends twice counts once')
  assert.equal(open(), false)
  hide()
  hide()
  assert.equal(store.get().codesOnScreen, 0, 'a screen that goes twice counts once')
})

test('a request that fails still ends its count, and an answer without a code shows none', async () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  await assert.rejects(accountRequest(store, () => Promise.reject(new Error('host down')), () => null))
  assert.equal(store.get().requests, 0)
  await accountRequest(store, () => Promise.resolve({ recoveryCode: null }), result => result.recoveryCode)
  assert.equal(store.get().codesOnScreen, 0)
})
test('a failed first read is tried again until it answers, so the gate does not stay shut on a host hiccup', async () => {
  const hostAnswers = scriptedHost()
  const waits: number[] = []
  const runs: (() => void)[] = []
  const store = accountStore(hostAnswers.load, () => undefined, (run, ms) => { waits.push(ms); runs.push(run) })
  const first = store.refresh()
  hostAnswers.pending[0].reject(new Error('host not ready'))
  await first
  assert.deepEqual(waits, [FIRST_READ_RETRY_MS[0]])
  runs[0]()
  hostAnswers.pending[1].reject(new Error('still not ready'))
  await flush()
  assert.deepEqual(waits, [FIRST_READ_RETRY_MS[0], FIRST_READ_RETRY_MS[1]])
  runs[1]()
  hostAnswers.pending[2].resolve({ signedIn: false })
  await flush()
  assert.equal(gateOpen(store.get()), true)
  // Once read, a failure is not retried on its own (the next event or action reads again).
  const later = store.refresh()
  hostAnswers.pending[3].reject(new Error('busy'))
  await later
  assert.equal(waits.length, 2)
})

test('Continue without an account ends when the service is back', () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  store.continueOffline()
  store.serviceBack()
  assert.equal(store.get().laterThisRun, false)
})

test('accountRequest lets go of its own hold on an answer code once a code screen holds it; the screen leaving ends it', async () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  const ran: Array<() => void> = []
  const schedule = (run: () => void) => { ran.push(run); return () => undefined }
  await accountRequest(store, () => Promise.resolve({ recoveryCode: 'CODE-ONE' }), result => result.recoveryCode, schedule)
  assert.equal(store.get().codesOnScreen, 1, 'the answer code is on screen before the screen mounts')
  assert.equal(store.codeHolders('CODE-ONE'), 1)
  const screen = store.codeShown('CODE-ONE')
  assert.equal(store.codeHolders('CODE-ONE'), 1, 'the request let go the moment the screen held the code: one holder, the screen')
  assert.equal(store.get().codesOnScreen, 1)
  screen()
  assert.equal(store.get().codesOnScreen, 0, 'the screen leaving is what ends the code: the request is not still holding it')
  ran[0]()
  assert.equal(store.get().codesOnScreen, 0, 'its timer firing later changes nothing')
})

test('accountRequest lets go of a code no screen took over, after the hand-over time', async () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  let fire = () => {}
  let waited = 0
  const schedule = (run: () => void, ms: number) => { fire = run; waited = ms; return () => undefined }
  await accountRequest(store, () => Promise.resolve({ recoveryCode: 'CODE-TWO' }), result => result.recoveryCode, schedule)
  assert.equal(waited, HANDOVER_MS)
  assert.equal(store.get().codesOnScreen, 1)
  fire()
  assert.equal(store.get().codesOnScreen, 0, 'a code no screen shows cannot hold the sheet up for good')
  fire()
  assert.equal(store.get().codesOnScreen, 0, 'letting go twice is one')
})

test('two holders of the same code count once and the code stays until both let go', () => {
  const store = accountStore(scriptedHost().load, () => undefined)
  const first = store.codeShown('CODE-THREE')
  const second = store.codeShown('CODE-THREE')
  assert.equal(store.get().codesOnScreen, 1)
  first()
  first()
  assert.equal(store.get().codesOnScreen, 1, 'one holder left, and a double release is one')
  second()
  assert.equal(store.get().codesOnScreen, 0)
})
