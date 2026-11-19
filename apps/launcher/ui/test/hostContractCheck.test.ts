// scripts/host-contract-lib.mjs, the readers behind npm run contracts. Ways the check could pass when it should not:
// a method named only in a log string or a comment counts as handled; an arm that throws counts as a handler; an
// "or" arm hides its second label; a renamed field on either side of an event goes unnoticed; a field the type
// requires but one emitter leaves out goes unnoticed.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import ts from 'typescript'
import { anonFields, compareEvent, dispatchArms, eventEmitters, isStub, realHandler, typeFields } from '../scripts/host-contract-lib.mjs'

const BRIDGE = `
  AppLog.Warn("game.ghost is retired"); // "game.comment" => nothing
  object? result = method switch
  {
      // The library's checks run first: off the UI thread, "quoted, with a comma".
      "library.get" => await Task.Run(() => LibraryGetAsync(paramsEl, hasParams)).ConfigureAwait(false),
      "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
      "hid.open" or "hid.close"
          => await Task.Run(() => HidCallAsync(method, paramsEl, hasParams, requestToken)).ConfigureAwait(true),
      "game.retired" => throw new InvalidOperationException("gone"),
      "app.noop" => null,
      _ => throw new InvalidOperationException($"Unknown method: {method}")
  };
`

test('only arms of the dispatcher switch are handlers, each label of an or arm included', () => {
  const arms = dispatchArms(BRIDGE)
  assert.deepEqual([...arms.keys()].sort(), ['app.noop', 'game.launch', 'game.retired', 'hid.close', 'hid.open', 'library.get'])
  assert.equal(arms.has('game.ghost'), false)
  assert.equal(arms.has('game.comment'), false)
  assert.equal(realHandler(arms.get('game.launch')!), true)
  assert.equal(realHandler(arms.get('hid.close')!), true)
  assert.equal(realHandler(arms.get('game.retired')!), false)
  assert.equal(realHandler(arms.get('app.noop')!), false)
})

