/**
 * Exo's driver advisories feed (README.md › Driver advisories): signs the table Exo Launcher builds in
 * (ExoLauncher/Services/DriverAdvisories.json) into the file the update address serves at /v1/advisories.json.
 *
 *   node advisories.mjs keygen [key file]   a new P-256 key: the private JWK to the key file (outside any repo), the
 *                                            public key (SEC1, base64url) to stdout, for DriverAdvisories.PublicKey
 *   node advisories.mjs sign [key file]     out/advisories.json: { payload: base64 of the table's bytes, signature }
 *
 * The key file defaults to %USERPROFILE%\.exo-keys\advisories.jwk (EXO_ADVISORIES_KEY overrides it). The signature is
 * ECDSA P-256 over SHA-256 of the payload bytes, r || s with s at most n / 2: the form Exo's P256.Verify takes.
 */
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
export const TABLE = join(here, '..', '..', 'ExoLauncher', 'Services', 'DriverAdvisories.json')
const OUT = join(here, 'out', 'advisories.json')
const ORDER = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n

/** A P1363 signature with s at most n / 2: s becomes n - s when it's higher (WebCrypto picks either). */
export function lowS(signature) {
  const bytes = new Uint8Array(signature)
  const s = BigInt('0x' + Buffer.from(bytes.slice(32)).toString('hex'))
  if (s <= ORDER >> 1n) return bytes
  const low = Buffer.from((ORDER - s).toString(16).padStart(64, '0'), 'hex')
  return new Uint8Array([...bytes.slice(0, 32), ...low])
}

/** The served file for a table's bytes: the bytes as they are, and their signature. */
export async function sign(payload, privateJwk) {
  const key = await crypto.subtle.importKey('jwk', privateJwk, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign'])
  const signature = lowS(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, payload))
  return JSON.stringify({ payload: Buffer.from(payload).toString('base64'), signature: Buffer.from(signature).toString('base64') })
}

const keyFile = (arg) => arg ?? process.env.EXO_ADVISORIES_KEY ?? join(homedir(), '.exo-keys', 'advisories.jwk')

async function main([command, arg]) {
  if (command === 'keygen') {
    const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify'])
    const jwk = await crypto.subtle.exportKey('jwk', pair.privateKey)
    const file = keyFile(arg)
    await mkdir(dirname(file), { recursive: true })
    await writeFile(file, JSON.stringify({ kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y, d: jwk.d }), { flag: 'wx' })
    console.log(Buffer.from(await crypto.subtle.exportKey('raw', pair.publicKey)).toString('base64url'))
    return
  }
  if (command === 'sign') {
    const payload = await readFile(TABLE)
    const serial = JSON.parse(payload.toString('utf8')).serial
    if (!Number.isSafeInteger(serial)) throw new Error('The table has no serial.')
    await mkdir(dirname(OUT), { recursive: true })
    await writeFile(OUT, await sign(payload, JSON.parse(await readFile(keyFile(arg), 'utf8'))))
    console.log(`${OUT}: serial ${serial}`)
    return
  }
  console.log('node advisories.mjs keygen|sign [key file]')
  process.exitCode = 1
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) await main(process.argv.slice(2))
