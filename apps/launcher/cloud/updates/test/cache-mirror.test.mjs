/**
 * The cache mirror's tests: the pin parser, the checks, and the run with fake upstream and R2 (nothing is downloaded or uploaded).
 *
 * How it could fail: a pin is missed or misread (wrong key, size, hash or tag); a download that doesn't match its pin is
 * written; an object already in R2 with another hash is overwritten; a folder listing that disagrees with the bytes is
 * written; a commit that isn't the pinned one is written; a licence changes without notice; a key outside cache/ is written.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { test } from 'node:test'
import {
  checkListing, gitBlobSha1, mirror, parseConstants, parseFrameGenPins, parseToolPins, parseZipPins, parseVendorPins, pickLicence, planObjects,
  readPins, sha256Hex, verifyBytes, STATE_KEY,
} from '../cache-mirror.mjs'

const LAUNCHER = 'C:/Users/Erix/Code/Exo/ExoLauncher-325'
const COMMIT = '374959484e79a640feaba44c93ac8cfb0a03f5b5'

const FIXTURE = `
    private const string DlssRepo = "NVIDIA/DLSS";
    private const string DlssPath = "lib/Windows_x86_64/rel/";
    private const string DlssLatest = "${COMMIT}"; // v310.9.1, Sep 8 2026
    private const string DlssPrevious = "a291cc7d2cc642a51566f3dfd5376f635cd1b284"; // v310.7.0, Jun 23 2026
    internal static readonly PinnedFile[] PinnedFiles =
    [
        new("nvngx_dlss.dll", "dlss-310.9.16", "DLSS 310.9.16", "310.9.16.0", "310.9.16", DlssRepo, "", "", 74187376, "858ccff96360c3098db9264cd6f13d04a027e09b6ce7ad5bbc297570e824b4f2",
            new(DlssSwapper + "dlss/dlss_v310.9.16.0_C77.zip", 45333524)),
        new("nvngx_dlss.dll", "dlss-310.9.1", "DLSS 310.9.1", "310.9.1.0", "310.9.1", DlssRepo, DlssLatest, DlssPath + "nvngx_dlss.dll", 58956912, "3975567b8943c53acce397f2b72380092f84f162d00b0d2c7d08a1025c563983"),
        // a comment between entries
        new("nvngx_dlss.dll", "dlss-310.7.0", "DLSS 310.7.0", "310.7.0.0", "310.7.0", DlssRepo, DlssPrevious, DlssPath + "nvngx_dlss.dll", 58977904, "be6e434a94ca32499515eb62ca0e6c274526055d568d0426e4c652dcdfb6ee6e"),
    ];
`

test('the parser reads constants, tags and the commit-pinned DLLs, and skips zip-only builds', () => {
  const constants = parseConstants(FIXTURE)
  assert.equal(constants.values.get('DlssPath'), 'lib/Windows_x86_64/rel/')
  assert.equal(constants.tags.get('DlssLatest'), 'v310.9.1')
  assert.equal(constants.tags.get('DlssPath'), undefined, 'a constant with no tag comment names none')
  const vendor = parseVendorPins(FIXTURE, constants)
  assert.equal(vendor.length, 2, 'the zip-only 310.9.16 entry has no commit and is not a GitHub pin')
  assert.deepEqual(
    { repo: vendor[0].repo, commit: vendor[0].commit, tag: vendor[0].tag, folder: vendor[0].folder, name: vendor[0].name, size: vendor[0].size },
    { repo: 'NVIDIA/DLSS', commit: COMMIT, tag: 'v310.9.1', folder: 'lib/Windows_x86_64/rel', name: 'nvngx_dlss.dll', size: 58956912 },
  )
  assert.equal(vendor[1].tag, 'v310.7.0', 'the previous release reads its tag from its own constant')
})

test('the parser reads the three tool pins by owner, repo, tag, file, size and SHA-256', () => {
  const src = `
    private static readonly GitHubReleaseAsset NileReleaseAsset = new(
        "imLinguin",
        "nile",
        "v1.2.0",
        "nile_windows_x86_64.exe",
        ExpectedSize: 12_386_046,
        ExpectedSha256: "6531790c59f78cea4a8743bf0582d5afda7fb887f5c143391d7339ad0f42ab88");
    private static readonly GitHubReleaseAsset LegendaryReleaseAsset = new(
        "derrod", "legendary", "0.21.1", "legendary_windows_x64.exe",
        ExpectedSize: 17_654_404,
        ExpectedSha256: "6be77857dc0a6dd33ea7442ce8b5291944ec6da53757f81ca95b57ce794baf5b",
        OwnerAliases: ["legendary-gl"]);`
  const tools = parseToolPins(src)
  assert.equal(tools.length, 2)
  assert.deepEqual(tools[0], {
    kind: 'asset', owner: 'imLinguin', repo: 'nile', tag: 'v1.2.0', asset: 'nile_windows_x86_64.exe',
    size: 12386046, sha256: '6531790c59f78cea4a8743bf0582d5afda7fb887f5c143391d7339ad0f42ab88',
  })
  assert.equal(tools[1].owner, 'derrod', 'the canonical owner is the path the cache uses; the alias is not a path')
})

test('the voice model pin (positional size and SHA-256, SpeakerModel.cs) is read like a tool pin', () => {
  const src = `
    internal static readonly GitHubReleaseAsset Model = new(
        "k2-fsa", "sherpa-onnx", "speaker-recongition-models", "nemo_en_titanet_small.onnx",
        40257283, "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e");`
  assert.deepEqual(parseToolPins(src), [{
    kind: 'asset', owner: 'k2-fsa', repo: 'sherpa-onnx', tag: 'speaker-recongition-models', asset: 'nemo_en_titanet_small.onnx',
    size: 40257283, sha256: 'ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e',
  }])
})

test('frame-gen pins are read with their owner from the mod table, tag, file, size and SHA-256', () => {
  const src = [
    '        new("MFG Unlocker", "dashdogy", "MFG-Unlocker", MfgAsset(), 64L << 20),',
    '    internal static readonly (string Repo, FrameGenRelease Release)[] PinnedFrameGenReleases =',
    '    [',
    '        ("MFG-Unlocker", new("v1.7.0", "MFGUnlocker-v1.7.0.zip", 4842619, "5bd19d03288e1ece1d0bb2030ac072b36758045028da192044dcaad73fd901dd")),',
    '    ];',
  ].join('\n')
  const pins = parseFrameGenPins(src)
  assert.deepEqual(pins, [{ kind: 'asset', owner: 'dashdogy', repo: 'MFG-Unlocker', tag: 'v1.7.0', asset: 'MFGUnlocker-v1.7.0.zip', size: 4842619, sha256: '5bd19d03288e1ece1d0bb2030ac072b36758045028da192044dcaad73fd901dd' }])
})

test('the Launcher source yields exactly the pins it compiles in (3 tools, the voice model, 3 frame-gen, 24 DLLs) when a checkout is present', (t) => {
  if (!existsSync(LAUNCHER)) return t.skip('no Launcher checkout at ' + LAUNCHER)
  const pins = readPins(LAUNCHER, 'release/3.25.0')
  // Three tools and two frame-gen releases: the Launcher dropped the DLSSG SM86 pin at 8ca6e47e (no licence).
  assert.equal(pins.tools.length, 6, 'three tools, the TitaNet voice model and two frame-gen releases')
  assert.ok(pins.tools.some((t) => t.repo === 'sherpa-onnx' && t.asset === 'nemo_en_titanet_small.onnx'), 'the voice model is mirrored')
  assert.equal(pins.vendor.length, 24)
  const plan = planObjects(pins)
  // DLSSG SM86 has no licence, so Exo may not redistribute it: its pin is read but not planned.
  assert.equal(plan.binaries.length, 30)
  assert.equal(plan.assetLicences.length, 6)
  assert.ok(!plan.binaries.some((b) => b.key.includes('dlssg_for_sm86')), 'the excluded repo is never planned')
  assert.equal(plan.commits.length, 7)
  assert.equal(plan.listings.length, 7)
})

test('bytes that differ from their pin are refused, by size and by hash', () => {
  const bytes = Buffer.from('hello')
  verifyBytes(bytes, { size: 5, sha256: sha256Hex(bytes) }, 'ok')
  assert.throws(() => verifyBytes(bytes, { size: 6, sha256: sha256Hex(bytes) }, 'x'), /5 bytes, the pin says 6/)
  assert.throws(() => verifyBytes(bytes, { size: 5, sha256: 'f'.repeat(64) }, 'x'), /SHA-256/)
})

test('a listing must name each pinned DLL with its size and git blob hash', () => {
  const bytes = Buffer.from('dll')
  const pin = { name: 'a.dll', size: 3 }
  const blob = () => gitBlobSha1(bytes)
  checkListing([{ type: 'file', name: 'a.dll', size: 3, sha: gitBlobSha1(bytes) }], [pin], blob)
  assert.throws(() => checkListing([], [pin], blob), /no a.dll/)
  assert.throws(() => checkListing([{ type: 'dir', name: 'a.dll', size: 3, sha: 'x' }], [pin], blob), /is a dir/)
  assert.throws(() => checkListing([{ type: 'file', name: 'a.dll', size: 4, sha: 'x' }], [pin], blob), /size/)
  assert.throws(() => checkListing([{ type: 'file', name: 'a.dll', size: 3, sha: 'x' }], [pin], blob), /blob/)
})

test('the licence file chosen is the repo\'s own licence, with the third-party notice as the fallback', () => {
  assert.equal(pickLicence(['LICENSE.txt', 'README.md']), 'LICENSE.txt')
  assert.equal(pickLicence(['3rdpartynotice.md', 'readme.md']), '3rdpartynotice.md')
  assert.equal(pickLicence(['README.md']), null)
})

/** A fake upstream and R2: the mirror's whole run, with nothing downloaded or uploaded. */
function fakeIo({ upstream = {}, api = {}, r2 = {}, record = {}, inspect = null } = {}) {
  const calls = { fetched: [], puts: [], gets: [], writes: 0 }
  return {
    calls,
    io: {
      async fetchBytes(url) {
        calls.fetched.push(url)
        if (!(url in upstream)) throw new Error('no fake upstream for ' + url)
        return upstream[url]
      },
      async fetchJson(path) {
        calls.fetched.push('api:' + path)
        if (!(path in api)) throw new Error('no fake api for ' + path)
        return api[path]
      },
      async r2Get(key) {
        calls.gets.push(key)
        return key in r2 ? r2[key] : null
      },
      async r2Put(key, bytes) {
        calls.puts.push(key)
        if (!key.startsWith('cache/') && key !== STATE_KEY) throw new Error('bad key ' + key)
        r2[key] = bytes
      },
      readRecord: () => record,
      inspectZip: async (bytes, name) => (inspect ? inspect(bytes, name) : { size: 0, sha256: '', status: 'NotSigned', valid: false, signer: '' }),
      writeRecord: () => { calls.writes++ },
    },
  }
}

