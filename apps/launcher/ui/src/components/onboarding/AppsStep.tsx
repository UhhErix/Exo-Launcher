import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { AppleMusic, Spotify, YouTube } from '../../brand/icons'
import { host } from '../../lib/host'
import { savedMusicService, saveMusicService, type MusicService } from '../../lib/music'
import { report } from '../../lib/report'
import type { Feedback } from './model'
import { StepTile } from './StepTile'

const SERVICES = [
  { id: 'spotify', label: 'Spotify', Icon: Spotify },
  { id: 'apple', label: 'Apple Music', Icon: AppleMusic },
  { id: 'youtube', label: 'YouTube Music', Icon: YouTube },
] as const

export function AppsStep({ onFeedback }: { onFeedback: (feedback: Feedback) => void }) {
  const [service, setService] = useState<MusicService>(savedMusicService)
  const [saving, setSaving] = useState(false)
  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    void host.webClientPreferences().then(
      next => { if (mounted.current && next.musicService) setService(next.musicService) },
      (error: unknown) => report(error, 'AppsStep.webClientPreferences'),
    )
    return () => { mounted.current = false }
  }, [])

  async function choose(next: MusicService) {
    if (next === service || saving) return
    const previous = service
    setService(next)
    setSaving(true)
    try {
      const saved = await host.setWebClientPreferences({ musicService: next })
      saveMusicService(saved.musicService ?? next)
    } catch {
      setService(previous)
      onFeedback({ message: 'Exo couldn’t save the music service. Try again.', error: true })
    } finally {
      setSaving(false)
    }
  }

  const current = SERVICES.some(item => item.id === service) ? service : SERVICES[0].id
  function onServiceKey(event: KeyboardEvent<HTMLDivElement>) {
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
    if (!step || saving) return
    event.preventDefault()
    const index = (SERVICES.findIndex(item => item.id === current) + step + SERVICES.length) % SERVICES.length
    event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="radio"]')[index]?.focus()
    void choose(SERVICES[index].id)
  }

  return (
    <>
      <div className="exo-step-tiles" role="radiogroup" aria-label="Music service" onKeyDown={onServiceKey}>
        {SERVICES.map(({ id, label, Icon }) => (
          <StepTile key={id} icon={<Icon size={22} />} title={label} radio selected={service === id} tabIndex={id === current ? 0 : -1}
            ariaDisabled={saving} onPress={() => void choose(id)} />
        ))}
      </div>

    </>
  )
}
