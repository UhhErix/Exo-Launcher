import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const manifestPath = path.join(root, 'ui/contracts/host-v1.json')
const rustOutPath = path.join(root, 'native-ui/crates/exo-wire/src/generated.rs')
const csOutPath = path.join(root, 'ExoLauncher/Services/ExoWireSchema.Generated.cs')

const fnv1a64 = (text) => {
  let hash = 0xcbf29ce484222325n
  const prime = 0x100000001b3n
  const mask = 0xffffffffffffffffn
  const bytes = Buffer.from(text, 'utf8')
  for (const byte of bytes) {
    hash ^= BigInt(byte)
    hash = (hash * prime) & mask
  }
  return hash
}

const rustIdent = (prefix, name) =>
  prefix + name.replaceAll(/[^a-zA-Z0-9]+/g, '_').replaceAll(/([a-z0-9])([A-Z])/g, '$1_$2').toUpperCase()

const csIdent = (prefix, name) =>
  prefix + name.split(/[^a-zA-Z0-9]+/).filter(Boolean).map(p => p[0].toUpperCase() + p.slice(1)).join('')

const RESERVED_METHODS = new Map([
  ['rpc.cancel', 65535],
])

const HOT_METHODS = [
  'library.get',
  'library.refresh',
  'game.get',
  'game.extras',
  'shell.bootstrap',
  'rpc.cancel',
]

const HOT_EVENTS = [
  'autopilot.live',
  'install.progress',
  'library.updated',
]