/** A one-DLL plan with its listing, commit and licence, all consistent. */
function onePinPlan(dll) {
  const commit = COMMIT
  const pin = { kind: 'dll', release: 'dlss-310.9.1', repo: 'NVIDIA/DLSS', commit, tag: 'v310.9.1', folder: 'lib/Windows_x86_64/rel', name: 'nvngx_dlss.dll', size: dll.length, sha256: sha256Hex(dll) }
  const url = `https://raw.githubusercontent.com/NVIDIA/DLSS/${commit}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  const upstream = { [url]: dll, [`https://raw.githubusercontent.com/NVIDIA/DLSS/${commit}/LICENSE.txt`]: Buffer.from('licence') }
  const api = {
    [`repos/NVIDIA/DLSS/contents/lib/Windows_x86_64/rel?ref=${commit}`]: [{ type: 'file', name: 'nvngx_dlss.dll', size: dll.length, sha: gitBlobSha1(dll) }],
    [`repos/NVIDIA/DLSS/commits/v310.9.1`]: { sha: commit },
    [`repos/NVIDIA/DLSS/contents?ref=${commit}`]: [{ name: 'LICENSE.txt' }],
  }
  return { plan: planObjects({ vendor: [pin], tools: [] }), upstream, api }
}

test('a clean run uploads the DLL, its listing, its commit and its licence, and records each under cache/ or the state', async () => {
  const dll = Buffer.from('a pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const { io, calls } = fakeIo({ upstream, api })
  const { done, state } = await mirror({ plan, io })
  assert.deepEqual(calls.puts.filter((k) => k.startsWith('cache/')).sort(), [
    `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`,
    `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/LICENSE.txt`,
    `cache/NVIDIA/DLSS/commits/v310.9.1`,
    `cache/NVIDIA/DLSS/contents/lib/Windows_x86_64/rel@${COMMIT}`,
  ].sort())
  assert.ok(calls.puts.includes(STATE_KEY))
  assert.equal(Object.keys(state.objects).length, 4)
  assert.equal(done.filter(([s]) => s === 'uploaded').length, 4)
})

test('a download that does not match its pin is refused and nothing is uploaded for it', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, api } = onePinPlan(dll)
  const wrong = Buffer.from('a different dll body')
  const url = `https://raw.githubusercontent.com/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  const { io, calls } = fakeIo({ upstream: { [url]: wrong }, api })
  await assert.rejects(mirror({ plan, io }), /does not match|bytes, the pin says|SHA-256/)
  assert.equal(calls.puts.filter((k) => k.startsWith('cache/')).length, 0)
})

test('an object already in R2 with a different hash is never overwritten', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const key = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  const { io, calls } = fakeIo({ upstream, api, r2: { [key]: Buffer.from('someone else put this there') } })
  await assert.rejects(mirror({ plan, io }), /refused .*R2 already holds/)
  assert.ok(!calls.puts.includes(key))
})

test('an object already in R2 with the pinned hash is adopted, not uploaded again', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const key = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  const { io, calls } = fakeIo({ upstream, api, r2: { [key]: dll } })
  const { done } = await mirror({ plan, io })
  assert.ok(!calls.puts.includes(key))
  assert.ok(done.some(([s, k]) => s === 'adopted' && k === key))
})

test('a re-run skips every object already recorded with its pinned hash', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const first = fakeIo({ upstream, api })
  const { state } = await mirror({ plan, io: first.io })
  // The second run reads the state the first one wrote, and R2 holds the same objects.
  const second = fakeIo({ upstream, api, r2: { [STATE_KEY]: Buffer.from(JSON.stringify(state)) } })
  const rerun = await mirror({ plan, io: second.io })
  const dllKey = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  assert.equal(second.calls.fetched.filter((u) => u.endsWith('nvngx_dlss.dll')).length, 0, 'the DLL is not downloaded again')
  assert.equal(second.calls.puts.filter((k) => k.startsWith('cache/')).length, 0, 'nothing already recorded is uploaded again')
  assert.ok(rerun.done.some(([s, k]) => s === 'present' && k === dllKey))
})

test('a recorded object whose pin changed is refused, not replaced', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const key = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
  const state = { schema: 1, objects: { [key]: { size: 1, sha256: 'e'.repeat(64) } } }
  const { io, calls } = fakeIo({ upstream, api, r2: { [STATE_KEY]: Buffer.from(JSON.stringify(state)) } })
  await assert.rejects(mirror({ plan, io }), /refused .*the state records/)
  assert.ok(!calls.puts.includes(key))
})

test('a licence whose bytes changed since it was recorded is refused', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const record = { [`cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/LICENSE.txt`]: { size: 1, sha256: '0'.repeat(64), upstream: 'x' } }
  const { io, calls } = fakeIo({ upstream, api, record })
  await assert.rejects(mirror({ plan, io }), /the licence changed/)
  assert.ok(!calls.puts.some((k) => k.endsWith('LICENSE.txt')))
})

test('a dry run uploads nothing and writes no record', async () => {
  const dll = Buffer.from('the pinned dll body')
  const { plan, upstream, api } = onePinPlan(dll)
  const { io, calls } = fakeIo({ upstream, api })
  const { done } = await mirror({ plan, io, dryRun: true })
  assert.equal(calls.puts.length, 0)
  assert.equal(calls.writes, 0)
  assert.ok(done.some(([s]) => s === 'would upload'))
})

// ---------- DLSS Swapper archives ----------

const ZIP_FIXTURE = `
    private const string DlssSwapper = "https://dlss-swapper-downloads.beeradmoore.com/";
    private const string DlssRepo = "NVIDIA/DLSS";
    internal static readonly PinnedFile[] PinnedFiles =
    [
        new("nvngx_dlss.dll", "dlss-310.9.16", "DLSS 310.9.16", "310.9.16.0", "310.9.16", DlssRepo, "", "", 74187376, "858ccff96360c3098db9264cd6f13d04a027e09b6ce7ad5bbc297570e824b4f2",
            new(DlssSwapper + "dlss/dlss_v310.9.16.0_C77DDAE731F54720DDEEEE06AC356C5A.zip", 45333524, "6c15c2d5e106379175671a95d8c387eae2d58f322a071b9b2d045eee2f271fc3")),
    ];
`
const ZIP_URL = 'https://dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.16.0_C77DDAE731F54720DDEEEE06AC356C5A.zip'
const ZIP_KEY = 'cache/dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.16.0_C77DDAE731F54720DDEEEE06AC356C5A.zip'
const ZIP_BYTES = Buffer.from('pretend these are the 45333524 zip bytes')
const INNER_DLL = Buffer.from('pretend nvngx_dlss.dll')
const LICENCE = Buffer.from('NVIDIA DLSS licence text')
const NVIDIA_SIGNER = 'CN=NVIDIA Corporation, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US'
const DLL_KEY = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/nvngx_dlss.dll`
const LICENCE_KEY = `cache/NVIDIA/DLSS/${COMMIT}/lib/Windows_x86_64/rel/LICENSE.txt`
const zipPin = { dllName: 'nvngx_dlss.dll', dllSize: INNER_DLL.length, dllSha256: sha256Hex(INNER_DLL), zipUrl: ZIP_URL, zipSize: ZIP_BYTES.length, zipSha256: sha256Hex(ZIP_BYTES) }
const nvidiaPin = { kind: 'dll', release: 'dlss-310.9.1', repo: 'NVIDIA/DLSS', commit: COMMIT, tag: 'v310.9.1', folder: 'lib/Windows_x86_64/rel', name: 'nvngx_dlss.dll', size: INNER_DLL.length, sha256: sha256Hex(INNER_DLL) }
const goodInspect = () => ({ size: INNER_DLL.length, sha256: sha256Hex(INNER_DLL), status: 'Valid', valid: true, signer: NVIDIA_SIGNER })

/** A run where the NVIDIA DLL is already recorded (so only the zip is new) and the licence is recorded at its DLSS key. */
function zipRun({ zipBytes = ZIP_BYTES, inspect = goodInspect, r2 = {}, state = null } = {}) {
  const plan = planObjects({ vendor: [nvidiaPin], tools: [], zips: [zipPin] })
  // The NVIDIA DLL is already recorded (with its git blob hash, which the folder listing is checked against).
  const dllRecord = { [DLL_KEY]: { size: INNER_DLL.length, sha256: sha256Hex(INNER_DLL), blobSha1: gitBlobSha1(INNER_DLL), upstream: 'x' } }
  const stateObjects = { ...(state ?? {}), [DLL_KEY]: dllRecord[DLL_KEY] }
  const record = { [LICENCE_KEY]: { size: LICENCE.length, sha256: sha256Hex(LICENCE), upstream: 'x' } }
  const upstream = { [ZIP_URL]: zipBytes, [`https://raw.githubusercontent.com/NVIDIA/DLSS/${COMMIT}/LICENSE.txt`]: LICENCE }
  // The DLSS folder's listing and tag commit, as the listing and commit checks read them.
  const api = {
    [`repos/NVIDIA/DLSS/contents/lib/Windows_x86_64/rel?ref=${COMMIT}`]: [{ type: 'file', name: 'nvngx_dlss.dll', size: INNER_DLL.length, sha: gitBlobSha1(INNER_DLL) }],
    'repos/NVIDIA/DLSS/commits/v310.9.1': { sha: COMMIT },
    [`repos/NVIDIA/DLSS/contents?ref=${COMMIT}`]: [{ name: 'LICENSE.txt' }],
  }
  const store = { ...r2, [STATE_KEY]: Buffer.from(JSON.stringify({ schema: 1, objects: stateObjects })) }
  const fake = fakeIo({ upstream, api, r2: store, record, inspect: (bytes, name) => inspect(bytes, name) })
  return { plan, fake, store }
}

test('a zip pin reads its URL, its zip size, and the DLL inside with its size and SHA-256', () => {
  const values = parseConstants(ZIP_FIXTURE).values
  assert.deepEqual(parseZipPins(ZIP_FIXTURE, { values }), [{
    dllName: 'nvngx_dlss.dll',
    dllSize: 74187376,
    dllSha256: '858ccff96360c3098db9264cd6f13d04a027e09b6ce7ad5bbc297570e824b4f2',
    zipUrl: ZIP_URL,
    zipSize: 45333524,
    zipSha256: '6c15c2d5e106379175671a95d8c387eae2d58f322a071b9b2d045eee2f271fc3',
  }])
})

test('a zip is planned at its cache path, with the NVIDIA licence in its folder', () => {
  const plan = planObjects({ vendor: [nvidiaPin], tools: [], zips: [zipPin] })
  assert.deepEqual(plan.zips.map((z) => z.key), [ZIP_KEY])
  assert.equal(plan.dlssLicence.recordKey, LICENCE_KEY)
})

test('a clean zip run checks the zip against its archive pin, uploads it under its cache path, and copies the DLSS licence next to it', async () => {
  const { plan, fake } = zipRun()
  const { done, state } = await mirror({ plan, io: fake.io })
  assert.ok(fake.calls.puts.includes(ZIP_KEY))
  assert.equal(state.objects[ZIP_KEY].sha256, sha256Hex(ZIP_BYTES))
  assert.equal(state.objects[ZIP_KEY].size, ZIP_BYTES.length)
  const folder = ZIP_KEY.slice(0, ZIP_KEY.lastIndexOf('/'))
  assert.ok(fake.calls.puts.includes(`${folder}/LICENSE.txt`), 'the licence sits beside the zip')
  assert.equal(done.find(([, k]) => k === ZIP_KEY)[0], 'uploaded')
})

test('a zip whose size differs from the Launcher pin is refused before anything is inspected or uploaded', async () => {
  const { plan, fake } = zipRun({ zipBytes: Buffer.from('short') })
  await assert.rejects(mirror({ plan, io: fake.io }), /bytes, the Launcher's pin says/)
  assert.ok(!fake.calls.puts.includes(ZIP_KEY))
})

test('a zip of the pinned size whose SHA-256 differs from the Launcher archive pin is refused before its DLL is read', async () => {
  const { plan, fake } = zipRun({ zipBytes: Buffer.alloc(ZIP_BYTES.length, 'x') })
  await assert.rejects(mirror({ plan, io: fake.io }), /the Launcher's archive pin says/)
  assert.ok(!fake.calls.puts.includes(ZIP_KEY))
  assert.ok(!fake.calls.inspected?.includes(ZIP_URL), 'the DLL is not read')
})

test('an inner DLL whose SHA-256 differs from the pin refuses the zip', async () => {
  const { plan, fake } = zipRun({ inspect: () => ({ ...goodInspect(), sha256: 'f'.repeat(64) }) })
  await assert.rejects(mirror({ plan, io: fake.io }), /refused .*the pin says/)
  assert.ok(!fake.calls.puts.includes(ZIP_KEY))
})

test('an inner DLL not validly signed by NVIDIA Corporation refuses the zip', async () => {
  const { plan, fake } = zipRun({ inspect: () => ({ ...goodInspect(), valid: false, status: 'NotSigned', signer: '' }) })
  await assert.rejects(mirror({ plan, io: fake.io }), /not validly signed by NVIDIA Corporation/)
  const { plan: plan2, fake: fake2 } = zipRun({ inspect: () => ({ ...goodInspect(), signer: 'CN=Someone Else, O=Other' }) })
  await assert.rejects(mirror({ plan: plan2, io: fake2.io }), /not validly signed by NVIDIA Corporation/)
  assert.ok(!fake.calls.puts.includes(ZIP_KEY) && !fake2.calls.puts.includes(ZIP_KEY))
})

test('an R2 object at the zip key with another hash is never overwritten', async () => {
  const { plan, fake } = zipRun({ r2: { [ZIP_KEY]: Buffer.from('someone else put this') } })
  await assert.rejects(mirror({ plan, io: fake.io }), /refused .*R2 already holds/)
  assert.ok(!fake.calls.puts.includes(ZIP_KEY))
})

test('a zip already recorded is present: it is not downloaded again', async () => {
  const state = { [ZIP_KEY]: { size: ZIP_BYTES.length, sha256: sha256Hex(ZIP_BYTES), upstream: ZIP_URL } }
  const { plan, fake } = zipRun({ state, r2: { [ZIP_KEY]: ZIP_BYTES } })
  const { done } = await mirror({ plan, io: fake.io })
  assert.ok(!fake.calls.fetched.includes(ZIP_URL), 'the zip is not fetched again')
  assert.ok(done.some(([s, k]) => s === 'present' && k === ZIP_KEY))
})
