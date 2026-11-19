import { memo, useEffect, useState } from 'react'
import { host, type WebClientOptions } from '../../lib/host'
import { savedMusicService, saveMusicService, type MusicService } from '../../lib/music'
import { Choice } from './Choice'
import { Row, Section } from './parts'

const SERVICES = [
  { value: 'spotify', label: 'Spotify' },
  { value: 'apple', label: 'Apple Music' },
  { value: 'youtube', label: 'YouTube Music' },
] as const

const LOAD_FAILED = 'Music settings could not be read. Reopen Settings to try again.'

const PERMISSION_CHOICES = [
  { value: 'ask', label: 'Ask' },
  { value: 'allow', label: 'Allow' },
  { value: 'block', label: 'Block' },
] as const

let prepared: WebClientOptions | null = null
let pending: Promise<WebClientOptions> | null = null
function readPreferences(): Promise<WebClientOptions> {
  pending ??= host.webClientPreferences()
    .then(next => { prepared = next; return next })
    .finally(() => { pending = null })
  return pending
}

/** Warmed after the library is usable, so the first open has its rows too. */
export const preloadAppsSection = readPreferences

export const AppsSection = memo(function AppsSection() {
  const [options, setOptions] = useState<WebClientOptions | null>(prepared)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)

  // Read again each time Settings shows (a hidden room's effects stop), which is also the retry after a failed read.
  useEffect(() => {
    let live = true
    void readPreferences().then(
      next => {
        if (!live) return
        setOptions(next)
        setError(current => current?.text === LOAD_FAILED ? null : current)
      },
      () => { if (live) setError({ row: 'service', text: LOAD_FAILED }) },
    )
    return () => { live = false }
  }, [])

  /** `row` is the row whose control saved, so a failure reads under it. */
  async function save(patch: Partial<WebClientOptions>, row: 'service' | 'notifications') {
    setSaving(true)
    setError(null)
    try {
      const next = await host.setWebClientPreferences(patch)
      prepared = next
      setOptions(next)
      if (next.musicService) saveMusicService(next.musicService)
    } catch {
      setError({ row, text: 'The change could not be saved. Try again.' })
    } finally {
      setSaving(false)
    }
  }

  const service: MusicService = options?.musicService ?? savedMusicService()
  const rowError = (row: string) => error?.row === row ? error.text : null

  return (
    <Section id="music">
      <Row label="Music service" labelId="set-music-service" error={rowError('service')}>
        <Choice
          label="Music service"
          value={service}
          choices={SERVICES}
          disabled={saving || !options}
          onChange={value => void save({ musicService: value as MusicService }, 'service')}
        />
      </Row>
      {service === 'youtube' ? (
        <Row label="YouTube Music notifications" labelId="set-yt-notifications" error={rowError('notifications')}>
          <Choice
            label="YouTube Music notifications"
            value={options?.notifications ?? 'ask'}
            choices={PERMISSION_CHOICES}
            disabled={saving || !options}
            onChange={value => void save({ notifications: value as WebClientOptions['notifications'] }, 'notifications')}
          />
        </Row>
      ) : null}
    </Section>
  )
})
