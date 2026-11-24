import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isDiscordSnowflake } from '../src/lib/discordSnowflake.ts'

test('isDiscordSnowflake accepts numeric Discord snowflake ids', () => {
  assert.equal(isDiscordSnowflake('123456789012345678'), true)
  assert.equal(isDiscordSnowflake('1'), true)
  assert.equal(isDiscordSnowflake('12345678901234567890'), true)
})

test('isDiscordSnowflake rejects non-snowflake routes and invalid values', () => {
  assert.equal(isDiscordSnowflake('@me'), false)
  assert.equal(isDiscordSnowflake('friends'), false)
  assert.equal(isDiscordSnowflake('requests'), false)
  assert.equal(isDiscordSnowflake(''), false)
  assert.equal(isDiscordSnowflake(null), false)
  assert.equal(isDiscordSnowflake(undefined), false)
  assert.equal(isDiscordSnowflake('123456789012345678901'), false)
  assert.equal(isDiscordSnowflake('12345abc'), false)
})
