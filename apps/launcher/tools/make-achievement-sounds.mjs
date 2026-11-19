// Exo's achievement sounds, made here from nothing but arithmetic: no samples, no packages. Same input, same bytes.
//
//   node tools/make-achievement-sounds.mjs [folder]
//
// A heavy, dampened thock: a low body whose pitch falls in the first moments, a knock above it so it still reads on small
// speakers and at low volume, no chime on top. Rarer is deeper, longer and hits again, never higher or more musical.
// Writes the set named by SHIPPED to ExoLauncher/Assets/Overlay/sounds/ (<tier>.wav; AchievementBanner.cs and the OBS
// source play them), and, with a folder, every set there as <set>-<tier>.wav. To change the sound: change SHIPPED, run
// this, rebuild. Prints each file's measurements and exits 1 if one breaks a limit in LIMITS.
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const SHIPPED = 'felt'

const RATE = 48000
const LIMITS = { peakDb: -1, below250: 80, knock: 8, presenceDb: -30, airDb: -45, dc: 1e-4, attackMs: 1 }

// The tiers: a build, a hit and a hold. A hit: { t: start s, f: body Hz, level, decay: body s }; a swell comes up over `rise`
// s and falls over `decay`. banner.html's HIT and --hit are timed to the last hit here.
const TIERS = {
  common: { seconds: 0.7, rt60: 0.3, lufs: -17, hits: [{ t: 0, f: 82, level: 1, decay: 0.09 }] },
  rare: { seconds: 0.95, rt60: 0.36, lufs: -16.5, hits: [{ t: 0, f: 70, level: 1, decay: 0.14 }], swell: [{ t: 0.01, f: 70, level: 0.16, rise: 0.03, decay: 0.16 }] },
  epic: { seconds: 1.3, rt60: 0.44, lufs: -16, hits: [{ t: 0, f: 60, level: 1, decay: 0.2 }], swell: [{ t: 0.01, f: 60, level: 0.3, rise: 0.04, decay: 0.28 }] },
  legendary: {
    seconds: 2.1, rt60: 0.55, lufs: -15.5,
    hits: [{ t: 0.62, f: 50, level: 1, decay: 0.28 }],
    swell: [{ t: 0, f: 50, level: 0.22, rise: 0.62, decay: 0.08 }, { t: 0.63, f: 50, level: 0.42, rise: 0.05, decay: 0.42 }],
  },
  completion: {
    seconds: 3, rt60: 0.65, lufs: -15,
    hits: [{ t: 0, f: 72, level: 0.5, decay: 0.08 }, { t: 0.42, f: 62, level: 0.66, decay: 0.11 }, { t: 0.9, f: 46, level: 1, decay: 0.34 }],
    swell: [{ t: 0.46, f: 46, level: 0.26, rise: 0.44, decay: 0.08 }, { t: 0.91, f: 46, level: 0.46, rise: 0.06, decay: 0.62 }],
  },
}

// The voicings. knock: the 120 to 250 Hz wood; tok: the short touch near 400 Hz; felt: its noise; drive: how hard the body
// saturates, which puts its weight where small speakers can show it; top: where everything above is closed.
const SETS = {
  felt: { knock: 1.15, tok: 0.3, felt: 0.05, drive: 2.1, wet: 0.2, top: 1500 },
  wood: { knock: 1.6, tok: 0.45, felt: 0.06, drive: 1.7, wet: 0.22, top: 1900 },
  deep: { knock: 1.3, tok: 0.24, felt: 0.03, drive: 2.4, wet: 0.16, top: 1100 },
  stone: { knock: 1.3, tok: 0.4, felt: 0.09, drive: 3, wet: 0.1, top: 2200 },
}

function random(seed) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296 * 2 - 1
  }
}

const rounded = (k, length) => k < length ? 0.5 - 0.5 * Math.cos(Math.PI * k / length) : 1

