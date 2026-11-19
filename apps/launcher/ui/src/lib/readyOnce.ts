/**
 * One message the host must hear once, and does hear: sent at the first call, sent again after a failure (up to `tries`
 * times, `retryMs` apart), and never again once it went through. Calls while one is under way add nothing.
 */
export function readyOnce(send: () => Promise<unknown>, tries = 5, retryMs = 1000, wait = (ms: number, then: () => void) => { setTimeout(then, ms) }) {
  let state: 'idle' | 'sending' | 'sent' = 'idle'
  let left = tries
  const attempt = () => {
    if (state !== 'idle' || left <= 0) return
    state = 'sending'
    left -= 1
    send().then(
      () => { state = 'sent' },
      () => { state = 'idle'; if (left > 0) wait(retryMs, attempt) },
    )
  }
  return attempt
}
