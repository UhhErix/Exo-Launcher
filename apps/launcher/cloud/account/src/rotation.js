/**
 * Retiring a key from ACCOUNT_SECRETS (README.md › Keys). A key may leave the keyring only when no account row names it:
 * its address digest (email_mac), its recovery code digest (recovery_mac) or its item key. Sign-in moves the first two
 * and the item key to the current key; a recovery code moves when it is made again, so one that is never made again
 * keeps its key. That is what the check refuses.
 */

const hexBytes = text => Uint8Array.from(text.match(/../g) ?? [], pair => parseInt(pair, 16))

/** The key ids one account row names: [column, key id] for its address digest, recovery code and item key. */
export function keysNamedBy(row) {
  const bytes = typeof row.item_key === 'string' ? hexBytes(row.item_key) : Uint8Array.from(row.item_key)
  const itemKey = new TextDecoder().decode(bytes.subarray(1, 1 + bytes[0]))
  return [
    ['email_mac', row.email_mac.split(':')[0]],
    ['recovery_mac', row.recovery_mac.split(':')[0]],
    ['item_key', itemKey],
  ]
}

/** The [column, key id] pairs, over all rows, that name a key being retired. An empty list means the keys may go. */
export function namedRetired(rows, retired) {
  const gone = new Set(retired)
  return rows.flatMap(row => keysNamedBy(row).filter(([, key]) => gone.has(key)))
}