/** One hit into a mono buffer: the falling body, saturated; the knock and touch above it; a breath of low noise. */
function hit(out, { t, f, level, decay }, voice, seed) {
  const start = Math.round(t * RATE), lift = Math.round(0.0025 * RATE)
  const noise = random(seed)
  let body = 0, low = 0, lower = 0
  const modes = [[160, voice.knock, 0.055], [235, voice.knock * 0.7, 0.04], [410, voice.tok, 0.016]].map(([hz, a, d]) => ({ step: 2 * Math.PI * hz / RATE, a, d, phase: 0 }))
  const norm = Math.tanh(voice.drive)
  for (let i = start, k = 0; i < out.length; i++, k++) {
    const s = k / RATE
    const env = Math.exp(-s / decay)
    if (env < 1e-4 && s > 0.2) break
    body += 2 * Math.PI * f * (1 + 1.5 * Math.exp(-s / 0.02)) / RATE
    let sample = Math.tanh(voice.drive * Math.sin(body) * env) / norm
    for (const m of modes) { sample += Math.sin(m.phase) * m.a * Math.exp(-s / m.d); m.phase += m.step }
    low += (noise() - low) * 0.06; lower += (low - lower) * 0.06
    sample += lower * 6 * voice.felt * Math.exp(-s / 0.008)
    out[i] += sample * level * rounded(k, lift)
  }
}

/** The weight that stays after a heavy hit: a low sine that comes up and goes, felt more than heard. */
function swell(out, { t, f, level, rise, decay }) {
  const start = Math.round(t * RATE), step = 2 * Math.PI * f / RATE
  for (let i = start, k = 0; i < out.length; i++, k++) {
    const s = k / RATE
    out[i] += Math.sin(step * k) * level * rounded(k, rise * RATE) * Math.exp(-Math.max(0, s - rise) / decay)
  }
}

function fft(re, im, inverse) {
  const n = re.length
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1
    for (; j & bit; bit >>= 1) j ^= bit
    j ^= bit
    if (i < j) { [re[i], re[j]] = [re[j], re[i]]; [im[i], im[j]] = [im[j], im[i]] }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const angle = (inverse ? 2 : -2) * Math.PI / len, half = len >> 1
    const wr = Math.cos(angle), wi = Math.sin(angle)
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0
      for (let j = 0; j < half; j++) {
        const a = i + j, b = a + half
        const tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr
        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti
        const next = cr * wr - ci * wi
        ci = cr * wi + ci * wr; cr = next
      }
    }
  }
  if (inverse) for (let i = 0; i < n; i++) { re[i] /= n; im[i] /= n }
}

/** A small, dark, damped room: noise that decays and closes down fast. One per ear: it is all the stereo there is. */
function room(seconds, rt60, seed) {
  const length = Math.round(seconds * RATE)
  return [0, 1].map((ear) => {
    const noise = random(seed + ear * 101), ir = new Float64Array(length)
    const predelay = Math.round(0.006 * RATE)
    let low = 0
    for (let i = predelay; i < length; i++) {
      const s = (i - predelay) / RATE
      low += (noise() - low) * (1 - Math.exp(-2 * Math.PI * (300 + 900 * Math.exp(-s / 0.05)) / RATE))
      ir[i] = low * Math.min(1, s / 0.004) * 10 ** (-3 * s / rt60)
    }
    const energy = Math.sqrt(ir.reduce((sum, v) => sum + v * v, 0))
    return ir.map(v => v / energy)
  })
}

function convolve(signal, ir, length) {
  let n = 1
  while (n < signal.length + ir.length) n <<= 1
  const ar = new Float64Array(n), ai = new Float64Array(n), br = new Float64Array(n), bi = new Float64Array(n)
  ar.set(signal); br.set(ir)
  fft(ar, ai, false); fft(br, bi, false)
  for (let i = 0; i < n; i++) { const r = ar[i] * br[i] - ai[i] * bi[i]; ai[i] = ar[i] * bi[i] + ai[i] * br[i]; ar[i] = r }
  fft(ar, ai, true)
  return ar.subarray(0, length)
}