export function renderWireSchema(manifestText) {
  const manifest = JSON.parse(manifestText)
  const version = Number(manifest.version)
  if (!Number.isInteger(version) || version < 1) {
    throw Error(`Invalid host contract version: ${manifest.version}`)
  }
  const methods = Object.keys(manifest.methods ?? {}).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
  const events = Object.keys(manifest.events ?? {}).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
  if (methods.length > 65000 || events.length > 65000) {
    throw Error('Host contract exceeds u16 wire id range.')
  }
  const hashInput = `v${version}\n${methods.join('\n')}\n--\n${events.join('\n')}\n`
  const schemaHash = fnv1a64(hashInput)
  const schemaHashHex = schemaHash.toString(16).padStart(16, '0')

  const methodIdMap = new Map(methods.map((m, idx) => [m, idx + 1]))
  for (const [name, id] of RESERVED_METHODS) {
    if (!methodIdMap.has(name)) methodIdMap.set(name, id)
  }
  const eventIdMap = new Map(events.map((e, idx) => [e, idx + 1]))

  const rustHotMethods = HOT_METHODS.map((name) => {
    const id = methodIdMap.get(name) ?? 0
    return `pub const ${rustIdent('METHOD_ID_', name)}: u16 = ${id};`
  }).join('\n')

  const rustHotEvents = HOT_EVENTS.map((name) => {
    const id = eventIdMap.get(name) ?? 0
    return `pub const ${rustIdent('EVENT_ID_', name)}: u16 = ${id};`
  }).join('\n')

  const rustMethods = methods.map((m) => `    ${JSON.stringify(m)},`).join('\n')
  const rustEvents = events.map((e) => `    ${JSON.stringify(e)},`).join('\n')

  const rustSource = [
    `pub const HOST_CONTRACT_VERSION: u32 = ${version};`,
    `pub const SCHEMA_HASH: u64 = 0x${schemaHashHex};`,
    `pub const METHOD_ID_UNKNOWN: u16 = 0;`,
    `pub const EVENT_ID_UNKNOWN: u16 = 0;`,
    '',
    rustHotMethods,
    '',
    rustHotEvents,
    '',
    'pub const METHODS: &[&str] = &[',
    rustMethods,
    '];',
    '',
    'pub const EVENTS: &[&str] = &[',
    rustEvents,
    '];',
    '',
    'pub fn method_id(name: &str) -> u16 {',
    '    if name == "rpc.cancel" {',
    '        return METHOD_ID_RPC_CANCEL;',
    '    }',
    '    match METHODS.binary_search(&name) {',
    '        Ok(idx) => (idx + 1) as u16,',
    '        Err(_) => METHOD_ID_UNKNOWN,',
    '    }',
    '}',
    '',
    'pub fn method_name(id: u16) -> Option<&\'static str> {',
    '    if id == 0 {',
    '        return None;',
    '    }',
    '    if id == METHOD_ID_RPC_CANCEL {',
    '        return Some("rpc.cancel");',
    '    }',
    '    METHODS.get((id - 1) as usize).copied()',
    '}',
    '',
    'pub fn event_id(name: &str) -> u16 {',
    '    match EVENTS.binary_search(&name) {',
    '        Ok(idx) => (idx + 1) as u16,',
    '        Err(_) => EVENT_ID_UNKNOWN,',
    '    }',
    '}',
    '',
    'pub fn event_name(id: u16) -> Option<&\'static str> {',
    '    if id == 0 {',
    '        return None;',
    '    }',
    '    EVENTS.get((id - 1) as usize).copied()',
    '}',
    '',
  ].join('\n')

  const csHotMethods = HOT_METHODS.map((name) => {
    const id = methodIdMap.get(name) ?? 0
    return `    internal const ushort ${csIdent('Method', name)} = ${id};`
  }).join('\n')

  const csHotEvents = HOT_EVENTS.map((name) => {
    const id = eventIdMap.get(name) ?? 0
    return `    internal const ushort ${csIdent('Event', name)} = ${id};`
  }).join('\n')

  const csMethods = methods.map((m) => `        ${JSON.stringify(m)},`).join('\n')
  const csEvents = events.map((e) => `        ${JSON.stringify(e)},`).join('\n')

  const csSource = [
    '#nullable enable',
    '',
    'using System.Collections.Frozen;',
    '',
    'namespace ExoLauncher.Services;',
    '',
    'internal static class ExoWireSchema',
    '{',
    `    internal const int HostContractVersion = ${version};`,
    `    internal const ulong SchemaHash = 0x${schemaHashHex}UL;`,
    '    internal const ushort UnknownId = 0;',
    '',
    csHotMethods,
    '',
    csHotEvents,
    '',
    '    internal static readonly string[] Methods =',
    '    [',
    csMethods,
    '    ];',
    '',
    '    internal static readonly string[] Events =',
    '    [',
    csEvents,
    '    ];',
    '',
    '    private static readonly FrozenDictionary<string, ushort> MethodIds = BuildLookup(Methods);',
    '    private static readonly FrozenDictionary<string, ushort> EventIds = BuildLookup(Events);',
    '',
    '    internal static ushort GetMethodId(string name) =>',
    '        name == "rpc.cancel" ? MethodRpcCancel : MethodIds.TryGetValue(name, out var id) ? id : UnknownId;',
    '',
    '    internal static string? GetMethodName(ushort id) =>',
    '        id == MethodRpcCancel ? "rpc.cancel" : id > 0 && id <= Methods.Length ? Methods[id - 1] : null;',
    '',
    '    internal static ushort GetEventId(string name) =>',
    '        EventIds.TryGetValue(name, out var id) ? id : UnknownId;',
    '',
    '    internal static string? GetEventName(ushort id) =>',
    '        id > 0 && id <= Events.Length ? Events[id - 1] : null;',
    '',
    '    private static FrozenDictionary<string, ushort> BuildLookup(string[] names)',
    '    {',
    '        var dict = new Dictionary<string, ushort>(names.Length, StringComparer.Ordinal);',
    '        for (var i = 0; i < names.Length; i++)',
    '            dict[names[i]] = (ushort)(i + 1);',
    '        return dict.ToFrozenDictionary(StringComparer.Ordinal);',
    '    }',
    '}',
    '',
  ].join('\n')

  return { rustSource, csSource, methodCount: methods.length, eventCount: events.length, schemaHashHex }
}

export function syncWireSchema({ write = false, manifestText = null } = {}) {
  const rawManifest = manifestText ?? fs.readFileSync(manifestPath, 'utf8')
  const { rustSource, csSource, methodCount, eventCount, schemaHashHex } = renderWireSchema(rawManifest)
  const readNormalized = (filePath) =>
    fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8').replaceAll('\r\n', '\n') : null

  if (write) {
    fs.mkdirSync(path.dirname(rustOutPath), { recursive: true })
    fs.writeFileSync(rustOutPath, rustSource)
    fs.mkdirSync(path.dirname(csOutPath), { recursive: true })
    fs.writeFileSync(csOutPath, csSource)
    return { methodCount, eventCount, schemaHashHex }
  }

  if (readNormalized(rustOutPath) !== rustSource) {
    throw Error('Generated Rust exo-wire schema is stale. Run npm run contracts:write.')
  }
  if (readNormalized(csOutPath) !== csSource) {
    throw Error('Generated C# ExoWireSchema is stale. Run npm run contracts:write.')
  }
  return { methodCount, eventCount, schemaHashHex }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const write = process.argv.includes('--write')
  const info = syncWireSchema({ write })
  console.log(`exo-wire schema (${write ? 'written' : 'ok'}): ${info.methodCount} methods, ${info.eventCount} events, hash=0x${info.schemaHashHex}`)
}
