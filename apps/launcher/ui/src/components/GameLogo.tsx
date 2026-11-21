import { useState } from 'react'
import type { Game } from '../lib/host'
import { artSteamAppId } from '../lib/library'

function logoCandidates(appId: string | null): string[] {
  if (!appId) return []
  return [
    `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/logo.png`,
    `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/logo.png`,
  ]
}

export function GameLogo({
  game,
  wrapClass,
  imgClass,
  titleClass,
}: {
  game: Game
  wrapClass: string
  imgClass: string
  titleClass: string
}) {
  const candidates = logoCandidates(artSteamAppId(game))
  const [state, setState] = useState<{ id: string; idx: number }>({ id: game.id, idx: 0 })
  const idx = state.id === game.id ? state.idx : 0
  const src = candidates[idx] ?? null

  if (!src) {
    return (
      <div className={wrapClass}>
        <h1 className={titleClass}>{game.title}</h1>
      </div>
    )
  }

  return (
    <div className={wrapClass}>
      <h1 className="sr-only">{game.title}</h1>
      <img
        key={`${game.id}:${src}`}
        src={src}
        alt={game.title}
        className={imgClass}
        draggable={false}
        decoding="async"
        onError={() => setState({ id: game.id, idx: idx + 1 })}
      />
    </div>
  )
}