/** RBJ biquad over a channel, in place. */
function biquad(x, b0, b1, b2, a0, a1, a2) {
  let x1 = 0, x2 = 0, y1 = 0, y2 = 0
  for (let i = 0; i < x.length; i++) {
    const y = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0
    x2 = x1; x1 = x[i]; y2 = y1; y1 = y; x[i] = y
  }
}
const highpass = (x, f, q) => { const w = 2 * Math.PI * f / RATE, c = Math.cos(w), al = Math.sin(w) / (2 * q); biquad(x, (1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al) }
const lowpass = (x, f, q) => { const w = 2 * Math.PI * f / RATE, c = Math.cos(w), al = Math.sin(w) / (2 * q); biquad(x, (1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al) }

function render(set, tier, seed) {
  const voice = SETS[set], { seconds, rt60, hits, swell: rise, lufs: target } = TIERS[tier]
  const length = Math.round(seconds * RATE)
  const mono = new Float64Array(length)
  hits.forEach((h, i) => hit(mono, { ...h, t: h.t + 0.001 }, voice, seed + i * 7))
  for (const s of rise ?? []) swell(mono, s)
  const irs = room(Math.min(seconds, rt60 * 1.5), rt60, seed + 1000)
  const out = irs.map((ir) => {
    const wet = convolve(mono, ir, length), mix = new Float64Array(length)
    highpass(wet, 150, 0.7) // The low end stays dry and in the middle; the room is only around it.
    for (let i = 0; i < length; i++) mix[i] = mono[i] + wet[i] * voice.wet
    highpass(mix, 32, 0.7)
    lowpass(mix, voice.top, 0.6)
    const mean = mix.reduce((s, v) => s + v, 0) / length, fade = Math.round(Math.min(0.2, seconds * 0.3) * RATE)
    for (let i = 0; i < length; i++) {
      mix[i] -= mean
      const left = length - 1 - i
      if (left < fade) mix[i] *= rounded(left, fade)
    }
    mix[0] = 0
    return mix
  })
  // To its loudness, but never past the peak limit: that one wins, and the table says where it landed.
  const peak = out.reduce((top, ch) => ch.reduce((most, v) => Math.max(most, Math.abs(v)), top), 0)
  const gain = Math.min(10 ** ((target - lufs(out)) / 20), 10 ** ((LIMITS.peakDb - 0.1) / 20) / peak)
  return out.map(ch => ch.map(v => v * gain))
}

/** Integrated loudness, BS.1770-4; a sound shorter than its 400 ms block is measured with silence after it. */
function lufs(channels) {
  const weighted = channels.map(ch => {
    const x = Float64Array.from(ch)
    biquad(x, 1.53512485958697, -2.69169618940638, 1.19839281085285, 1, -1.69065929318241, 0.73248077421585)
    biquad(x, 1, -2, 1, 1, -1.99004745483398, 0.99007225036621)
    return x
  })
  const size = Math.round(0.4 * RATE), step = Math.round(0.1 * RATE), blocks = []
  for (let at = 0; at + size <= weighted[0].length; at += step) {
    let sum = 0
    for (const x of weighted) for (let i = at; i < at + size; i++) sum += x[i] * x[i]
    blocks.push(sum / size)
  }
  const loud = (list) => -0.691 + 10 * Math.log10(list.reduce((s, v) => s + v, 0) / list.length)
  const audible = blocks.filter(p => -0.691 + 10 * Math.log10(p) > -70)
  const floor = loud(audible) - 10
  return loud(audible.filter(p => -0.691 + 10 * Math.log10(p) > floor))
}

/** How the energy divides between the bands, as a share of the whole. No window: each file starts and ends in silence, and a window would hide a hit at its very start. */
function spectrum(channels) {
  const length = channels[0].length
  let n = 1
  while (n < length) n <<= 1
  const edges = { '<60': [0, 60], '60-120': [60, 120], '120-250': [120, 250], '250-500': [250, 500], '500-2k': [500, 2000], '2-5k': [2000, 5000], '>5k': [5000, 24000] }
  const power = Object.fromEntries(Object.keys(edges).map(k => [k, 0]))
  let total = 0
  for (const ch of channels) {
    const re = new Float64Array(n), im = new Float64Array(n)
    re.set(ch)
    fft(re, im, false)
    for (let i = 1; i < n / 2; i++) {
      const f = i * RATE / n, p = re[i] * re[i] + im[i] * im[i]
      total += p
      for (const [k, [a, b]] of Object.entries(edges)) if (f >= a && f < b) power[k] += p
    }
  }
  return Object.fromEntries(Object.entries(power).map(([k, p]) => [k, p / total]))
}

function measure(channels, hitAt) {
  const length = channels[0].length
  let peak = 0, sum = 0, cross = 0, ll = 0, rr = 0, dc = 0
  for (const ch of channels) {
    let mean = 0
    for (const v of ch) { peak = Math.max(peak, Math.abs(v)); sum += v * v; mean += v }
    dc = Math.max(dc, Math.abs(mean / length))
  }
  for (let i = 0; i < length; i++) { cross += channels[0][i] * channels[1][i]; ll += channels[0][i] ** 2; rr += channels[1][i] ** 2 }
  // Attack of the last, heaviest hit: 10% to 90% of its rise above what came before, on a 1 ms envelope.
  const env = [], from = Math.round(hitAt * RATE)
  for (let at = from; at + 48 < Math.min(length, from + RATE / 8); at += 6) {
    let e = 0
    for (let i = at; i < at + 48; i++) e += channels[0][i] ** 2 + channels[1][i] ** 2
    env.push(Math.sqrt(e / 48))
  }
  const top = Math.max(...env.slice(0, 60)), floor = env[0]
  const reach = (share) => env.findIndex(v => v >= floor + (top - floor) * share)
  const parts = spectrum(channels)
  const pct = (v) => +(v * 100).toFixed(1)
  return {
    s: +(length / RATE).toFixed(2),
    peakDb: +(20 * Math.log10(peak)).toFixed(1),
    rmsDb: +(10 * Math.log10(sum / (2 * length))).toFixed(1),
    lufs: +lufs(channels).toFixed(1),
    attackMs: +((reach(0.9) - reach(0.1)) * 6 / 48).toFixed(1),
    dc: +dc.toExponential(1),
    corr: +(cross / Math.sqrt(ll * rr)).toFixed(2),
    ...Object.fromEntries(Object.entries(parts).slice(0, 5).map(([k, v]) => [k + '%', pct(v)])),
    below250: pct(parts['<60'] + parts['60-120'] + parts['120-250']),
    presenceDb: +(10 * Math.log10(Math.max(parts['2-5k'], 1e-12))).toFixed(1),
    airDb: +(10 * Math.log10(Math.max(parts['>5k'], 1e-12))).toFixed(1),
  }
}

function broken(m) {
  const out = []
  if (m.peakDb > LIMITS.peakDb) out.push(`peak ${m.peakDb}`)
  if (m.below250 < LIMITS.below250) out.push(`${m.below250}% below 250 Hz`)
  if (m['120-250%'] + m['250-500%'] < LIMITS.knock) out.push(`only ${m['120-250%'] + m['250-500%']}% at 120-500 Hz: gone on small speakers`)
  if (m.presenceDb > LIMITS.presenceDb) out.push(`2-5 kHz ${m.presenceDb}`)
  if (m.airDb > LIMITS.airDb) out.push(`above 5 kHz ${m.airDb}`)
  if (m.dc > LIMITS.dc) out.push(`DC ${m.dc}`)
  if (m.attackMs < LIMITS.attackMs) out.push(`attack ${m.attackMs} ms`)
  return out
}

function wav(channels) {
  const frames = channels[0].length, bytes = Buffer.alloc(44 + frames * 4)
  bytes.write('RIFF', 0); bytes.writeUInt32LE(36 + frames * 4, 4); bytes.write('WAVEfmt ', 8); bytes.writeUInt32LE(16, 16)
  bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(2, 22); bytes.writeUInt32LE(RATE, 24); bytes.writeUInt32LE(RATE * 4, 28)
  bytes.writeUInt16LE(4, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 4, 40)
  for (let i = 0; i < frames; i++) for (const c of [0, 1]) bytes.writeInt16LE(Math.round(Math.max(-1, Math.min(1, channels[c][i])) * 32767), 44 + i * 4 + c * 2)
  return bytes
}

const candidates = process.argv[2]
const shipped = join(dirname(fileURLToPath(import.meta.url)), '..', 'ExoLauncher', 'Assets', 'Overlay', 'sounds')
mkdirSync(shipped, { recursive: true })
if (candidates) mkdirSync(candidates, { recursive: true })
let failed = false
const table = []
for (const set of Object.keys(SETS)) {
  if (!candidates && set !== SHIPPED) continue
  for (const tier of Object.keys(TIERS)) {
    const channels = render(set, tier, 4242)
    const m = measure(channels, TIERS[tier].hits.at(-1).t), bad = broken(m)
    table.push({ file: `${set}-${tier}`, ...m, broken: bad.join('; ') })
    if (bad.length) failed = true
    const bytes = wav(channels)
    if (candidates) writeFileSync(join(candidates, `${set}-${tier}.wav`), bytes)
    if (set === SHIPPED) writeFileSync(join(shipped, `${tier}.wav`), bytes)
  }
}
console.table(table)
process.exit(failed ? 1 : 0)