test('a bridge with no dispatcher switch is an error, not an empty pass', () => {
  assert.throws(() => dispatchArms('switch (level) { case "x": break; }'), /no `object\? result = method switch/)
})

test('anonymous object fields are read as the bridge serialises them', () => {
  assert.deepEqual(anonFields('gameId = game.Id, ok = true, message = a ? "x, y" : b.Message, result.ProcessId, maximized, Total = list.Count(x => x > 1)'),
    ['gameId', 'ok', 'message', 'processId', 'maximized', 'total'])
})

const CS = `
  PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Game started.", phase = "running" });
  PostEvent("launch.status", new
  {
      gameId = game.Id, // the exact id
      ok = result.Ok,
      message = result.Message,
      phase = "stopped",
  });
  PostEvent("install.progress", MapProgress(p));
  PostEvent("other.thing", payload);
  private static object MapProgress(InstallProgress p) => new
  {
      gameId = p.GameId,
      isActive = p.IsActive,
  };
`
const fieldsOf = (source: string, name: string) => {
  const tree = ts.createSourceFile('host.ts', source, ts.ScriptTarget.Latest, true)
  const map = tree.statements.find(s => ts.isInterfaceDeclaration(s) && s.name.text === 'HostEventMap') as ts.InterfaceDeclaration
  const member = map.members.find(m => m.name?.getText(tree).includes(name)) as ts.PropertySignature
  return typeFields(tree, member.type!)
}
const TS = `
  export interface InstallProgress { gameId: string; isActive: boolean; percent?: number | null }
  export interface HostEventMap {
    'launch.status': { gameId: string; ok: boolean; message?: string | null; phase: string }
    'install.progress': InstallProgress
    'shaders.changed': Record<string, never>
    'music.playback': MusicPlayback
  }
`

test('emitters are found inline and through a mapping method; a payload built elsewhere is not guessed', () => {
  assert.deepEqual(eventEmitters(CS, 'launch.status'), [['gameId', 'ok', 'message', 'phase'], ['gameId', 'ok', 'message', 'phase']])
  assert.deepEqual(eventEmitters(CS, 'install.progress'), [['gameId', 'isActive']])
  assert.deepEqual(eventEmitters(CS, 'other.thing'), [null])
})

test('matching fields pass; a field renamed on either side fails', () => {
  assert.deepEqual(compareEvent('launch.status', fieldsOf(TS, 'launch.status'), eventEmitters(CS, 'launch.status')), [])
  assert.deepEqual(compareEvent('install.progress', fieldsOf(TS, 'install.progress'), eventEmitters(CS, 'install.progress')), [])
  const renamedNative = eventEmitters(CS.replace('gameId = p.GameId', 'id = p.GameId'), 'install.progress')
  const problems = compareEvent('install.progress', fieldsOf(TS, 'install.progress'), renamedNative)
  assert.equal(problems.length, 2)
  assert.match(problems.join('\n'), /sends "id"/)
  assert.match(problems.join('\n'), /requires "gameId"/)
  const renamedPage = fieldsOf(TS.replace('phase: string', 'step: string'), 'launch.status')
  assert.notDeepEqual(compareEvent('launch.status', renamedPage, eventEmitters(CS, 'launch.status')), [])
})

test('an optional field may be missing from an emitter; a required one may not', () => {
  const sent = [['gameId', 'ok', 'phase']]
  assert.deepEqual(compareEvent('launch.status', fieldsOf(TS, 'launch.status'), sent), [])
  assert.match(compareEvent('launch.status', fieldsOf(TS, 'launch.status'), [['gameId', 'phase']]).join(), /requires "ok"/)
})

test('Record<string, never> is no fields, and an imported type is not checked', () => {
  assert.deepEqual(fieldsOf(TS, 'shaders.changed'), [])
  assert.equal(fieldsOf(TS, 'music.playback'), null)
  assert.deepEqual(compareEvent('music.playback', null, [['anything']]), [])
})

test('an arm that only answers yes is a stub; one that calls something is not', () => {
  for (const stub of ['true', 'false', 'new { ok = true }', 'new { ok = false, message = "Not here." }', 'Task.CompletedTask', 'await Task.FromResult(true)', '(object)true', 'new { ok = true }.ConfigureAwait(false)'])
    assert.equal(isStub(stub), true, stub)
  for (const real of ['await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true)', 'new { ok = Save(x) }', 'new { ok = true, items = list }', 'Handle(true)', 'await Task.Run(() => Work())'])
    assert.equal(isStub(real), false, real)
})

const SENDERS = `
  Event?.Invoke("discord.state", new { state, message });
  DiscordSession.Publish("discord.voiceTune", new { state = "recording" });
  events.Add(("discord.unread", new { channelId, count }));
  _bridge?.PostCaptureEvent("capture.saved", new { item, edit });
  VideoFrame?.Invoke(new { @event = "discord.video.frame", source, userId, keyframe }, data);
  var m = new { @event = "music.volume", data = new { level, source = "app" } };
  AppLog.Info("discord.ghost is retired");
`
test('every way the host sends an event is a sender, and a name in a log line or an RPC arm is not', () => {
  assert.deepEqual(eventEmitters(SENDERS, 'discord.state'), [['state', 'message']])
  assert.deepEqual(eventEmitters(SENDERS, 'discord.voiceTune'), [['state']])
  assert.deepEqual(eventEmitters(SENDERS, 'discord.unread'), [['channelId', 'count']])
  assert.deepEqual(eventEmitters(SENDERS, 'capture.saved'), [['item', 'edit']])
  assert.deepEqual(eventEmitters(SENDERS, 'discord.video.frame'), [['source', 'userId', 'keyframe']])
  assert.deepEqual(eventEmitters(SENDERS, 'music.volume'), [['level', 'source']])
  assert.deepEqual(eventEmitters(SENDERS, 'discord.ghost'), [])
  assert.deepEqual(eventEmitters('"discord.typing" => DiscordTyping(paramsEl)', 'discord.typing'), [])
})
