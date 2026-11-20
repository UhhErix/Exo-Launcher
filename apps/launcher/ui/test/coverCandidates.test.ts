import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import {
  artTimeoutMs,
  buildPortraitArtCandidates,
  buildWideArtCandidates,
  cacheKey,
  clearWideMiss,
  isIconCover,
  recordWideMiss,
  rememberedCandidateUrl,
  rememberedIndex,
  rememberLoadedUrl,
  withAttempt,
} from '../src/lib/coverUrlPolicy.ts'

describe('coverUrlPolicy', () => {
  it('filters unsafe and rejected URLs and excludes fresh wide misses', () => {
    const missUrl = 'https://cdn.cloudflare.steamstatic.com/steam/apps/570/library_hero.jpg'
    recordWideMiss(missUrl)
    const afterMiss = buildWideArtCandidates(
      {
        id: 'steam:570',
        coverUrl: 'https://covers.exo-launcher.local/570.jpg',
        coverSource: 'steam',
        launchTarget: undefined,
        variants: undefined,
        artRevision: 2,
        includes: undefined,
        heroUrl: 'https://evil.example.com/banner.jpg',
      },
      '570',
      null,
    )
    assert.equal(afterMiss.some((c) => c.url === missUrl), false)
    assert.equal(afterMiss.some((c) => c.url.includes('evil.example.com')), false)
    assert.equal(afterMiss.at(-1)?.derived, true)
    assert.equal(afterMiss.at(-1)?.url, 'https://covers.exo-launcher.local/570.jpg?rev=2')

    clearWideMiss(missUrl)
    const restored = buildWideArtCandidates(
      {
        id: 'steam:570',
        coverUrl: 'https://covers.exo-launcher.local/570.jpg',
        coverSource: 'steam',
        launchTarget: undefined,
        variants: undefined,
        artRevision: 2,
        includes: undefined,
        heroUrl: null,
      },
      '570',
      null,
    )
    assert.equal(restored.some((c) => c.url === missUrl && !c.derived), true)
  })

  it('orders portrait fallbacks cleanly with derived store fallback at the end', () => {
    const list = buildPortraitArtCandidates(
      { coverUrl: 'https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg', artRevision: 0 },
      '730',
      [],
    )
    assert.equal(list[0]?.url, 'https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900_2x.jpg')
    assert.equal(list[1]?.url, 'https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg')
    assert.equal(list.at(-1)?.derived, true)
  })

  it('appends retry attempts only on Exo local cache / thumb URLs and remembers loaded keys', () => {
    assert.equal(withAttempt('https://covers.exo-launcher.local/a.jpg', 2), 'https://covers.exo-launcher.local/a.jpg?retry=2')
    assert.equal(withAttempt('https://cdn.cloudflare.steamstatic.com/a.jpg', 2), 'https://cdn.cloudflare.steamstatic.com/a.jpg')
    assert.equal(artTimeoutMs('https://covers.exo-launcher.local/a.jpg'), 1500)
    assert.equal(isIconCover('https://covers.exo-launcher.local/icon_game.png', undefined), true)

    const key = cacheKey('game-1', 'poster', 1)
    const candidates = [{ url: 'https://covers.exo-launcher.local/g1.jpg', derived: false }]
    rememberLoadedUrl(key, 'https://covers.exo-launcher.local/g1.jpg')
    assert.equal(rememberedIndex(key, candidates), 0)
    assert.equal(rememberedCandidateUrl(key, candidates), 'https://covers.exo-launcher.local/g1.jpg')
  })
})
