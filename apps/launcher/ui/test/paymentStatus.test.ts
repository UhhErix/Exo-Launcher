import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'

// Processing must not unlock another purchase; confirmation must use its returned status;
// missing replies and polling errors must not invent success or stop checking.
const script = readFileSync(new URL('../public/pay/pay.js', import.meta.url), 'utf8')
const intent = (status: string) => ({ paymentIntent: { status } })

async function checkout(replies: unknown[], confirmed: unknown = intent('succeeded')) {
  const messages: { type: string; message?: string }[] = []
  const timers: (() => Promise<void>)[] = []
  let listen: (event: object) => void = () => {}
  let confirmations = 0
  const stripe = {
    retrievePaymentIntent: async () => {
      const reply = replies.shift()
      if (reply instanceof Error) throw reply
      return reply
    },
    confirmCardPayment: async () => { confirmations++; return confirmed },
  }
  const parent = { postMessage: (message: { type: string }) => messages.push(message) }
  runInNewContext(script, {
    window: {
      parent, Stripe: () => stripe,
      addEventListener: (_: string, callback: typeof listen) => { listen = callback },
      setTimeout: (callback: () => Promise<void>, delay: number) => { assert.equal(delay, 3000); timers.push(callback) },
    },
    document: { body: { classList: { toggle() {} } } },
  })
  listen({ origin: 'https://app.exo-launcher.local', source: parent, data: { type: 'confirmPayment', key: 'key', clientSecret: 'secret' } })
  await new Promise(resolve => setImmediate(resolve))
  return { messages, timers, confirmations, outcomes: () => messages.filter(m => ['paid', 'pending', 'error'].includes(m.type)).map(m => m.type) }
}

for (const status of ['processing', 'pending', 'requires_action', 'requires_confirmation', 'requires_capture']) {
  test(`${status} stays pending until Stripe reports success`, async () => {
    const run = await checkout([intent(status), intent('succeeded')], intent(status))
    assert.deepEqual(run.outcomes(), ['pending'])
    assert.equal(run.confirmations, ['requires_action', 'requires_confirmation'].includes(status) ? 1 : 0)
    await run.timers.shift()!()
    assert.deepEqual(run.outcomes(), ['pending', 'paid'])
    assert.equal(run.timers.length, 0)
  })
}

for (const status of ['succeeded', 'canceled', 'requires_payment_method']) {
  test(`${status} is terminal without reconfirming`, async () => {
    const run = await checkout([intent(status)])
    assert.deepEqual(run.outcomes(), [status === 'succeeded' ? 'paid' : 'error'])
    assert.equal(run.confirmations, 0)
    assert.equal(run.timers.length, 0)
  })
}

test('confirmation errors remain errors', async () => {
  const run = await checkout([intent('requires_action')], { error: { message: 'Declined' } })
  assert.deepEqual(run.outcomes(), ['error'])
  assert.equal(run.messages.at(-1)?.message, 'Declined')
  assert.equal(run.timers.length, 0)
})

test('empty and unknown statuses stay pending through polling errors', async () => {
  const run = await checkout([{}, intent('unknown'), new Error('offline'), { error: { message: 'offline' } }, intent('canceled')])
  for (let i = 0; i < 4; i++) {
    assert.equal(run.outcomes().at(-1), 'pending')
    await run.timers.shift()!()
  }
  assert.deepEqual(run.outcomes(), ['pending', 'pending', 'pending', 'pending', 'error'])
  assert.equal(run.timers.length, 0)
})
