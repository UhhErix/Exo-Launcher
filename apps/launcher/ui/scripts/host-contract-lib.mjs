// Pure readers for scripts/check-host-contract.mjs, kept apart so ui/test/hostContractCheck.test.ts runs them on snippets.
import ts from 'typescript'

/** The text between the bracket at `open` and its partner, with strings and nested brackets skipped. */
function balanced(text, open) {
  const pairs = { '{': '}', '(': ')', '[': ']' }
  const stack = []
  for (let i = open; i < text.length; i++) {
    const ch = text[i]
    if (ch === '/' && text[i + 1] === '/') { i = text.indexOf('\n', i); if (i < 0) return null; continue }
    if (ch === '/' && text[i + 1] === '*') { i = text.indexOf('*/', i) + 1; if (i <= 0) return null; continue }
    if (ch === '"' || ch === "'") {
      // C# strings: skip to the closing quote (verbatim and interpolated strings are close enough for field names).
      for (i++; i < text.length && text[i] !== ch; i++) if (text[i] === '\\') i++
      continue
    }
    if (pairs[ch]) stack.push(pairs[ch])
    else if (ch === stack[stack.length - 1]) { stack.pop(); if (!stack.length) return text.slice(open + 1, i) }
  }
  return null
}

/** Split on commas outside brackets and strings. */
function splitTop(body) {
  const out = []
  let depth = 0, current = ''
  for (let i = 0; i < body.length; i++) {
    const ch = body[i]
    if (ch === '/' && body[i + 1] === '/') { const end = body.indexOf('\n', i); i = end < 0 ? body.length : end; continue }
    if (ch === '/' && body[i + 1] === '*') { const end = body.indexOf('*/', i); i = end < 0 ? body.length : end + 1; continue }
    if (ch === '"' || ch === "'") {
      const start = i
      for (i++; i < body.length && body[i] !== ch; i++) if (body[i] === '\\') i++
      current += body.slice(start, i + 1)
      continue
    }
    if ('({['.includes(ch)) depth++
    else if (')}]'.includes(ch)) depth--
    if (ch === ',' && depth === 0) { out.push(current); current = '' } else current += ch
  }
  out.push(current)
  return out.map(s => s.replace(/\/\/[^\n]*/g, '').trim()).filter(Boolean)
}

const camel = name => name[0].toLowerCase() + name.slice(1)

/** The JSON field names of a C# anonymous object's body (`a = 1, b, x.Status`), camel-cased as the bridge serialises them. */
export function anonFields(body) {
  return splitTop(body).map(member => {
    const named = /^@?([A-Za-z_]\w*)\s*=(?!=)/.exec(member)
    if (named) return camel(named[1])
    const last = /([A-Za-z_]\w*)\s*$/.exec(member)
    return last ? camel(last[1]) : null
  })
}

/**
 * The method names the bridge's dispatcher answers: the labels of the arms of `object? result = method switch { ... }`
 * (`"a" => ...`, `"a" or "b" => ...`), each with its handler expression. A label that only appears elsewhere (a log
 * line, a comment) is not a handler.
 */
