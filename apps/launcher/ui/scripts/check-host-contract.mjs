import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { compareEvent, declFields, dispatchArms, eventEmitters, isStub, realHandler, typeFields } from './host-contract-lib.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const read = file => fs.readFileSync(path.join(root, file), 'utf8').replaceAll('\r\n', '\n')
const protocol = read('ExoLauncher/Services/ExoBridgeProtocol.cs')
const version = Number(protocol.match(/const int Version = (\d+)/)?.[1])
if (version !== 1) throw Error('Review the protocol version and generator before changing the bridge schema.')
// The wire records and their UI names. A property is a bool, a string, one of these records or an array of one.
const records = { ExoUpdateResponse: 'AppUpdateResponse', ExoWhatsNewRelease: 'WhatsNewWire', ExoWhatsNewItem: 'WhatsNewItemWire' }
const tsType = kind => {
  if (kind === 'bool') return 'boolean'
  if (kind === 'string') return 'string'
  if (kind.endsWith('[]')) return `${tsType(kind.slice(0, -2))}[]`
  if (records[kind]) return records[kind]
  throw Error(`Review the generator: the bridge schema has a type it does not know (${kind}).`)
}
const generated = '// Generated from ExoBridgeProtocol.cs. Run npm run contracts:write after reviewing wire changes.\n' + Object.entries(records).map(([record, name]) => {
  const body = protocol.match(new RegExp(`record ${record}\\s*\\{([\\s\\S]*?)\\n\\}`))?.[1]
  if (!body) throw Error(`Review the generator: the bridge schema has no record ${record}.`)
  const properties = [...body.matchAll(/public ([\w[\]]+)(\?)? (\w+) \{ get; init; \}/g)]
  return `export type ${name} = {\n${properties.map(([, kind, nullable, prop]) => `  ${prop[0].toLowerCase() + prop.slice(1)}${nullable ? '?' : ''}: ${tsType(kind)}`).join('\n')}\n}\n`
}).join('')
const uiFile = 'ui/src/lib/hostWire.ts'
if (process.argv.includes('--write')) fs.writeFileSync(path.join(root, uiFile), generated)
else if (read(uiFile) !== generated) throw Error('Generated UI wire types are stale. Review the native schema and run npm run contracts:write.')

const host = read('ui/src/lib/host.ts')
const tree = ts.createSourceFile('host.ts', host, ts.ScriptTarget.Latest, true)
// Arms that only answer yes, each with the reason that is fine: a stub is not counted as a handler unless it is listed here.
const STUB_ARMS = new Map([
  /* method => reason (one real sentence) */
])
const calls = new Map()
const unreadable = []
const events = new Map()
const eventTypes = new Map()
function visit(node) {
  if (ts.isCallExpression(node) && node.expression.getText(tree) === 'rawCall' && node.arguments.length) {
    const type = node.typeArguments?.[0]?.getText(tree).replace(/\s+/g, ' ') ?? 'unknown'
    // The method name is a string, or a choice between strings (force ? 'library.refresh' : 'library.get'); anything
    // else is a call this check cannot see, which is itself a problem.
    const names = (arg) => ts.isStringLiteralLike(arg) ? [arg.text]
      : ts.isConditionalExpression(arg) ? [...names(arg.whenTrue), ...names(arg.whenFalse)]
      : ts.isParenthesizedExpression(arg) ? names(arg.expression) : null
    const seen = names(node.arguments[0])
    if (seen) for (const name of seen) calls.set(name, type)
    else if (node.getSourceFile() === tree && !node.getText(tree).startsWith('rawCall<T>(')) unreadable.push(node.arguments[0].getText(tree))
  }
  // Typed host events: the payload the UI relies on for each pushed name.
  if (ts.isInterfaceDeclaration(node) && node.name.text === 'HostEventMap') {
    for (const member of node.members) if (ts.isPropertySignature(member) && ts.isStringLiteral(member.name)) {
      events.set(member.name.text, member.type?.getText(tree).replace(/\s+/g, ' ') ?? 'unknown')
      eventTypes.set(member.name.text, member.type)
    }
  }
  ts.forEachChild(node, visit)
}
visit(tree)
// Methods: each one the page calls is an arm of the bridge's dispatcher switch with a handler that does the work
// (a name in a log line or a comment is not a handler). rpc.cancel is answered before the switch.
const arms = dispatchArms(read('ExoLauncher/Services/WebHostBridge.cs'))
const answeredBeforeSwitch = new Set(['rpc.cancel'])
const problems = []
for (const name of unreadable) problems.push(`The page calls a host method named by ${name}, which this check cannot read: write each name as a string`)
for (const [method, reason] of STUB_ARMS) {
  if (!/^\S+(\s+\S+){4,}\.$/.test(reason)) problems.push(`STUB_ARMS: the reason for ${method} must be a real sentence`)
  if (!arms.has(method) || !isStub(arms.get(method))) problems.push(`STUB_ARMS lists ${method}, which is no longer a stub arm: remove it`)
}
for (const method of calls.keys()) {
  if (answeredBeforeSwitch.has(method)) continue
  if (!arms.has(method)) problems.push(`Missing native handler: ${method} is not an arm of the dispatcher in WebHostBridge.cs`)
  else if (!realHandler(arms.get(method))) problems.push(`No real handler: ${method} => ${arms.get(method)}`)
  else if (isStub(arms.get(method)) && !STUB_ARMS.has(method)) problems.push(`Stub handler: ${method} => ${arms.get(method)} answers yes without doing anything (list it in STUB_ARMS with a reason if that is right)`)
}
const nativeAll = fs.readdirSync(path.join(root, 'ExoLauncher'), { recursive: true })
  .filter(file => file.endsWith('.cs') && !/(^|[\\/])(bin|obj)[\\/]/.test(file))
  .map(file => read(path.join('ExoLauncher', file))).join('\n')
