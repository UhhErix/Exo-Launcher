// Regression tests for Phase 5.3 Discord protocol parity: gift link parsing, Stage lifecycle controls,
// GiftCard embed/redemption wiring, and read-only Embedded Activity voice status.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { giftCode } from '../src/lib/discordGift.ts'

const voiceSource = readFileSync(new URL('../src/components/discord/voice.tsx', import.meta.url), 'utf8')
const messageSource = readFileSync(new URL('../src/components/discord/Message.tsx', import.meta.url), 'utf8')
const channelsSource = readFileSync(new URL('../src/components/discord/Channels.tsx', import.meta.url), 'utf8')
const markdownSource = readFileSync(new URL('../src/components/discord/markdown.tsx', import.meta.url), 'utf8')

test('giftCode parses discord.gift and discord.com/gifts variants and rejects invalid links', () => {
  assert.equal(giftCode('https://discord.gift/AbCdEfGh1234'), 'AbCdEfGh1234')
  assert.equal(giftCode('https://www.discord.gift/Valid-Code99?src=chat#hero'), 'Valid-Code99')
  assert.equal(giftCode('https://discord.com/gifts/NitroGift2026/'), 'NitroGift2026')
  assert.equal(giftCode('https://ptb.discord.com/gifts/PtbGiftCode123'), 'PtbGiftCode123')
  assert.equal(giftCode('https://canary.discord.com/gifts/CanaryGift99'), 'CanaryGift99')
  assert.equal(giftCode('https://discordapp.com/gifts/LegacyGift1234'), 'LegacyGift1234')

  assert.equal(giftCode('https://discord.gift/short'), null)
  assert.equal(giftCode('https://discord.gg/AbCdEfGh1234'), null)
  assert.equal(giftCode('https://evil.com/discord.gift/AbCdEfGh1234'), null)
  assert.equal(giftCode('not a url'), null)
})

test('StageBanner wires stageStart, stageEdit, stageEnd and VoiceRoom shows read-only channel.activity', () => {
  assert.match(voiceSource, /'stageStart' \| 'stageEdit' \| 'stageEnd'/)
  assert.match(voiceSource, /host\.discordVoice\(action,\s*\{\s*channelId:\s*channel\.id/)
  assert.match(voiceSource, /onManage\(editing === 'start' \? 'stageStart' : 'stageEdit',\s*clean\)/)
  assert.match(voiceSource, /onManage\('stageEnd'\)/)
  assert.match(voiceSource, /!stageRoom\.isStage && channel\.activity/)
  assert.match(channelsSource, /channel\.activity \? <VoiceActivity name=\{channel\.activity\} \/> : null/)
})

test('Message renders GiftCard via discordServer gift/redeemGift and markdown re-exports giftCode', () => {
  assert.match(markdownSource, /export \{ giftCode \}/)
  assert.match(messageSource, /host\.discordServer\('gift',\s*\{\s*code\s*\}\)/)
  assert.match(messageSource, /host\.discordServer\('redeemGift',/)
  assert.match(messageSource, /<GiftCard\b/)
})