export function dispatchArms(cs) {
  const start = cs.search(/object\?\s+result\s*=\s*method\s+switch\s*\{/)
  if (start < 0) throw Error('Review the contract check: WebHostBridge.cs has no `object? result = method switch { ... }` dispatcher.')
  const body = balanced(cs, cs.indexOf('{', start))
  if (body === null) throw Error('Review the contract check: the dispatcher switch does not close.')
  const arms = new Map()
  for (const arm of splitTop(body)) {
    const m = /^((?:"[^"]+"\s*(?:or\s*)?)+)\s*=>\s*([\s\S]+)$/.exec(arm)
    if (!m) continue
    for (const [, label] of m[1].matchAll(/"([^"]+)"/g)) arms.set(label, m[2].trim())
  }
  return arms
}

/** True when an arm's expression does the work: not a throw, a null, or an empty answer. */
export function realHandler(expression) {
  return !!expression && !/^(throw\b|null\b|default\b|new\s*\{\s*\}$)/.test(expression)
}

/**
 * True when an arm only answers "yes" without doing anything: `true`, `new { ok = true }`, `Task.CompletedTask`, a
 * completed task of a constant. Such an arm is counted as a handler only when check-host-contract.mjs lists it, with its reason.
 */
export function isStub(expression) {
  const e = (expression ?? '').replace(/\s+/g, ' ').replace(/\.ConfigureAwait\([^)]*\)$/, '').replace(/^await /, '').trim()
  return /^(\(object\)\s*)?(true|false)$/.test(e) || /^(Value)?Task\.CompletedTask$/.test(e) || /^(Value)?Task\.FromResult\(\s*(true|false|null|"[^"]*")?\s*\)$/.test(e) ||
    /^new \{ ok = (true|false) \}$/.test(e) || /^new \{ ok = (true|false), (message|error) = "[^"]*" \}$/.test(e)
}

/**
 * Every place the native code sends `event` with an object it builds in place: `"event", new { ... }`, or
 * `"event", Map(x)` where `Map` is declared as `object Map(...) => new { ... }`. Each is that object's field list;
 * an emitter whose payload is built elsewhere is `null` (not checked).
 */
export function eventEmitters(cs, event) {
  const name = event.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const out = []
  // PostEvent("e", x), PostCaptureEvent("e", x), SerializeEvent("e", x), events.Add(("e", x)) and { @event = "e", data = x }.
  const sends = `(?:\\w*Event\\(\\s*"${name}"\\s*,|Invoke\\(\\s*"${name}"\\s*,|Publish\\(\\s*"${name}"\\s*,|\\.Add\\(\\(\\s*"${name}"\\s*,|@event\\s*=\\s*"${name}"\\s*,\\s*data\\s*=)\\s*`
  for (const m of cs.matchAll(new RegExp(sends, 'g'))) {
    const at = m.index + m[0].length
    const rest = cs.slice(at)
    const inline = /^new\s*\{/.exec(rest)
    if (inline) { out.push(anonFields(balanced(cs, at + inline[0].length - 1) ?? '')); continue }
    const call = /^([A-Za-z_]\w*)\(/.exec(rest)
    const decl = call && new RegExp(`object\\??\\s+${call[1]}\\s*\\([^)]*\\)\\s*=>\\s*new\\s*\\{`).exec(cs)
    out.push(decl ? anonFields(balanced(cs, decl.index + decl[0].length - 1) ?? '') : null)
  }
  // The whole object is the event: new { @event = "e", a, b } (the video frames).
  for (const m of cs.matchAll(new RegExp(`new\\s*\\{(?=\\s*@event\\s*=\\s*"${name}"\\s*,(?!\\s*data\\s*=))`, 'g')))
    out.push(anonFields(balanced(cs, m.index + m[0].length - 1) ?? '').filter(field => field !== 'event'))
  return out
}

/**
 * A HostEventMap entry's fields, when its type is a literal (`{ a: string; b?: number }`) or an interface or type
 * alias declared in the same file: each field's name and whether it is optional. `Record<string, never>` is no
 * fields. Anything else (an imported type, a union) is `null`: not checked.
 */
export function typeFields(tree, typeNode) {
  const fromMembers = members => members.filter(ts.isPropertySignature).map(m => ({ name: m.name.getText(tree).replace(/^['"]|['"]$/g, ''), optional: !!m.questionToken }))
  if (ts.isTypeLiteralNode(typeNode)) return fromMembers(typeNode.members)
  if (ts.isTypeReferenceNode(typeNode)) {
    const ref = typeNode.typeName.getText(tree)
    if (ref === 'Record' && typeNode.typeArguments?.[1]?.kind === ts.SyntaxKind.NeverKeyword) return []
    for (const statement of tree.statements) {
      if (ts.isInterfaceDeclaration(statement) && statement.name.text === ref && !statement.heritageClauses) return fromMembers(statement.members)
      if (ts.isTypeAliasDeclaration(statement) && statement.name.text === ref && ts.isTypeLiteralNode(statement.type)) return fromMembers(statement.type.members)
    }
  }
  return null
}

/**
 * Compare one event's page type with what the host sends: every field the host sends is in the type (or the page
 * reads a field nobody named), and every field the type requires is in every emitter. Returns the problems.
 */
export function compareEvent(event, fields, emitters) {
  const problems = []
  if (!fields) return problems
  const known = new Set(fields.map(f => f.name))
  for (const [i, sent] of emitters.entries()) {
    if (!sent) continue
    for (const field of sent) if (field && !known.has(field)) problems.push(`${event}: the host sends "${field}" (emitter ${i + 1}) but HostEventMap does not name it`)
    for (const f of fields) if (!f.optional && !sent.includes(f.name)) problems.push(`${event}: HostEventMap requires "${f.name}" but emitter ${i + 1} does not send it`)
  }
  return problems
}

/** The fields of one declaration (an interface with no parent, or a type alias of a literal) found in another file. */
export function declFields(tree, statement) {
  const fromMembers = members => members.filter(ts.isPropertySignature).map(m => ({ name: m.name.getText(tree).replace(/^['"]|['"]$/g, ''), optional: !!m.questionToken }))
  if (ts.isInterfaceDeclaration(statement) && !statement.heritageClauses) return fromMembers(statement.members)
  if (ts.isTypeAliasDeclaration(statement) && ts.isTypeLiteralNode(statement.type)) return fromMembers(statement.type.members)
  return null
}
