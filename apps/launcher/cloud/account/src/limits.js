/** The account service's limits (README.md › Limits): times in seconds unless a name says Ms, and counts. */
const DAY = 24 * 60 * 60

export const limits = {
  // Argon2id for a password (OWASP's minimum): 19 MiB, 2 passes, 1 lane, a 32-byte hash. A stored hash names its own.
  argon2: { memorySize: 19456, iterations: 2, parallelism: 1, hashLength: 32 },
  // Argon2 hashes per isolate at once (a slot is taken only around a hash). A request that finds them taken waits
  // slotWaitMs for one, then is answered 503 busy with Retry-After: 1. A hash is 19 MiB while it runs.
  hashesInFlight: 4,
  slotWaitMs: 2000,
  // A request body must arrive in full within bodyReadMs, or it is answered 408 slow-body.
  bodyReadMs: 10_000,
  // ... and the whole body within bodyMaxMs, however steadily its chunks arrive.
  bodyMaxMs: 60_000,
  passwordMin: 12,
  passwordMax: 1024,
  // Per address and client address: lockFailures wrong tries in lockWindowSeconds lock that pair for lockSeconds.
  lockFailures: 10,
  lockWindowSeconds: 15 * 60,
  lockSeconds: 15 * 60,
  // A try's place on its pair is given back when the try is answered. One still held after this is from a try that never
  // answered (a D1 error): it is cleared, and counts for nothing.
  pendingSeconds: 5 * 60,
  // Per address, across all client addresses and devices: wrong tries in addressWindowSeconds past addressFailures each
  // wait a second more, at most addressMaxDelaySeconds. Nothing is refused for it: only the pair locks. A known device
  // (a valid device token) doesn't wait.
  addressFailures: 100,
  addressWindowSeconds: 60 * 60,
  addressMaxDelaySeconds: 10,
  // A session's last_used moves at most this often; one unused this long ends.
  touchSeconds: DAY,
  idleSeconds: 400 * DAY,
  bodyBytes: 16 * 1024,
  // Sync: items per PUT, the largest value, the largest PUT body, and items per page of a pull or an export.
  syncBatch: 100,
  syncItemBytes: 64 * 1024,
  syncBodyBytes: 8 * 1024 * 1024,
  syncPage: 500,
  // A page of a pull or an export also stops at this many bytes of sealed values (the first item always goes).
  syncPageBytes: 4 * 1024 * 1024,
  // Per account: the items it may hold, the bytes of their sealed values, and the sessions (one per PC). A PUT that would
  // pass the item or byte cap is refused as account-full; beyond the session cap the least recently used session ends.
  accountItems: 20_000,
  accountBytes: 64 * 1024 * 1024,
  accountSessions: 50,
}