// Events: each typed event has a native sender, and where both sides spell the payload out (a type literal or a
// same-file interface here, `new { ... }` there) the fields agree: renaming one side's field fails this check.
// A page type declared in another file (a literal object type or an interface without a parent) is found by name across ui/src.
const declared = new Map()
for (const file of fs.readdirSync(path.join(root, 'ui/src'), { recursive: true }).filter(f => /\.tsx?$/.test(f))) {
  const text = read(path.join('ui/src', file))
  if (!/(interface|type)\s+[A-Z]/.test(text)) continue
  const other = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true)
  for (const st of other.statements) {
    const name = (ts.isInterfaceDeclaration(st) || ts.isTypeAliasDeclaration(st)) ? st.name.text : null
    if (name) declared.set(name, declared.has(name) ? null : { tree: other, statement: st })
  }
}
function importedFields(typeNode) {
  if (!typeNode || !ts.isTypeReferenceNode(typeNode)) return null
  const found = declared.get(typeNode.typeName.getText(tree))
  return found ? declFields(found.tree, found.statement) : null
}
let comparedEvents = 0
for (const event of events.keys()) {
  const emitters = eventEmitters(nativeAll, event)
  if (!emitters.length) { problems.push(`Missing native event: ${event} has no sender (a name in an RPC arm or a string is not one)`); continue }
  const fields = typeFields(tree, eventTypes.get(event)) ?? importedFields(eventTypes.get(event))
  if (fields && emitters.some(Boolean)) comparedEvents++
  problems.push(...compareEvent(event, fields, emitters))
}
if (problems.length) throw Error(`Host/UI contract:\n  ${problems.join('\n  ')}`)
const sorted = map => Object.fromEntries([...map].sort(([a], [b]) => a.localeCompare(b)))
const manifest = JSON.stringify({ version, methods: sorted(calls), events: sorted(events) }, null, 2) + '\n'
const manifestFile = path.join(root, 'ui/contracts/host-v1.json')
if (process.argv.includes('--write')) { fs.mkdirSync(path.dirname(manifestFile), { recursive: true }); fs.writeFileSync(manifestFile, manifest) }
else if (fs.readFileSync(manifestFile, 'utf8').replaceAll('\r\n', '\n') !== manifest) throw Error('Host/UI contract changed. Review consumers and regenerate the versioned manifest with npm run contracts:write.')
console.log(`Host protocol v${version}: ${calls.size} UI methods are dispatcher arms with handlers, ${events.size} typed events have native emitters (${comparedEvents} with their fields compared); generated update schema is current.`)
